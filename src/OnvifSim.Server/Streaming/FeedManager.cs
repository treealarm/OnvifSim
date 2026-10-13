using System.Collections.Concurrent;
using OnvifSim.Rendering;
using OnvifSim.World;

namespace OnvifSim.Streaming;

// One CameraFeed per camera id, created lazily; also renders one-off snapshots.
public sealed class FeedManager(SceneState scene, ILoggerFactory loggers)
{
    private readonly ConcurrentDictionary<string, CameraFeed> _feeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger = loggers.CreateLogger<CameraFeed>();

    public CameraFeed? Get(string cameraId) =>
        scene.FindCamera(cameraId) == null ? null : _feeds.GetOrAdd(cameraId, id => new CameraFeed(id, scene, _logger));

    public byte[]? Snapshot(string cameraId)
    {
        var snap = scene.Current;
        var cam = snap.World.Cameras.FirstOrDefault(c => string.Equals(c.Id, cameraId, StringComparison.OrdinalIgnoreCase));
        if (cam == null) return null;
        var frame = new Frame(cam.Video.Width, cam.Video.Height);
        new SoftwareRasterizer().Render(snap.Scene, CameraPose.From(cam, snap.Scene.Terrain), frame);
        return JpegEncoder.Encode(frame, cam.Video.Quality);
    }
}
