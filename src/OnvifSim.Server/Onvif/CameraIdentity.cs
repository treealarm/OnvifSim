using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using OnvifSim.World;

namespace OnvifSim.Onvif;

// How a simulated camera presents itself, the same in GetDeviceInformation, GetScopes and WS-Discovery.
// Everything derives from the camera id, so it survives restarts without being stored.
public static class CameraIdentity
{
    public const string Manufacturer = "OnvifSim";

    // "0.1.0": the informational version without the "+commit" suffix the SDK appends.
    public static readonly string FirmwareVersion =
        (typeof(CameraIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0").Split('+')[0];

    public static string Model(CameraModel camera) => camera.Kind == CameraKind.Ptz ? "Simulated PTZ camera" : "Simulated fixed camera";
    public static string HardwareId(CameraModel camera) => camera.Kind == CameraKind.Ptz ? "OnvifSim-PTZ" : "OnvifSim-Fixed";
    public static string Hostname(CameraModel camera) => $"onvifsim-{camera.Id}";

    // WS-Discovery endpoint reference: a name-based UUID, stable for a camera id.
    public static string EndpointReference(string cameraId)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("onvifsim:" + cameraId));
        hash[6] = (byte)(hash[6] & 0x0F | 0x30); // version 3 (name-based, MD5)
        hash[8] = (byte)(hash[8] & 0x3F | 0x80); // RFC 4122 variant
        return "urn:uuid:" + new Guid(hash, bigEndian: true);
    }

    // A locally administered MAC address, stable for a camera id.
    public static string HardwareAddress(CameraModel camera)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("onvifsim-mac:" + camera.Id));
        hash[0] = (byte)(hash[0] & 0xFC | 0x02);
        return string.Join(":", hash.Take(6).Select(b => b.ToString("x2")));
    }

    // All fixed: the simulator offers nothing to configure here.
    public static IReadOnlyList<string> Scopes(CameraModel camera) =>
    [
        "onvif://www.onvif.org/type/video_encoder",
        "onvif://www.onvif.org/type/Network_Video_Transmitter",
        "onvif://www.onvif.org/Profile/Streaming",
        $"onvif://www.onvif.org/hardware/{HardwareId(camera)}",
        $"onvif://www.onvif.org/name/{Uri.EscapeDataString(camera.Name.Length > 0 ? camera.Name : camera.Id)}",
        "onvif://www.onvif.org/location/OnvifSim",
    ];
}
