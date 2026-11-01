namespace OnvifSim.Streaming;

// The RTSP listener port each camera's streams are served on, published by RtspHost for the streams
// themselves and the editor. A camera is reachable only on its own port, even when paths are unique.
public sealed class RtspEndpoints
{
    private volatile IReadOnlyDictionary<string, int> _ports = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public void Set(IReadOnlyDictionary<string, int> ports) =>
        _ports = new Dictionary<string, int>(ports, StringComparer.OrdinalIgnoreCase);

    public int? PortOf(string cameraId) => _ports.TryGetValue(cameraId, out var port) ? port : null;
}
