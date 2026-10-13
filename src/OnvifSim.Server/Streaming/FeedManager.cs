using System.Collections.Concurrent;
using OnvifSim.World;

namespace OnvifSim.Streaming;

// One CameraFeed per camera id, created lazily, and one frame renderer per camera that the feed and the
// snapshots share, so a snapshot of a camera that is streaming costs nothing.
public sealed class FeedManager(SceneState scene, ILoggerFactory loggers)
{
    private readonly ConcurrentDictionary<string, CameraFeed> _feeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachingFrameRenderer> _renderers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger = loggers.CreateLogger<CameraFeed>();

    public CameraFeed? Get(string cameraId) =>
        scene.FindCamera(cameraId) == null ? null : _feeds.GetOrAdd(cameraId, id => new CameraFeed(id, scene, RendererOf(id), _logger));

    public byte[]? Snapshot(string cameraId)
    {
        var snap = scene.Current;
        var cam = snap.World.Cameras.FirstOrDefault(c => string.Equals(c.Id, cameraId, StringComparison.OrdinalIgnoreCase));
        return cam == null ? null : RendererOf(cam.Id).Render(snap, cam);
    }

    public CachingFrameRenderer RendererOf(string cameraId) => _renderers.GetOrAdd(cameraId, _ => new CachingFrameRenderer());
}
