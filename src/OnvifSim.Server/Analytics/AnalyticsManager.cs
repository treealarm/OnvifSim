using System.Collections.Concurrent;
using OnvifSim.Ptz;
using OnvifSim.World;

namespace OnvifSim.Analytics;

// The analytics of every camera, created lazily by camera id.
public sealed class AnalyticsManager(SceneState scene, PtzSimulator ptz, TimeProvider time, ILoggerFactory loggers)
{
    private readonly ConcurrentDictionary<string, CameraAnalytics> _cameras = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger = loggers.CreateLogger<CameraAnalytics>();

    public CameraAnalytics? Get(string cameraId) =>
        scene.FindCamera(cameraId) == null ? null : _cameras.GetOrAdd(cameraId, id => new CameraAnalytics(id, scene, ptz, time, _logger));
}
