using HostInterface = System.Net.NetworkInformation.NetworkInterface;
using System.Net.Sockets;
using OnvifSim.Onvif.Wsdl;
using OnvifSim.Onvif.Wsdl.Device;
using OnvifSim.Onvif.Soap;
using OnvifSim.World;

namespace OnvifSim.Onvif.Services;

// Device management (ONVIF Core): identity, clock, services and capabilities, and a read-only view of
// network settings. The simulated network is the address the request arrived on; nothing here can be
// reconfigured, so setters only accept the current values.
[OnvifService(typeof(Device), "device_service", 21, 12)]
public sealed class DeviceHandlers(OnvifRequestContext context)
{
    private CameraModel Camera => context.Camera;

    public GetDeviceInformationResponse GetDeviceInformation(GetDeviceInformationRequest request) => new()
    {
        Manufacturer = CameraIdentity.Manufacturer,
        Model = CameraIdentity.Model(Camera),
        FirmwareVersion = CameraIdentity.FirmwareVersion,
        SerialNumber = Camera.Id,
        HardwareId = CameraIdentity.HardwareId(Camera),
    };

    // The simulator's clock is the host's, kept by NTP and reported as UTC.
    [PreAuth]
    public GetSystemDateAndTimeResponse GetSystemDateAndTime(GetSystemDateAndTimeRequest request)
    {
        var now = System.DateTime.UtcNow;
        return new GetSystemDateAndTimeResponse
        {
            SystemDateAndTime = new SystemDateTime
            {
                DateTimeType = SetDateTimeType.NTP,
                DaylightSavings = false,
                TimeZone = new OnvifSim.Onvif.Wsdl.TimeZone { TZ = "UTC0" },
                UTCDateTime = ToOnvif(now),
                LocalDateTime = ToOnvif(now),
            },
        };
    }

    [PreAuth]
    public GetServicesResponse GetServices(GetServicesRequest request) => new()
    {
        Service = context.Services.Select(s => new Service
        {
            Namespace = s.Namespace,
            XAddr = context.XAddr(s.Path),
            Version = new OnvifVersion { Major = s.Major, Minor = s.Minor },
            Capabilities = request.IncludeCapability ? context.ServiceCapabilities(s.Namespace) : null,
        }).ToArray(),
    };

    public GetServiceCapabilitiesResponse GetServiceCapabilities(GetServiceCapabilitiesRequest request) => new()
    {
        Capabilities = new DeviceServiceCapabilities
        {
            Network = new NetworkCapabilities
            {
                IPFilter = false, IPFilterSpecified = true,
                ZeroConfiguration = false, ZeroConfigurationSpecified = true,
                IPVersion6 = false, IPVersion6Specified = true,
                DynDNS = false, DynDNSSpecified = true,
            },
            Security = new SecurityCapabilities
            {
                UsernameToken = true, UsernameTokenSpecified = true,
                HttpDigest = true, HttpDigestSpecified = true,
            },
            SystemMember = new SystemCapabilities
            {
                DiscoveryResolve = false, DiscoveryResolveSpecified = true,
                DiscoveryBye = true, DiscoveryByeSpecified = true,
                RemoteDiscovery = false, RemoteDiscoverySpecified = true,
                SystemBackup = false, SystemBackupSpecified = true,
                SystemLogging = false, SystemLoggingSpecified = true,
                HttpFirmwareUpgrade = false, HttpFirmwareUpgradeSpecified = true,
                StorageConfiguration = false, StorageConfigurationSpecified = true,
            },
            Misc = new MiscCapabilities(),
        },
    };

    // The legacy (ONVIF 1.x) way to find services; Media here is Media1.
    [PreAuth]
    public GetCapabilitiesResponse GetCapabilities(GetCapabilitiesRequest request)
    {
        var categories = request.Category is { Length: > 0 } c ? c : [CapabilityCategory.All];
        CapabilityCategory[] offered = context.HasPtz
            ? [CapabilityCategory.All, CapabilityCategory.Device, CapabilityCategory.Media, CapabilityCategory.Imaging, CapabilityCategory.PTZ]
            : [CapabilityCategory.All, CapabilityCategory.Device, CapabilityCategory.Media, CapabilityCategory.Imaging];
        var unsupported = categories.Except(offered).ToList();
        if (unsupported.Count > 0)
            throw OnvifFault.NoSuchService($"The device has no {string.Join(", ", unsupported)} service.");

        bool Wants(CapabilityCategory category) => categories.Contains(CapabilityCategory.All) || categories.Contains(category);
        return new GetCapabilitiesResponse
        {
            Capabilities = new Capabilities
            {
                Device = Wants(CapabilityCategory.Device) ? DeviceCapabilities() : null,
                Media = Wants(CapabilityCategory.Media) ? MediaCapabilities() : null,
                Imaging = Wants(CapabilityCategory.Imaging) ? new ImagingCapabilities { XAddr = context.XAddr("imaging_service") } : null,
                PTZ = context.HasPtz && Wants(CapabilityCategory.PTZ) ? new PTZCapabilities { XAddr = context.XAddr("ptz_service") } : null,
            },
        };
    }

    public GetScopesResponse GetScopes(GetScopesRequest request) => new()
    {
        Scopes = CameraIdentity.Scopes(Camera).Select(s => new Scope { ScopeDef = ScopeDefinition.Fixed, ScopeItem = s }).ToArray(),
    };

    public GetEndpointReferenceResponse GetEndpointReference(GetEndpointReferenceRequest request) => new()
    {
        GUID = CameraIdentity.EndpointReference(Camera.Id),
    };

    [PreAuth]
    public GetHostnameResponse GetHostname(GetHostnameRequest request) =>
        new() { HostnameInformation = new HostnameInformation { FromDHCP = false, Name = CameraIdentity.Hostname(Camera) } };

    public SetHostnameResponse SetHostname(SetHostnameRequest request) =>
        request.Name == CameraIdentity.Hostname(Camera)
            ? new SetHostnameResponse()
            : throw OnvifFault.ConfigModify("The hostname of a simulated camera is fixed.");

    public GetUsersResponse GetUsers(GetUsersRequest request) => new()
    {
        User = Camera.Users.Select(u => new User { Username = u.Username, UserLevel = UserLevel.Administrator }).ToArray(),
    };

    public GetNetworkInterfacesResponse GetNetworkInterfaces(GetNetworkInterfacesRequest request)
    {
        var address = context.LocalAddress;
        bool ipv4 = address.AddressFamily == AddressFamily.InterNetwork;
        return new GetNetworkInterfacesResponse
        {
            NetworkInterfaces =
            [
                new NetworkInterface
                {
                    token = "eth0",
                    Enabled = true,
                    Info = new NetworkInterfaceInfo { Name = "eth0", HwAddress = CameraIdentity.HardwareAddress(Camera), MTU = 1500, MTUSpecified = true },
                    IPv4 = new IPv4NetworkInterface
                    {
                        Enabled = ipv4,
                        Config = new IPv4Configuration
                        {
                            DHCP = false,
                            Manual = ipv4 ? [new PrefixedIPv4Address { Address = address.ToString(), PrefixLength = PrefixLengthOf(address) }] : [],
                        },
                    },
                },
            ],
        };
    }

    public GetNetworkProtocolsResponse GetNetworkProtocols(GetNetworkProtocolsRequest request) => new()
    {
        NetworkProtocols =
        [
            new NetworkProtocol { Name = NetworkProtocolType.HTTP, Enabled = true, Port = [context.LocalPort] },
            new NetworkProtocol { Name = NetworkProtocolType.RTSP, Enabled = true, Port = [context.RtspPort] },
        ],
    };

    public GetDNSResponse GetDNS(GetDNSRequest request) => new() { DNSInformation = new DNSInformation { FromDHCP = false } };

    public GetNTPResponse GetNTP(GetNTPRequest request) => new() { NTPInformation = new NTPInformation { FromDHCP = false } };

    public GetNetworkDefaultGatewayResponse GetNetworkDefaultGateway(GetNetworkDefaultGatewayRequest request) =>
        new() { NetworkGateway = new NetworkGateway() };

    public GetStorageConfigurationsResponse GetStorageConfigurations(GetStorageConfigurationsRequest request) =>
        new() { StorageConfigurations = [] };

    public GetRelayOutputsResponse GetRelayOutputs(GetRelayOutputsRequest request) => new() { RelayOutputs = [] };

    private DeviceCapabilities DeviceCapabilities() => new()
    {
        XAddr = context.XAddr("device_service"),
        Network = new NetworkCapabilities1 { IPFilter = false, IPFilterSpecified = true, ZeroConfiguration = false, ZeroConfigurationSpecified = true },
        SystemMember = new SystemCapabilities1
        {
            DiscoveryBye = true,
            SupportedVersions = [new OnvifVersion { Major = 21, Minor = 12 }],
        },
        Security = new SecurityCapabilities1(),
    };

    private MediaCapabilities MediaCapabilities() => new()
    {
        XAddr = context.XAddr("media_service"),
        StreamingCapabilities = new RealTimeStreamingCapabilities
        {
            RTPMulticast = false, RTPMulticastSpecified = true,
            RTP_TCP = true, RTP_TCPSpecified = true,
            RTP_RTSP_TCP = true, RTP_RTSP_TCPSpecified = true,
        },
    };

    // The prefix of the host interface that owns the address; /24 if none does (e.g. behind NAT).
    private static int PrefixLengthOf(System.Net.IPAddress address) =>
        HostInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .FirstOrDefault(a => a.Address.Equals(address))?.PrefixLength ?? 24;

    private static OnvifSim.Onvif.Wsdl.DateTime ToOnvif(System.DateTime utc) => new()
    {
        Date = new Date { Year = utc.Year, Month = utc.Month, Day = utc.Day },
        Time = new Time { Hour = utc.Hour, Minute = utc.Minute, Second = utc.Second },
    };
}
