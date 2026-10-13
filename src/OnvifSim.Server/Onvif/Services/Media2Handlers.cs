using OnvifSim.Onvif.Wsdl;
using OnvifSim.Onvif.Wsdl.Media2;
using OnvifSim.Onvif.Soap;
using OnvifSim.World;
using static OnvifSim.Onvif.Services.CameraMedia;

namespace OnvifSim.Onvif.Services;

// Media2 (ver20): the same profile and encoder as Media1 in the Media2 shapes, which OnvifLib and most
// current clients prefer. Audio, metadata and OSDs are empty, as in Media1.
[OnvifService(typeof(Media2), "media2_service", 21, 12)]
public sealed class Media2Handlers(OnvifRequestContext context)
{
    // Media2 stream protocols this camera serves (ONVIF Media2 5.2.4: RtspUnicast, RTSP; RtspOverHttp
    // and RtspMulticast are not offered).
    private static readonly string[] SupportedStreamProtocols = ["RtspUnicast", "RTSP"];

    private CameraModel Camera => context.Camera;

    // Capabilities2, ProfileCapabilities2 and StreamingCapabilities1 are the tr2: types: svcutil numbers
    // classes whose names other namespaces already took.
    public GetServiceCapabilitiesResponse GetServiceCapabilities(GetServiceCapabilitiesRequest request) => new()
    {
        Capabilities = new Capabilities2
        {
            ProfileCapabilities = new ProfileCapabilities2
            {
                MaximumNumberOfProfiles = 1, MaximumNumberOfProfilesSpecified = true,
                ConfigurationsSupported = context.HasPtz ? ["VideoSource", "VideoEncoder", "PTZ"] : ["VideoSource", "VideoEncoder"],
            },
            StreamingCapabilities = new StreamingCapabilities1
            {
                RTSPStreaming = true, RTSPStreamingSpecified = true,
                RTPMulticast = false, RTPMulticastSpecified = true,
                RTP_RTSP_TCP = true, RTP_RTSP_TCPSpecified = true,
                NonAggregateControl = false, NonAggregateControlSpecified = true,
            },
            SnapshotUri = true, SnapshotUriSpecified = true,
            Rotation = false, RotationSpecified = true,
            OSD = false, OSDSpecified = true,
        },
    };

    // Configurations are included only for the types asked for, per Media2: none when Type is absent.
    public GetProfilesResponse GetProfiles(GetProfilesRequest request)
    {
        CheckProfile(request.Token);
        var types = request.Type ?? [];
        bool all = types.Contains("All");
        bool videoSource = all || types.Contains("VideoSource");
        bool videoEncoder = all || types.Contains("VideoEncoder");
        bool ptz = context.HasPtz && (all || types.Contains("PTZ"));
        return new GetProfilesResponse
        {
            Profiles =
            [
                new MediaProfile
                {
                    token = ProfileToken,
                    @fixed = true,
                    fixedSpecified = true,
                    Name = ProfileName,
                    Configurations = videoSource || videoEncoder || ptz
                        ? new ConfigurationSet
                        {
                            VideoSource = videoSource ? VideoSourceConfiguration(Camera) : null,
                            VideoEncoder = videoEncoder ? VideoEncoderConfiguration() : null,
                            PTZ = ptz ? CameraPtz.Configuration() : null,
                        }
                        : null,
                },
            ],
        };
    }

    public GetVideoSourceConfigurationsResponse GetVideoSourceConfigurations(GetVideoSourceConfigurationsRequest request)
    {
        CheckVideoSourceConfiguration(request.ConfigurationToken);
        CheckProfile(request.ProfileToken);
        return new GetVideoSourceConfigurationsResponse { Configurations = [VideoSourceConfiguration(Camera)] };
    }

    public GetVideoEncoderConfigurationsResponse GetVideoEncoderConfigurations(GetVideoEncoderConfigurationsRequest request)
    {
        CheckVideoEncoderConfiguration(request.ConfigurationToken);
        CheckProfile(request.ProfileToken);
        return new GetVideoEncoderConfigurationsResponse { Configurations = [VideoEncoderConfiguration()] };
    }

    public GetVideoEncoderConfigurationOptionsResponse GetVideoEncoderConfigurationOptions(GetVideoEncoderConfigurationOptionsRequest request)
    {
        CheckVideoEncoderConfiguration(request.ConfigurationToken);
        CheckProfile(request.ProfileToken);
        var resolutions = Resolutions(Camera);
        var largest = resolutions.MaxBy(r => r.Width * r.Height);
        return new GetVideoEncoderConfigurationOptionsResponse
        {
            Options =
            [
                new VideoEncoder2ConfigurationOptions
                {
                    Encoding = "JPEG",
                    QualityRange = new FloatRange { Min = MinQuality, Max = MaxQuality },
                    ResolutionsAvailable = resolutions.Select(r => new VideoResolution2 { Width = r.Width, Height = r.Height }).ToArray(),
                    BitrateRange = new IntRange
                    {
                        Min = 64,
                        Max = EstimatedBitrateKbps(new VideoModel { Width = largest.Width, Height = largest.Height, Fps = MaxFrameRate }),
                    },
                    FrameRatesSupported = FrameRates,
                },
            ],
        };
    }

    public SetVideoEncoderConfigurationResponse SetVideoEncoderConfiguration(SetVideoEncoderConfigurationRequest request)
    {
        var configuration = request.Configuration;
        if (configuration?.Resolution == null)
            throw OnvifFault.InvalidArgValue("The configuration needs a resolution.");
        float frameRate = configuration.RateControl?.FrameRateLimit ?? Camera.Video.Fps;
        ApplyEncoderSettings(context, configuration.token, configuration.Encoding ?? "",
            configuration.Resolution.Width, configuration.Resolution.Height, frameRate, configuration.Quality);
        return new SetVideoEncoderConfigurationResponse();
    }

    public GetStreamUriResponse GetStreamUri(GetStreamUriRequest request)
    {
        CheckProfile(request.ProfileToken);
        if (!SupportedStreamProtocols.Contains(request.Protocol, StringComparer.OrdinalIgnoreCase))
            throw OnvifFault.InvalidArgValue($"Stream protocol '{request.Protocol}' is not supported; use {string.Join(" or ", SupportedStreamProtocols)}.");
        return new GetStreamUriResponse { Uri = context.StreamUri };
    }

    public GetSnapshotUriResponse GetSnapshotUri(GetSnapshotUriRequest request)
    {
        CheckProfile(request.ProfileToken);
        return new GetSnapshotUriResponse { Uri = context.SnapshotUri };
    }

    public GetAudioEncoderConfigurationsResponse GetAudioEncoderConfigurations(GetAudioEncoderConfigurationsRequest request) =>
        new() { Configurations = [] };

    public GetMetadataConfigurationsResponse GetMetadataConfigurations(GetMetadataConfigurationsRequest request) =>
        new() { Configurations = [] };

    public GetAnalyticsConfigurationsResponse GetAnalyticsConfigurations(GetAnalyticsConfigurationsRequest request) =>
        new() { Configurations = [] };

    public GetOSDsResponse GetOSDs(GetOSDsRequest request) => new() { OSDs = [] };

    public GetOSDOptionsResponse GetOSDOptions(GetOSDOptionsRequest request)
    {
        CheckVideoSourceConfiguration(request.ConfigurationToken);
        return new GetOSDOptionsResponse { OSDOptions = NoOsdOptions() };
    }

    private VideoEncoder2Configuration VideoEncoderConfiguration() => new()
    {
        token = VideoEncoderConfigurationToken,
        Name = "VideoEncoder",
        UseCount = 1,
        Encoding = "JPEG",
        Resolution = new VideoResolution2 { Width = Camera.Video.Width, Height = Camera.Video.Height },
        Quality = Camera.Video.Quality,
        RateControl = new VideoRateControl2
        {
            FrameRateLimit = Camera.Video.Fps,
            BitrateLimit = EstimatedBitrateKbps(Camera.Video),
            ConstantBitRate = false, ConstantBitRateSpecified = true,
        },
        Multicast = NoMulticast(),
    };
}
