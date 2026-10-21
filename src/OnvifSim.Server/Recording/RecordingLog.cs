using System.Text.Json;
using OnvifSim.World;

namespace OnvifSim.Recording;

// A stretch of time a camera recorded without a break.
public readonly record struct RecordedSpan(DateTimeOffset From, DateTimeOffset To)
{
    public TimeSpan Duration => To - From;
}

// What each camera has recorded on its own storage, the way a camera with an SD card goes on recording
// while nobody watches and while its network is down. Only when it recorded is kept, never frames: the
// world is a function of time, so a replay draws the past again (ReplayStreams).
//
// Every camera records from the moment it is in the world, unless recording is switched off for it
// (the editor API, or SetRecordingJobMode). The spans survive a restart in recordings.json; the time
// the simulator was not running is a hole, as on a camera that was switched off. The card is finite
// and records round: whatever is older than the camera's RecordingRetentionHours has been recorded over.
public sealed class RecordingLog : IDisposable
{
    // How often recordings.json is brought up to date while cameras record: a crash loses at most this
    // much of every open span.
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    private sealed class Entry
    {
        public bool Enabled = true;
        public List<RecordedSpan> Closed = [];
        public DateTimeOffset? OpenSince;
    }

    // recordings.json: per camera id, whether it records and its spans, an open one ending at the save.
    private sealed record SavedCamera(bool Enabled, List<RecordedSpan> Spans);

    private readonly WorldStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _cameras = new(StringComparer.OrdinalIgnoreCase);
    private readonly ITimer _saveTimer;

    public RecordingLog(SimOptions options, WorldStore store, TimeProvider time, ILogger<RecordingLog> logger)
    {
        _store = store;
        _time = time;
        _logger = logger;
        _path = Path.Combine(options.DataDirectory, "recordings.json");
        Load();
        lock (_gate) Reconcile(store.Current, time.GetUtcNow());
        Save();
        store.Changed += OnWorldChanged;
        _saveTimer = time.CreateTimer(_ => Save(), null, SaveInterval, SaveInterval);
    }

    public DateTimeOffset Now => _time.GetUtcNow();

    // What the camera holds now, oldest first: whole spans inside its retention, the oldest one cut at
    // where the card has been recorded over, an open one ending now.
    public IReadOnlyList<RecordedSpan> SpansOf(CameraModel camera)
    {
        var now = _time.GetUtcNow();
        var earliest = now - TimeSpan.FromHours(camera.RecordingRetentionHours);
        lock (_gate)
        {
            if (!_cameras.TryGetValue(camera.Id, out var entry)) return [];
            IEnumerable<RecordedSpan> all = entry.OpenSince is { } since ? [.. entry.Closed, new RecordedSpan(since, now)] : entry.Closed;
            return all.Where(span => span.To > earliest)
                .Select(span => span.From < earliest ? span with { From = earliest } : span)
                .ToList();
        }
    }

    public bool IsRecording(string cameraId)
    {
        lock (_gate) return _cameras.TryGetValue(cameraId, out var entry) && entry.Enabled;
    }

    // Starts or stops the camera's recording; doing what is already being done changes nothing.
    public void SetRecording(string cameraId, bool recording)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_cameras.TryGetValue(cameraId, out var entry) || entry.Enabled == recording) return;
            entry.Enabled = recording;
            if (recording) entry.OpenSince = now;
            else Close(entry, now);
        }
        _logger.LogInformation("Camera {Camera}: recording {State}", cameraId, recording ? "started" : "stopped");
        Save();
    }

    public void Dispose()
    {
        _store.Changed -= OnWorldChanged;
        _saveTimer.Dispose();
        Save();
    }

    private void OnWorldChanged(WorldModel world)
    {
        lock (_gate) Reconcile(world, _time.GetUtcNow());
    }

    // A camera new to the world starts recording; one gone from it takes its recording with it.
    private void Reconcile(WorldModel world, DateTimeOffset now)
    {
        var ids = world.Cameras.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _cameras.Keys.Where(id => !ids.Contains(id)).ToList()) _cameras.Remove(gone);
        foreach (var id in ids)
        {
            if (!_cameras.TryGetValue(id, out var entry)) _cameras[id] = entry = new Entry();
            if (entry.Enabled && entry.OpenSince == null) entry.OpenSince = now;
        }
    }

    private static void Close(Entry entry, DateTimeOffset now)
    {
        if (entry.OpenSince is { } since && now > since) entry.Closed.Add(new RecordedSpan(since, now));
        entry.OpenSince = null;
    }

    // What a restart finds: every span closed where the last save left it.
    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, SavedCamera>>(File.ReadAllText(_path), WorldStore.Json) ?? [];
            lock (_gate)
                foreach (var (id, camera) in saved)
                    _cameras[id] = new Entry { Enabled = camera.Enabled, Closed = camera.Spans };
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning("{Path} cannot be read ({Reason}); the cameras' recordings start afresh", _path, ex.Message);
        }
    }

    // Also prunes: a span the retention has wholly recorded over is forgotten.
    private void Save()
    {
        var now = _time.GetUtcNow();
        var world = _store.Current;
        Dictionary<string, SavedCamera> saved;
        lock (_gate)
        {
            foreach (var (id, entry) in _cameras)
            {
                var camera = world.Cameras.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
                if (camera == null) continue;
                var earliest = now - TimeSpan.FromHours(camera.RecordingRetentionHours);
                entry.Closed.RemoveAll(span => span.To <= earliest);
            }
            saved = _cameras.ToDictionary(kv => kv.Key, kv => new SavedCamera(kv.Value.Enabled,
                [.. kv.Value.Closed, .. kv.Value.OpenSince is { } since ? [new RecordedSpan(since, now)] : Array.Empty<RecordedSpan>()]));
        }
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(saved, WorldStore.Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException ex)
        {
            // Runs on a timer, where an exception would end the process; the next save tries again.
            _logger.LogError(ex, "Cannot save {Path}", _path);
        }
    }
}
