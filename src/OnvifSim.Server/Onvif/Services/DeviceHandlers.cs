using HostInterface = System.Net.NetworkInformation.NetworkInterface;
using System.Net.Sockets;
using OnvifSim.Onvif.Wsdl;
using OnvifSim.Onvif.Wsdl.Device;
using OnvifSim.Onvif.Soap;
using OnvifSim.Rendering;
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

    // Where the camera is on the Earth. The world has one geo anchor (Geo), so the answer is its pose
    // through that frame; the camera on a vehicle follows the vehicle, and its location changes with
    // the clock. Fixed cameras can be moved by SetGeoLocation; a vehicle-mounted one cannot.
    public GetGeoLocationResponse GetGeoLocation(GetGeoLocationRequest request)
    {
        var world = context.Store.Current;
        var pose = CameraPose.From(Camera, new Terrain(world.Terrain));
        var geo = WorldGeo.ToGeo(world, new Vec3(pose.Position.X, pose.Position.Y, pose.Position.Z));
        return new GetGeoLocationResponse
        {
            Location =
            [
                new LocationEntity
                {
                    Entity = "Device",
                    Token = Camera.Id,
                    Fixed = true,
                    FixedSpecified = true,
                    GeoLocation = new GeoLocation
                    {
                        lon = geo.Longitude, lonSpecified = true,
                        lat = geo.Latitude, latSpecified = true,
                        elevation = (float)geo.Elevation, elevationSpecified = true,
                    },
                    GeoOrientation = new GeoOrientation
                    {
                        yaw = WorldGeo.TrueHeading(world, pose.Heading), yawSpecified = true,
                        pitch = pose.Pitch, pitchSpecified = true,
                        roll = pose.Roll, rollSpecified = true,
                    },
                },
            ],
        };
    }

    // The request names the camera's place on the Earth. It is converted into the world's metres and
    // the fixed camera is moved there, provided that place is inside the terrain and not below its
    // ground; the world's geo anchor itself never moves. The orientation in the request is ignored:
    // the camera's mount sets where it looks.
    public SetGeoLocationResponse SetGeoLocation(SetGeoLocationRequest request)
    {
        var location = request.Location?.FirstOrDefault(l => string.Equals(l.Entity, "Device", StringComparison.OrdinalIgnoreCase))
            ?? request.Location?.FirstOrDefault();
        var geo = location?.GeoLocation
            ?? throw OnvifFault.InvalidArgValue("SetGeoLocation needs a Device location.");
        if (!geo.latSpecified || !geo.lonSpecified)
            throw OnvifFault.InvalidArgValue("SetGeoLocation needs both a latitude and a longitude.");
        if (geo.lat is < -90 or > 90 || geo.lon is < -180 or > 180)
            throw OnvifFault.InvalidArgValue($"Latitude {geo.lat} / longitude {geo.lon} is outside the Earth.");

        var world = context.Store.Current;
        var currentEye = CameraPose.From(Camera, new Terrain(world.Terrain)).Position.Y;
        var target = WorldGeo.ToWorld(world, geo.lat, geo.lon, geo.elevationSpecified ? geo.elevation : currentEye);
        var half = world.Terrain.Size / 2;
        if (Math.Abs(target.X) > half || Math.Abs(target.Z) > half)
            throw OnvifFault.InvalidArgValue("The requested location is outside the world.");
        if (target.Y < new Terrain(world.Terrain).HeightAt(target.X, target.Z))
            throw OnvifFault.InvalidArgValue("The requested location is below the ground.");

        context.Store.Update(w =>
        {
            var camera = w.Cameras.FirstOrDefault(c => string.Equals(c.Id, Camera.Id, StringComparison.OrdinalIgnoreCase))
                ?? throw OnvifFault.InvalidArgValue($"No camera with id '{Camera.Id}'.");
            camera.Position = new Vec3(
                target.X,
                target.Y - new Terrain(w.Terrain).HeightAt(target.X, target.Z),
                target.Z);
        });
        return new SetGeoLocationResponse();
    }

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
                GeoLocationEntries = 1, GeoLocationEntriesSpecified = true,
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
            ? [CapabilityCategory.All, CapabilityCategory.Device, CapabilityCategory.Events, CapabilityCategory.Media, CapabilityCategory.Imaging, CapabilityCategory.PTZ]
            : [CapabilityCategory.All, CapabilityCategory.Device, CapabilityCategory.Events, CapabilityCategory.Media, CapabilityCategory.Imaging];
        var unsupported = categories.Except(offered).ToList();
        if (unsupported.Count > 0)
            throw OnvifFault.NoSuchService($"The device has no {string.Join(", ", unsupported)} service.");

        bool Wants(CapabilityCategory category) => categories.Contains(CapabilityCategory.All) || categories.Contains(category);
        return new GetCapabilitiesResponse
        {
            Capabilities = new Capabilities
            {
                Device = Wants(CapabilityCategory.Device) ? DeviceCapabilities() : null,
                Events = Wants(CapabilityCategory.Events)
                    ? new EventCapabilities
                    {
                        XAddr = context.XAddr("event_service"),
                        WSPullPointSupport = true,
                        WSSubscriptionPolicySupport = false,
                        WSPausableSubscriptionManagerInterfaceSupport = false,
                    }
                    : null,
                Media = Wants(CapabilityCategory.Media) ? MediaCapabilities() : null,
                Imaging = Wants(CapabilityCategory.Imaging) ? new ImagingCapabilities { XAddr = context.XAddr("imaging_service") } : null,
                PTZ = context.HasPtz && Wants(CapabilityCategory.PTZ) ? new PTZCapabilities { XAddr = context.XAddr("ptz_service") } : null,
                // Profile G's services have no category of their own; All brings them along.
                Extension = Wants(CapabilityCategory.All)
                    ? new CapabilitiesExtension
                    {
                        Recording = new RecordingCapabilities { XAddr = context.XAddr("recording_service"), MediaProfileSource = true },
                        Search = new SearchCapabilities { XAddr = context.XAddr("search_service"), MetadataSearch = false },
                        Replay = new ReplayCapabilities { XAddr = context.XAddr("replay_service") },
                    }
                    : null,
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
