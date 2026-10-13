using System.Net;
using System.Xml;
using OnvifSim.Ptz;
using OnvifSim.World;

namespace OnvifSim.Onvif;

// What a handler needs to answer one request for one camera. Addresses handed to the client (XAddrs,
// stream and snapshot URIs) use the host and port the client itself addressed, so they stay valid
// behind NAT or Docker port mapping.
public sealed class OnvifRequestContext(
    CameraModel camera, WorldStore store, PtzSimulator ptz, OnvifDispatcher dispatcher, HttpRequest request, int rtspPort)
{
    // The camera as it was when the request arrived.
    public CameraModel Camera => camera;
    public bool HasPtz => camera.Kind == CameraKind.Ptz && camera.Ptz != null;
    public WorldStore Store => store;
    public PtzSimulator Ptz => ptz;
    // The services this camera has: PTZ only on a PTZ camera.
    public IReadOnlyList<OnvifServiceInfo> Services => dispatcher.Services.Where(s => HasPtz || !s.RequiresPtz).ToList();
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
    public string StreamUri => $"rtsp://{HostName}:{rtspPort}/{Uri.EscapeDataString(camera.Id)}";
    public string SnapshotUri => $"http://{Authority}/onvif/snapshot.jpg";

    private static string FormatHost(IPAddress address) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
}
