using System.Net;
using System.Xml;
using OnvifSim.Onvif.Events;
using OnvifSim.Ptz;
using OnvifSim.Recording;
using OnvifSim.World;

namespace OnvifSim.Onvif;

// What a handler needs to answer one request for one camera. Addresses handed to the client (XAddrs,
// stream and snapshot URIs) use the host and port the client itself addressed, so they stay valid
// behind NAT or Docker port mapping.
public sealed class OnvifRequestContext(
    CameraModel camera, WorldStore store, PtzSimulator ptz, PullPoints pullPoints, RecordingLog recordings, OnvifDispatcher dispatcher,
    HttpRequest request, int rtspPort, bool hasH264)
{
    // The camera as it was when the request arrived.
    public CameraModel Camera => camera;
    public bool HasPtz => camera.Kind == CameraKind.Ptz && camera.Ptz != null;
    // Whether the server can encode H.264, which gives every camera its H.264 profile.
    public bool HasH264 => hasH264;
    public WorldStore Store => store;
    public PtzSimulator Ptz => ptz;
    public PullPoints PullPoints => pullPoints;
    public RecordingLog Recordings => recordings;
    // The path the request was posted to: a pull point's address names the subscription.
    public string Path => request.Path.Value ?? "";
    // Ends when the client gives up on the request, for an operation that waits.
    public CancellationToken Aborted => request.HttpContext.RequestAborted;
    // The services this camera has: PTZ only on a PTZ camera.
    public IReadOnlyList<OnvifServiceInfo> Services => dispatcher.Services.Where(s => s.Listed && (HasPtz || !s.RequiresPtz)).ToList();
    public int RtspPort => rtspPort;
    public IPAddress LocalAddress => request.HttpContext.Connection.LocalIpAddress is { } address
        ? address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address
        : IPAddress.Loopback;
    public int LocalPort => request.HttpContext.Connection.LocalPort;

    // host[:port] as the client addressed the camera.
    private string Authority => request.Host.HasValue ? request.Host.Value! : $"{FormatHost(LocalAddress)}:{LocalPort}";
    private string HostName => request.Host.HasValue ? request.Host.Host : FormatHost(LocalAddress);

    // What the service's GetServiceCapabilities returns, for GetServices with IncludeCapability.
    public XmlElement? ServiceCapabilities(string serviceNamespace) => dispatcher.ServiceCapabilities(serviceNamespace, this);

    public string XAddr(string servicePath) => $"http://{Authority}/onvif/{servicePath}";
    // The camera's JPEG stream; its H.264 stream is below it (CameraMedia.StreamUriOf).
    public string StreamUri => $"rtsp://{HostName}:{rtspPort}/{Uri.EscapeDataString(camera.Id)}";
    public string SnapshotUri => $"http://{Authority}/onvif/snapshot.jpg";
    // The camera's recording, as Replay.GetReplayUri hands it out (Streaming.ReplayStreams).
    public string ReplayUri => $"{StreamUri}/replay";

    private static string FormatHost(IPAddress address) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
}
