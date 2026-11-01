using OnvifSim.Onvif.Wsdl;
using OnvifSim.Onvif.Wsdl.Media2;
using OnvifSim.Onvif.Soap;
using OnvifSim.World;
using static OnvifSim.Onvif.Services.CameraMedia;

namespace OnvifSim.Onvif.Services;

// Media2 (ver20): the same streams as Media1 in the Media2 shapes, which OnvifLib and most current
// clients prefer. Audio and OSDs are empty, as in Media1.
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
                MaximumNumberOfProfiles = VisibleStreams(context).Count, MaximumNumberOfProfilesSpecified = true,
                ConfigurationsSupported = context.HasPtz
                    ? ["VideoSource", "VideoEncoder", "Analytics", "Metadata", "PTZ"]
                    : ["VideoSource", "VideoEncoder", "Analytics", "Metadata"],
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
        var streams = string.IsNullOrEmpty(request.Token) ? VisibleStreams(context) : [StreamOfProfile(context, request.Token)];
        var types = request.Type ?? [];
        bool all = types.Contains("All");
        bool videoSource = all || types.Contains("VideoSource");
        bool videoEncoder = all || types.Contains("VideoEncoder");
        bool ptz = context.HasPtz && (all || types.Contains("PTZ"));
        bool analytics = all || types.Contains("Analytics");
        bool metadata = all || types.Contains("Metadata");
        return new GetProfilesResponse
        {
            Profiles = streams.Select(stream => new MediaProfile
            {
                token = stream.Token,
                @fixed = true,
                fixedSpecified = true,
                Name = stream.Name,
                Configurations = videoSource || videoEncoder || ptz || analytics || metadata
                    ? new ConfigurationSet
                    {
                        VideoSource = videoSource ? VideoSourceConfiguration(context) : null,
                        VideoEncoder = videoEncoder ? VideoEncoderConfiguration(stream) : null,
                        Analytics = analytics ? AnalyticsConfiguration(context) : null,
                        PTZ = ptz ? PtzConfiguration() : null,
                        Metadata = metadata ? MetadataConfiguration(context) : null,
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
        Configurations = SelectStreams(context, request.ConfigurationToken, request.ProfileToken)
            .Select(VideoEncoderConfiguration).ToArray(),
    };

    public GetVideoEncoderConfigurationOptionsResponse GetVideoEncoderConfigurationOptions(GetVideoEncoderConfigurationOptionsRequest request) => new()
    {
        Options = OptionStreams(context, request.ConfigurationToken, request.ProfileToken)
            .Select(stream => stream.Codec == StreamCodec.H264 ? H264Options(stream) : JpegOptions(stream))
            .ToArray(),
    };

    public SetVideoEncoderConfigurationResponse SetVideoEncoderConfiguration(SetVideoEncoderConfigurationRequest request)
    {
        var configuration = request.Configuration;
        if (configuration?.Resolution == null)
            throw OnvifFault.InvalidArgValue("The configuration needs a resolution.");
        var resolution = configuration.Resolution;
        var rateControl = configuration.RateControl;
        var stream = StreamOfEncoder(context, configuration.token);
        if (stream.Codec == StreamCodec.H264)
            ApplyH264Settings(context, stream, configuration.Encoding ?? "", resolution.Width, resolution.Height,
                rateControl?.FrameRateLimit ?? H264Of(Camera, stream).Fps, rateControl?.BitrateLimit,
                configuration.GovLengthSpecified ? configuration.GovLength : null);
        else
            ApplyJpegSettings(context, stream, configuration.Encoding ?? "", resolution.Width, resolution.Height,
                rateControl?.FrameRateLimit ?? VideoOf(Camera, stream).Fps, configuration.Quality);
        return new SetVideoEncoderConfigurationResponse();
    }

    public GetStreamUriResponse GetStreamUri(GetStreamUriRequest request)
    {
        var stream = StreamOfProfile(context, request.ProfileToken);
        if (!SupportedStreamProtocols.Contains(request.Protocol, StringComparer.OrdinalIgnoreCase))
            throw OnvifFault.InvalidArgValue($"Stream protocol '{request.Protocol}' is not supported; use {string.Join(" or ", SupportedStreamProtocols)}.");
        return new GetStreamUriResponse { Uri = StreamUriOf(context, stream) };
    }

    public GetSnapshotUriResponse GetSnapshotUri(GetSnapshotUriRequest request)
    {
        CheckProfile(context, request.ProfileToken);
        return new GetSnapshotUriResponse { Uri = context.SnapshotUri };
    }

    public GetAudioEncoderConfigurationsResponse GetAudioEncoderConfigurations(GetAudioEncoderConfigurationsRequest request) =>
        new() { Configurations = [] };

    public GetMetadataConfigurationsResponse GetMetadataConfigurations(GetMetadataConfigurationsRequest request)
    {
        CheckMetadataConfiguration(request.ConfigurationToken);
        CheckProfile(context, request.ProfileToken);
        return new GetMetadataConfigurationsResponse { Configurations = [MetadataConfiguration(context)] };
    }

    public GetMetadataConfigurationOptionsResponse GetMetadataConfigurationOptions(GetMetadataConfigurationOptionsRequest request)
    {
        CheckMetadataConfiguration(request.ConfigurationToken);
        CheckProfile(context, request.ProfileToken);
        return new GetMetadataConfigurationOptionsResponse { Options = MetadataConfigurationOptions(context) };
    }

    public SetMetadataConfigurationResponse SetMetadataConfiguration(SetMetadataConfigurationRequest request)
    {
        ApplyMetadataSettings(context, request.Configuration);
        return new SetMetadataConfigurationResponse();
    }

    public GetAnalyticsConfigurationsResponse GetAnalyticsConfigurations(GetAnalyticsConfigurationsRequest request)
    {
        CheckAnalyticsConfiguration(request.ConfigurationToken);
        CheckProfile(context, request.ProfileToken);
        return new GetAnalyticsConfigurationsResponse { Configurations = [AnalyticsConfiguration(context)] };
    }

    public GetOSDsResponse GetOSDs(GetOSDsRequest request) => new() { OSDs = [] };

    public GetOSDOptionsResponse GetOSDOptions(GetOSDOptionsRequest request)
    {
        CheckVideoSourceConfiguration(request.ConfigurationToken);
        return new GetOSDOptionsResponse { OSDOptions = NoOsdOptions() };
    }

    private PTZConfiguration? PtzConfiguration()
    {
        if (!context.HasPtz) return null;
        var configuration = CameraPtz.Configuration(Camera.Ptz!);
        configuration.UseCount = VisibleStreams(context).Count;
        return configuration;
    }

    private VideoEncoder2Configuration VideoEncoderConfiguration(StreamModel stream) =>
        stream.Codec == StreamCodec.H264 ? H264EncoderConfiguration(stream) : JpegEncoderConfiguration(stream);

    private VideoEncoder2Configuration JpegEncoderConfiguration(StreamModel stream)
    {
        var video = VideoOf(Camera, stream);
        return new VideoEncoder2Configuration
        {
            token = EncoderTokenOf(stream),
            Name = "VideoEncoder",
            UseCount = 1,
            Encoding = "JPEG",
            Resolution = new VideoResolution2 { Width = video.Width, Height = video.Height },
            Quality = video.Quality,
            RateControl = new VideoRateControl2
            {
                FrameRateLimit = video.Fps,
                BitrateLimit = EstimatedBitrateKbps(video),
                ConstantBitRate = false, ConstantBitRateSpecified = true,
            },
            Multicast = NoMulticast(),
        };
    }

    private VideoEncoder2Configuration H264EncoderConfiguration(StreamModel stream)
    {
        var h264 = H264Of(Camera, stream);
        return new VideoEncoder2Configuration
        {
            token = EncoderTokenOf(stream),
            Name = "VideoEncoderH264",
            UseCount = 1,
            Encoding = "H264",
            Resolution = new VideoResolution2 { Width = h264.Width, Height = h264.Height },
            Quality = H264Quality,
            RateControl = new VideoRateControl2
            {
                FrameRateLimit = h264.Fps,
                BitrateLimit = h264.BitrateKbps,
                ConstantBitRate = true, ConstantBitRateSpecified = true,
            },
            GovLength = h264.GovLength, GovLengthSpecified = true,
            Profile = "Baseline",
            Multicast = NoMulticast(),
        };
    }

    private VideoEncoder2ConfigurationOptions JpegOptions(StreamModel stream)
    {
        var video = VideoOf(Camera, stream);
        var resolutions = Resolutions(video.Width, video.Height);
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
    private VideoEncoder2ConfigurationOptions H264Options(StreamModel stream)
    {
        var h264 = H264Of(Camera, stream);
        return new VideoEncoder2ConfigurationOptions
        {
            Encoding = "H264",
            QualityRange = new FloatRange { Min = MinQuality, Max = MaxQuality },
            ResolutionsAvailable = ResolutionsOf(Resolutions(h264.Width, h264.Height)),
            BitrateRange = new IntRange { Min = MinBitrateKbps, Max = MaxBitrateKbps },
            GovLengthRange = [MinGovLength, MaxGovLength],
            FrameRatesSupported = FrameRates,
            ProfilesSupported = ["Baseline"],
            ConstantBitRateSupported = true, ConstantBitRateSupportedSpecified = true,
        };
    }

    private static VideoResolution2[] ResolutionsOf(IReadOnlyList<(int Width, int Height)> resolutions) =>
        resolutions.Select(r => new VideoResolution2 { Width = r.Width, Height = r.Height }).ToArray();
}
