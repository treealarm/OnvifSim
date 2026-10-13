using System.Threading.Channels;
using OnvifSim.Ptz;
using OnvifSim.World;

namespace OnvifSim.Analytics;

// One look of a camera's analytics: the people in its picture with their object ids, and its PTZ head
// (null for a fixed camera). Camera is the camera's configuration the look was taken with.
public sealed record AnalyticsFrame(DateTimeOffset UtcTime, CameraModel Camera, IReadOnlyList<TrackedObject> Objects, PtzState? Ptz);

// Runs one camera's analytics only while somebody wants it, like CameraFeed renders only while somebody
// watches: the first subscriber starts a look every Interval, the last one leaving stops it. Everyone
// shares the looks, so the metadata stream, the events and the editor agree on the object ids.
public sealed class CameraAnalytics(string cameraId, SceneState scene, PtzSimulator ptz, TimeProvider time, ILogger logger)
{
    // Five looks a second, as cameras send their boxes.
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    private readonly Lock _gate = new();
    private readonly List<Channel<AnalyticsFrame>> _subscribers = [];
    private CancellationTokenSource? _cts;

    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<AnalyticsFrame>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true,
        });
        lock (_gate)
        {
            _subscribers.Add(channel);
            if (_cts == null)
            {
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _ = Task.Run(() => RunAsync(token), CancellationToken.None);
            }
        }
        return new Subscription(this, channel);
    }

    private void Unsubscribe(Channel<AnalyticsFrame> channel)
    {
        lock (_gate)
        {
            if (!_subscribers.Remove(channel)) return;
            channel.Writer.TryComplete();
            if (_subscribers.Count == 0)
            {
                _cts?.Cancel();
                _cts = null;
            }
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        var tracker = new ObjectTracker();
        try
        {
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                var snapshot = scene.Current;
                var camera = snapshot.World.Cameras.FirstOrDefault(c => string.Equals(c.Id, cameraId, StringComparison.OrdinalIgnoreCase));
                if (camera == null)
                {
                    CompleteAll();
                    return;
                }
                var frame = Look(snapshot, camera, tracker);
                lock (_gate)
                    foreach (var s in _subscribers)
                        s.Writer.TryWrite(frame);
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Camera {Camera}: analytics failed", cameraId);
            CompleteAll();
        }
    }

    private AnalyticsFrame Look(SceneState.Snapshot snapshot, CameraModel camera, ObjectTracker tracker)
    {
        var now = time.GetUtcNow();
        PtzState? head = camera.Kind == CameraKind.Ptz && camera.Ptz != null ? ptz.StateOf(camera) : null;
        var sightings = camera.Metadata.Analytics
            ? PersonDetector.Detect(snapshot, camera, head?.Position ?? default, now)
            : [];
        return new AnalyticsFrame(now, camera, tracker.Update(sightings), head);
    }

    private void CompleteAll()
    {
        lock (_gate)
        {
            foreach (var s in _subscribers) s.Writer.TryComplete();
            _subscribers.Clear();
            _cts?.Cancel();
            _cts = null;
        }
    }

    public sealed class Subscription(CameraAnalytics analytics, Channel<AnalyticsFrame> channel) : IDisposable
    {
        public ChannelReader<AnalyticsFrame> Frames => channel.Reader;
        public void Dispose() => analytics.Unsubscribe(channel);
    }
}
