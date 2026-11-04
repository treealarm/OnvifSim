using OnvifSim.Onvif.Wsdl;
using OnvifSim.Onvif.Wsdl.Media;
using OnvifSim.Onvif.Soap;
using OnvifSim.World;
using static OnvifSim.Onvif.Services.CameraMedia;

namespace OnvifSim.Onvif.Services;

// Media (ver10, Profile S): one profile per stream the camera offers; their stream and snapshot URIs,
// encoder settings and the metadata configuration they share. Audio and OSDs exist only as empty lists,
// so clients that walk them get an answer.
[OnvifService(typeof(Media), "media_service", 21, 12)]
public sealed class MediaHandlers(OnvifRequestContext context)
{
    private CameraModel Camera => context.Camera;

    // Capabilities2 and ProfileCapabilities1 are trt:Capabilities and trt:ProfileCapabilities: svcutil
    // numbers classes whose names other namespaces already took.
    public GetServiceCapabilitiesResponse GetServiceCapabilities(GetServiceCapabilitiesRequest request) => new()
    {
        Capabilities = new Capabilities2
        {
            ProfileCapabilities = new ProfileCapabilities1 { MaximumNumberOfProfiles = VisibleStreams(context).Count, MaximumNumberOfProfilesSpecified = true },
            StreamingCapabilities = new StreamingCapabilities
            {
                RTPMulticast = false, RTPMulticastSpecified = true,
                RTP_TCP = true, RTP_TCPSpecified = true,
                RTP_RTSP_TCP = true, RTP_RTSP_TCPSpecified = true,
                NonAggregateControl = false, NonAggregateControlSpecified = true,
                NoRTSPStreaming = false, NoRTSPStreamingSpecified = true,
            },
            SnapshotUri = true, SnapshotUriSpecified = true,
            Rotation = false, RotationSpecified = true,
            OSD = false, OSDSpecified = true,
        },
    };

    public GetProfilesResponse GetProfiles(GetProfilesRequest request) => new() { Profiles = VisibleStreams(context).Select(Profile).ToArray() };

    public GetProfileResponse GetProfile(GetProfileRequest request) =>
        new() { Profile = Profile(StreamOfProfile(context, request.ProfileToken)) };

    public GetVideoSourcesResponse GetVideoSources(GetVideoSourcesRequest request)
    {
        var first = VisibleStreams(context).FirstOrDefault();
        var (width, height) = first == null ? (Camera.Video.Width, Camera.Video.Height) : ResolutionOf(Camera, first);
        return new()
        {
            VideoSources =
            [
                new VideoSource
                {
                    token = VideoSourceToken,
                    Framerate = first == null ? Camera.Video.Fps : FpsOf(first),
                    Resolution = new VideoResolution { Width = width, Height = height },
                },
            ],
        };
    }

    public GetVideoSourceConfigurationsResponse GetVideoSourceConfigurations(GetVideoSourceConfigurationsRequest request) =>
        new() { Configurations = [VideoSourceConfiguration(context)] };

    public GetVideoSourceConfigurationResponse GetVideoSourceConfiguration(GetVideoSourceConfigurationRequest request)
    {
        if (request.ConfigurationToken != VideoSourceConfigurationToken) throw OnvifFault.NoConfig(request.ConfigurationToken);
        return new GetVideoSourceConfigurationResponse { Configuration = VideoSourceConfiguration(context) };
    }

    public GetVideoEncoderConfigurationsResponse GetVideoEncoderConfigurations(GetVideoEncoderConfigurationsRequest request) =>
        new() { Configurations = VisibleStreams(context).Select(VideoEncoderConfiguration).ToArray() };

    public GetVideoEncoderConfigurationResponse GetVideoEncoderConfiguration(GetVideoEncoderConfigurationRequest request) =>
        new() { Configuration = VideoEncoderConfiguration(StreamOfEncoder(context, request.ConfigurationToken)) };

    // Streams are fixed and each is its own profile, so the compatible encoder of a profile is its own.
    public GetCompatibleVideoEncoderConfigurationsResponse GetCompatibleVideoEncoderConfigurations(GetCompatibleVideoEncoderConfigurationsRequest request) =>
        new() { Configurations = [VideoEncoderConfiguration(StreamOfProfile(context, request.ProfileToken))] };

    // Media1 has one options structure for all encodings; it carries the parts of the codecs the tokens
    // select (one per codec when none is given). The quality range is the JPEG one: H.264 ignores quality.
    public GetVideoEncoderConfigurationOptionsResponse GetVideoEncoderConfigurationOptions(GetVideoEncoderConfigurationOptionsRequest request)
    {
        var streams = OptionStreams(context, request.ConfigurationToken, request.ProfileToken);
        var options = new VideoEncoderConfigurationOptions { QualityRange = new IntRange { Min = MinQuality, Max = MaxQuality } };
        if (streams.FirstOrDefault(stream => stream.Codec == StreamCodec.Jpeg) is { } jpeg)
        {
            var video = VideoOf(Camera, jpeg);
            options.JPEG = new JpegOptions
            {
                ResolutionsAvailable = ResolutionsOf(video.Width, video.Height),
                FrameRateRange = new IntRange { Min = MinFrameRate, Max = MaxFrameRate },
                EncodingIntervalRange = new IntRange { Min = 1, Max = 1 },
            };
        }
        if (streams.FirstOrDefault(stream => stream.Codec == StreamCodec.H264) is { } h264)
        {
            // The bitrate range exists only in the extension's H264Options2, which repeats the rest.
            options.H264 = H264Options<H264Options>(h264);
            var extended = H264Options<H264Options2>(h264);
            extended.BitrateRange = new IntRange { Min = MinBitrateKbps, Max = MaxBitrateKbps };
            options.Extension = new VideoEncoderOptionsExtension { H264 = extended };
        }
        return new GetVideoEncoderConfigurationOptionsResponse { Options = options };
    }

    public SetVideoEncoderConfigurationResponse SetVideoEncoderConfiguration(SetVideoEncoderConfigurationRequest request)
    {
        var configuration = request.Configuration;
        if (configuration?.Resolution == null || configuration.RateControl == null)
            throw OnvifFault.InvalidArgValue("The configuration needs a resolution and a rate control.");
        var (resolution, rateControl) = (configuration.Resolution, configuration.RateControl);
        var stream = StreamOfEncoder(context, configuration.token);
        if (stream.Codec == StreamCodec.H264)
            ApplyH264Settings(context, stream, configuration.Encoding.ToString(), resolution.Width, resolution.Height,
                rateControl.FrameRateLimit, rateControl.BitrateLimit, configuration.H264?.GovLength);
        else
            ApplyJpegSettings(context, stream, configuration.Encoding.ToString(), resolution.Width, resolution.Height,
                rateControl.FrameRateLimit, configuration.Quality);
        return new SetVideoEncoderConfigurationResponse();
    }

    public GetStreamUriResponse GetStreamUri(GetStreamUriRequest request)
    {
        var stream = StreamOfProfile(context, request.ProfileToken);
        if (request.StreamSetup?.Stream == StreamType.RTPMulticast)
            throw OnvifFault.InvalidArgValue("Multicast streaming is not supported.");
        if (request.StreamSetup?.Transport?.Protocol == TransportProtocol.HTTP)
            throw OnvifFault.InvalidArgValue("RTSP over HTTP is not supported; use RTSP, TCP or UDP.");
        return new GetStreamUriResponse { MediaUri = Uri(StreamUriOf(context, stream)) };
    }

    public GetSnapshotUriResponse GetSnapshotUri(GetSnapshotUriRequest request)
    {
        CheckProfile(context, request.ProfileToken);
        return new GetSnapshotUriResponse { MediaUri = Uri(context.SnapshotUri) };
    }

    public GetAudioEncoderConfigurationsResponse GetAudioEncoderConfigurations(GetAudioEncoderConfigurationsRequest request) =>
        new() { Configurations = [] };

    public GetMetadataConfigurationsResponse GetMetadataConfigurations(GetMetadataConfigurationsRequest request) =>
        new() { Configurations = [MetadataConfiguration(context)] };

    public GetMetadataConfigurationResponse GetMetadataConfiguration(GetMetadataConfigurationRequest request)
    {
        if (request.ConfigurationToken != MetadataConfigurationToken) throw OnvifFault.NoConfig(request.ConfigurationToken);
        return new GetMetadataConfigurationResponse { Configuration = MetadataConfiguration(context) };
    }

    public GetCompatibleMetadataConfigurationsResponse GetCompatibleMetadataConfigurations(GetCompatibleMetadataConfigurationsRequest request)
    {
        CheckProfile(context, request.ProfileToken);
        return new GetCompatibleMetadataConfigurationsResponse { Configurations = [MetadataConfiguration(context)] };
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

    public GetVideoAnalyticsConfigurationsResponse GetVideoAnalyticsConfigurations(GetVideoAnalyticsConfigurationsRequest request) =>
        new() { Configurations = [AnalyticsConfiguration(context)] };

    public GetVideoAnalyticsConfigurationResponse GetVideoAnalyticsConfiguration(GetVideoAnalyticsConfigurationRequest request)
    {
        if (request.ConfigurationToken != AnalyticsConfigurationToken) throw OnvifFault.NoConfig(request.ConfigurationToken);
        return new GetVideoAnalyticsConfigurationResponse { Configuration = AnalyticsConfiguration(context) };
    }

    public GetOSDsResponse GetOSDs(GetOSDsRequest request) => new() { OSDs = [] };

    public GetOSDOptionsResponse GetOSDOptions(GetOSDOptionsRequest request)
    {
        CheckVideoSourceConfiguration(request.ConfigurationToken);
        return new GetOSDOptionsResponse { OSDOptions = NoOsdOptions() };
    }

    private Profile Profile(StreamModel stream) => new()
    {
        token = stream.Token,
        @fixed = true,
        fixedSpecified = true,
        Name = stream.Name,
        VideoSourceConfiguration = VideoSourceConfiguration(context),
        VideoEncoderConfiguration = VideoEncoderConfiguration(stream),
        PTZConfiguration = PtzConfiguration(),
        VideoAnalyticsConfiguration = AnalyticsConfiguration(context),
        MetadataConfiguration = MetadataConfiguration(context),
    };

    private PTZConfiguration? PtzConfiguration()
    {
        if (!context.HasPtz) return null;
        var configuration = CameraPtz.Configuration(Camera.Ptz!);
        configuration.UseCount = VisibleStreams(context).Count;
        return configuration;
    }

    private VideoEncoderConfiguration VideoEncoderConfiguration(StreamModel stream) =>
        stream.Codec == StreamCodec.H264 ? H264EncoderConfiguration(stream) : JpegEncoderConfiguration(stream);

    private VideoEncoderConfiguration JpegEncoderConfiguration(StreamModel stream)
    {
        var video = VideoOf(Camera, stream);
        return new VideoEncoderConfiguration
        {
            token = EncoderTokenOf(stream),
            Name = "VideoEncoder",
            UseCount = 1,
            Encoding = VideoEncoding.JPEG,
            Resolution = new VideoResolution { Width = video.Width, Height = video.Height },
            Quality = video.Quality,
            RateControl = new VideoRateControl
            {
                FrameRateLimit = video.Fps,
                EncodingInterval = 1,
                BitrateLimit = EstimatedBitrateKbps(video),
            },
            Multicast = NoMulticast(),
            SessionTimeout = SessionTimeout,
        };
    }

    private VideoEncoderConfiguration H264EncoderConfiguration(StreamModel stream)
    {
        var h264 = H264Of(Camera, stream);
        return new VideoEncoderConfiguration
        {
            token = EncoderTokenOf(stream),
            Name = "VideoEncoderH264",
            UseCount = 1,
            Encoding = VideoEncoding.H264,
            Resolution = new VideoResolution { Width = h264.Width, Height = h264.Height },
            Quality = H264Quality,
            RateControl = new VideoRateControl
            {
                FrameRateLimit = h264.Fps,
                EncodingInterval = 1,
                BitrateLimit = h264.BitrateKbps,
            },
            H264 = new H264Configuration { GovLength = h264.GovLength, H264Profile = H264Profile.Baseline },
            Multicast = NoMulticast(),
            SessionTimeout = SessionTimeout,
        };
    }

    private T H264Options<T>(StreamModel stream) where T : H264Options, new()
    {
        var h264 = H264Of(Camera, stream);
        return new T
        {
            ResolutionsAvailable = ResolutionsOf(h264.Width, h264.Height),
            GovLengthRange = new IntRange { Min = MinGovLength, Max = MaxGovLength },
            FrameRateRange = new IntRange { Min = MinFrameRate, Max = MaxFrameRate },
            EncodingIntervalRange = new IntRange { Min = 1, Max = 1 },
            H264ProfilesSupported = [H264Profile.Baseline],
        };
    }

    private int FpsOf(StreamModel stream) =>
        stream.Codec == StreamCodec.H264 ? H264Of(Camera, stream).Fps : VideoOf(Camera, stream).Fps;
    private static VideoResolution[] ResolutionsOf(int width, int height) =>
        Resolutions(width, height).Select(r => new VideoResolution { Width = r.Width, Height = r.Height }).ToArray();

    private static MediaUri Uri(string uri) => new()
    {
        Uri = uri,
        InvalidAfterConnect = false,
        InvalidAfterReboot = false,
        Timeout = "PT0S",
    };
}
