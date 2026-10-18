using System.Collections.Concurrent;
using OnvifSim.Ptz;
using OnvifSim.Streaming.H264;
using OnvifSim.World;

namespace OnvifSim.Streaming;

public enum VideoCodec { Jpeg, H264 }

// The feeds of every camera, created lazily: one JPEG feed and, when ffmpeg is available, one H.264 feed
// per camera id. The JPEG feed and the snapshots share one frame renderer, so a snapshot of a camera
// that is streaming costs nothing.
public sealed class FeedManager(SceneState scene, PtzSimulator ptz, H264Support h264, TimeProvider time, ILoggerFactory loggers)
{
    private readonly ConcurrentDictionary<string, CameraFeed> _feeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, H264Feed> _h264Feeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachingFrameRenderer> _renderers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger = loggers.CreateLogger<CameraFeed>();

    public bool H264Available => h264.IsAvailable;

    public CameraFeed? Get(string cameraId) =>
        scene.FindCamera(cameraId) == null ? null : _feeds.GetOrAdd(cameraId, id => new CameraFeed(id, scene, RendererOf(id), ptz, _logger));

    public H264Feed? GetH264(string cameraId) =>
        h264.FfmpegPath is not { } ffmpeg || scene.FindCamera(cameraId) == null
            ? null
            : _h264Feeds.GetOrAdd(cameraId, id => new H264Feed(id, scene, ptz, new CachingFrameRenderer(time, ptz.StateOf), ffmpeg, _logger));

    public byte[]? Snapshot(string cameraId)
    {
        var snap = scene.Current;
        var cam = snap.World.Cameras.FirstOrDefault(c => string.Equals(c.Id, cameraId, StringComparison.OrdinalIgnoreCase));
        return cam == null ? null : RendererOf(cam.Id).Render(snap, cam, ptz.PositionOf(cam));
    }

    public CachingFrameRenderer RendererOf(string cameraId) => _renderers.GetOrAdd(cameraId, _ => new CachingFrameRenderer(time, ptz.StateOf));
}
