using OnvifSim.Onvif.Wsdl;
using OnvifSim.Onvif.Wsdl.Media2;
using OnvifSim.Onvif.Soap;
using OnvifSim.Streaming;
using OnvifSim.World;
using static OnvifSim.Onvif.Services.CameraMedia;

namespace OnvifSim.Onvif.Services;

// Media2 (ver20): the same profiles and encoders as Media1 in the Media2 shapes, which OnvifLib and most
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
                MaximumNumberOfProfiles = Codecs(context).Count, MaximumNumberOfProfilesSpecified = true,
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
        var codecs = string.IsNullOrEmpty(request.Token) ? Codecs(context) : [CodecOfProfile(context, request.Token)];
        var types = request.Type ?? [];
        bool all = types.Contains("All");
        bool videoSource = all || types.Contains("VideoSource");
        bool videoEncoder = all || types.Contains("VideoEncoder");
        bool ptz = context.HasPtz && (all || types.Contains("PTZ"));
        return new GetProfilesResponse
        {
            Profiles = codecs.Select(codec => new MediaProfile
            {
                token = ProfileTokenOf(codec),
                @fixed = true,
                fixedSpecified = true,
                Name = ProfileNameOf(codec),
                Configurations = videoSource || videoEncoder || ptz
                    ? new ConfigurationSet
                    {
                        VideoSource = videoSource ? VideoSourceConfiguration(context) : null,
                        VideoEncoder = videoEncoder ? VideoEncoderConfiguration(codec) : null,
                        PTZ = ptz ? CameraPtz.Configuration() : null,
                    }
                    : null,
            }).ToArray(),
        };
    }

    public GetVideoSourceConfigurationsResponse GetVideoSourceConfigurations(GetVideoSourceConfigurationsRequest request)
    {
        CheckVideoSourceConfiguration(request.ConfigurationToken);
        CheckProfile(context, request.ProfileToken);
        return new GetVideoSourceConfigurationsResponse { Configurations = [VideoSourceConfiguration(context)] };
    }

    public GetVideoEncoderConfigurationsResponse GetVideoEncoderConfigurations(GetVideoEncoderConfigurationsRequest request) => new()
    {
        Configurations = SelectCodecs(context, request.ConfigurationToken, request.ProfileToken).Select(VideoEncoderConfiguration).ToArray(),
    };

    public GetVideoEncoderConfigurationOptionsResponse GetVideoEncoderConfigurationOptions(GetVideoEncoderConfigurationOptionsRequest request) => new()
    {
        Options = SelectCodecs(context, request.ConfigurationToken, request.ProfileToken)
            .Select(codec => codec == VideoCodec.H264 ? H264Options() : JpegOptions())
            .ToArray(),
    };

    public SetVideoEncoderConfigurationResponse SetVideoEncoderConfiguration(SetVideoEncoderConfigurationRequest request)
    {
        var configuration = request.Configuration;
        if (configuration?.Resolution == null)
            throw OnvifFault.InvalidArgValue("The configuration needs a resolution.");
        var resolution = configuration.Resolution;
        var rateControl = configuration.RateControl;
        if (CodecOfEncoder(context, configuration.token) == VideoCodec.H264)
            ApplyH264Settings(context, configuration.Encoding ?? "", resolution.Width, resolution.Height,
                rateControl?.FrameRateLimit ?? Camera.H264.Fps, rateControl?.BitrateLimit,
                configuration.GovLengthSpecified ? configuration.GovLength : null);
        else
            ApplyJpegSettings(context, configuration.Encoding ?? "", resolution.Width, resolution.Height,
                rateControl?.FrameRateLimit ?? Camera.Video.Fps, configuration.Quality);
        return new SetVideoEncoderConfigurationResponse();
    }

    public GetStreamUriResponse GetStreamUri(GetStreamUriRequest request)
    {
        var codec = CodecOfProfile(context, request.ProfileToken);
        if (!SupportedStreamProtocols.Contains(request.Protocol, StringComparer.OrdinalIgnoreCase))
            throw OnvifFault.InvalidArgValue($"Stream protocol '{request.Protocol}' is not supported; use {string.Join(" or ", SupportedStreamProtocols)}.");
        return new GetStreamUriResponse { Uri = StreamUriOf(context, codec) };
    }

    public GetSnapshotUriResponse GetSnapshotUri(GetSnapshotUriRequest request)
    {
        CodecOfProfile(context, request.ProfileToken);
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

    private VideoEncoder2Configuration VideoEncoderConfiguration(VideoCodec codec) =>
        codec == VideoCodec.H264 ? H264EncoderConfiguration() : JpegEncoderConfiguration();

    private VideoEncoder2Configuration JpegEncoderConfiguration() => new()
    {
        token = JpegEncoderToken,
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

    private VideoEncoder2Configuration H264EncoderConfiguration() => new()
    {
        token = H264EncoderToken,
        Name = "VideoEncoderH264",
        UseCount = 1,
        Encoding = "H264",
        Resolution = new VideoResolution2 { Width = Camera.H264.Width, Height = Camera.H264.Height },
        Quality = H264Quality,
        RateControl = new VideoRateControl2
        {
            FrameRateLimit = Camera.H264.Fps,
            BitrateLimit = Camera.H264.BitrateKbps,
            ConstantBitRate = true, ConstantBitRateSpecified = true,
        },
        GovLength = Camera.H264.GovLength, GovLengthSpecified = true,
        Profile = "Baseline",
        Multicast = NoMulticast(),
    };

    private VideoEncoder2ConfigurationOptions JpegOptions()
    {
        var resolutions = Resolutions(Camera.Video.Width, Camera.Video.Height);
        var largest = resolutions.MaxBy(r => r.Width * r.Height);
        return new VideoEncoder2ConfigurationOptions
        {
            Encoding = "JPEG",
            QualityRange = new FloatRange { Min = MinQuality, Max = MaxQuality },
            ResolutionsAvailable = ResolutionsOf(resolutions),
            BitrateRange = new IntRange
            {
                Min = MinBitrateKbps,
                Max = EstimatedBitrateKbps(new VideoModel { Width = largest.Width, Height = largest.Height, Fps = MaxFrameRate }),
            },
            FrameRatesSupported = FrameRates,
        };
    }

    // The quality range is the same as JPEG's so a client can send back what it read; H.264 ignores it.
    private VideoEncoder2ConfigurationOptions H264Options() => new()
    {
        Encoding = "H264",
        QualityRange = new FloatRange { Min = MinQuality, Max = MaxQuality },
        ResolutionsAvailable = ResolutionsOf(Resolutions(Camera.H264.Width, Camera.H264.Height)),
        BitrateRange = new IntRange { Min = MinBitrateKbps, Max = MaxBitrateKbps },
        GovLengthRange = [MinGovLength, MaxGovLength],
        FrameRatesSupported = FrameRates,
        ProfilesSupported = ["Baseline"],
        ConstantBitRateSupported = true, ConstantBitRateSupportedSpecified = true,
    };

    private static VideoResolution2[] ResolutionsOf(IReadOnlyList<(int Width, int Height)> resolutions) =>
        resolutions.Select(r => new VideoResolution2 { Width = r.Width, Height = r.Height }).ToArray();
}
