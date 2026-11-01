using System.Collections.Concurrent;
using OnvifSim.Ptz;
using OnvifSim.Streaming.H264;
using OnvifSim.World;

namespace OnvifSim.Streaming;

// The feeds of every camera stream, created lazily: one JPEG feed and, when ffmpeg is available, one
// H.264 feed per stream. A camera's snapshot and its first JPEG stream share one frame renderer, so a
// snapshot of a camera that is streaming costs nothing.
public sealed class FeedManager
{
    private sealed record FeedEntry(CameraFeed Feed, string CameraId, string StreamToken);
    private sealed record H264FeedEntry(H264Feed Feed, string CameraId, string StreamToken);

    private readonly SceneState _scene;
    private readonly PtzSimulator _ptz;
    private readonly H264Support _h264;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, FeedEntry> _feeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, H264FeedEntry> _h264Feeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachingFrameRenderer> _renderers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;

    public FeedManager(SceneState scene, PtzSimulator ptz, H264Support h264, TimeProvider time, WorldStore store, ILoggerFactory loggers)
    {
        _scene = scene;
        _ptz = ptz;
        _h264 = h264;
        _time = time;
        _logger = loggers.CreateLogger<CameraFeed>();
        store.Changed += Prune;
    }

    public bool H264Available => _h264.IsAvailable;

    public CameraFeed? Get(string cameraId, string streamToken)
    {
        if (FindStream(cameraId, streamToken, StreamCodec.Jpeg) == null) return null;
        var key = KeyOf(cameraId, streamToken);
        return _feeds.GetOrAdd(key, _ => new FeedEntry(
            new CameraFeed(cameraId, streamToken, _scene, RendererOf(cameraId, streamToken), _ptz, _logger), cameraId, streamToken)).Feed;
    }

    public H264Feed? GetH264(string cameraId, string streamToken)
    {
        if (_h264.FfmpegPath is not { } ffmpeg || FindStream(cameraId, streamToken, StreamCodec.H264) == null) return null;
        var key = KeyOf(cameraId, streamToken);
        return _h264Feeds.GetOrAdd(key, _ => new H264FeedEntry(
            new H264Feed(cameraId, streamToken, _scene, _ptz, new CachingFrameRenderer(_time, _ptz.StateOf), ffmpeg, _logger),
            cameraId, streamToken)).Feed;
    }

    // The camera's snapshot is its first JPEG stream, the one its snapshot and MJPEG preview show.
    public byte[]? Snapshot(string cameraId)
    {
        var snap = _scene.Current;
        var camera = FindCamera(snap.World, cameraId);
        var stream = camera == null ? null : FirstJpegStream(camera);
        return camera == null || stream == null
            ? null
            : RendererOf(camera.Id, stream.Token).Render(snap, camera, _ptz.PositionOf(camera), CameraStreamsModel.VideoOf(camera, stream));
    }

    // The Motion JPEG feed of the camera's first JPEG stream, for its preview.
    public CameraFeed? Preview(string cameraId)
    {
        var camera = FindCamera(_scene.Current.World, cameraId);
        var stream = camera == null ? null : FirstJpegStream(camera);
        return camera == null || stream == null ? null : Get(camera.Id, stream.Token);
    }

    public CachingFrameRenderer RendererOf(string cameraId, string streamToken) =>
        _renderers.GetOrAdd(KeyOf(cameraId, streamToken), _ => new CachingFrameRenderer(_time, _ptz.StateOf));

    // A stream that leaves the world takes its feed with it; a stream added again under the same token
    // starts a fresh one instead of finding the old loop asleep.
    private void Prune(WorldModel world)
    {
        foreach (var (key, entry) in _feeds)
            if (FindStream(world, entry.CameraId, entry.StreamToken, StreamCodec.Jpeg) == null) _feeds.TryRemove(key, out _);
        foreach (var (key, entry) in _h264Feeds)
            if (FindStream(world, entry.CameraId, entry.StreamToken, StreamCodec.H264) == null) _h264Feeds.TryRemove(key, out _);
        var used = world.Cameras
            .SelectMany(camera => CameraStreamsModel.Effective(camera).Select(stream => KeyOf(camera.Id, stream.Token)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _renderers.Keys)
            if (!used.Contains(key)) _renderers.TryRemove(key, out _);
    }

    private StreamModel? FindStream(string cameraId, string streamToken, StreamCodec codec) =>
        FindStream(_scene.Current.World, cameraId, streamToken, codec);

    private static StreamModel? FindStream(WorldModel world, string cameraId, string streamToken, StreamCodec codec)
    {
        var camera = FindCamera(world, cameraId);
        var stream = camera == null ? null : CameraStreamsModel.Find(camera, streamToken);
        return stream != null && stream.Codec == codec ? stream : null;
    }

    private static StreamModel? FirstJpegStream(CameraModel camera) =>
        CameraStreamsModel.Effective(camera).FirstOrDefault(stream => stream.Codec == StreamCodec.Jpeg);

    private static CameraModel? FindCamera(WorldModel world, string cameraId) =>
        world.Cameras.FirstOrDefault(c => string.Equals(c.Id, cameraId, StringComparison.OrdinalIgnoreCase));

    private static string KeyOf(string cameraId, string streamToken) => $"{cameraId}|{streamToken}";
}
