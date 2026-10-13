using OnvifSim.Onvif.Wsdl;
using OnvifSim.Onvif.Wsdl.Media;
using OnvifSim.Onvif.Soap;
using OnvifSim.Streaming;
using OnvifSim.World;
using static OnvifSim.Onvif.Services.CameraMedia;

namespace OnvifSim.Onvif.Services;

// Media (ver10, Profile S): the JPEG profile and, with ffmpeg, the H.264 one; their stream and snapshot
// URIs and encoder settings. Audio, metadata and OSDs exist only as empty lists, so clients that walk
// them get an answer.
[OnvifService(typeof(Media), "media_service", 21, 12)]
public sealed class MediaHandlers(OnvifRequestContext context)
{
    private CameraModel Camera => context.Camera;

    // Capabilities1 and ProfileCapabilities1 are trt:Capabilities and trt:ProfileCapabilities: svcutil
    // numbers classes whose names other namespaces already took.
    public GetServiceCapabilitiesResponse GetServiceCapabilities(GetServiceCapabilitiesRequest request) => new()
    {
        Capabilities = new Capabilities1
        {
            ProfileCapabilities = new ProfileCapabilities1 { MaximumNumberOfProfiles = Codecs(context).Count, MaximumNumberOfProfilesSpecified = true },
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

    public GetProfilesResponse GetProfiles(GetProfilesRequest request) => new() { Profiles = Codecs(context).Select(Profile).ToArray() };

    public GetProfileResponse GetProfile(GetProfileRequest request) =>
        new() { Profile = Profile(CodecOfProfile(context, request.ProfileToken)) };

    public GetVideoSourcesResponse GetVideoSources(GetVideoSourcesRequest request) => new()
    {
        VideoSources =
        [
            new VideoSource
            {
                token = VideoSourceToken,
                Framerate = Camera.Video.Fps,
                Resolution = new VideoResolution { Width = Camera.Video.Width, Height = Camera.Video.Height },
            },
        ],
    };

    public GetVideoSourceConfigurationsResponse GetVideoSourceConfigurations(GetVideoSourceConfigurationsRequest request) =>
        new() { Configurations = [VideoSourceConfiguration(context)] };

    public GetVideoSourceConfigurationResponse GetVideoSourceConfiguration(GetVideoSourceConfigurationRequest request)
    {
        if (request.ConfigurationToken != VideoSourceConfigurationToken) throw OnvifFault.NoConfig(request.ConfigurationToken);
        return new GetVideoSourceConfigurationResponse { Configuration = VideoSourceConfiguration(context) };
    }

    public GetVideoEncoderConfigurationsResponse GetVideoEncoderConfigurations(GetVideoEncoderConfigurationsRequest request) =>
        new() { Configurations = Codecs(context).Select(VideoEncoderConfiguration).ToArray() };

    public GetVideoEncoderConfigurationResponse GetVideoEncoderConfiguration(GetVideoEncoderConfigurationRequest request) =>
        new() { Configuration = VideoEncoderConfiguration(CodecOfEncoder(context, request.ConfigurationToken)) };

    // Profiles are fixed, so the compatible encoder of a profile is its own.
    public GetCompatibleVideoEncoderConfigurationsResponse GetCompatibleVideoEncoderConfigurations(GetCompatibleVideoEncoderConfigurationsRequest request) =>
        new() { Configurations = [VideoEncoderConfiguration(CodecOfProfile(context, request.ProfileToken))] };

    // Media1 has one options structure for all encodings; it carries the parts of the encoders the
    // tokens select (both when none is given). The quality range is the JPEG one: H.264 ignores quality.
    public GetVideoEncoderConfigurationOptionsResponse GetVideoEncoderConfigurationOptions(GetVideoEncoderConfigurationOptionsRequest request)
    {
        var codecs = SelectCodecs(context, request.ConfigurationToken, request.ProfileToken);
        var options = new VideoEncoderConfigurationOptions { QualityRange = new IntRange { Min = MinQuality, Max = MaxQuality } };
        if (codecs.Contains(VideoCodec.Jpeg))
            options.JPEG = new JpegOptions
            {
                ResolutionsAvailable = ResolutionsOf(Camera.Video.Width, Camera.Video.Height),
                FrameRateRange = new IntRange { Min = MinFrameRate, Max = MaxFrameRate },
                EncodingIntervalRange = new IntRange { Min = 1, Max = 1 },
            };
        if (codecs.Contains(VideoCodec.H264))
        {
            // The bitrate range exists only in the extension's H264Options2, which repeats the rest.
            options.H264 = H264Options<H264Options>();
            var extended = H264Options<H264Options2>();
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
        if (CodecOfEncoder(context, configuration.token) == VideoCodec.H264)
            ApplyH264Settings(context, configuration.Encoding.ToString(), resolution.Width, resolution.Height,
                rateControl.FrameRateLimit, rateControl.BitrateLimit, configuration.H264?.GovLength);
        else
            ApplyJpegSettings(context, configuration.Encoding.ToString(), resolution.Width, resolution.Height,
                rateControl.FrameRateLimit, configuration.Quality);
        return new SetVideoEncoderConfigurationResponse();
    }

    public GetStreamUriResponse GetStreamUri(GetStreamUriRequest request)
    {
        var codec = CodecOfProfile(context, request.ProfileToken);
        if (request.StreamSetup?.Stream == StreamType.RTPMulticast)
            throw OnvifFault.InvalidArgValue("Multicast streaming is not supported.");
        if (request.StreamSetup?.Transport?.Protocol == TransportProtocol.HTTP)
            throw OnvifFault.InvalidArgValue("RTSP over HTTP is not supported; use RTSP, TCP or UDP.");
        return new GetStreamUriResponse { MediaUri = Uri(StreamUriOf(context, codec)) };
    }

    public GetSnapshotUriResponse GetSnapshotUri(GetSnapshotUriRequest request)
    {
        CodecOfProfile(context, request.ProfileToken);
        return new GetSnapshotUriResponse { MediaUri = Uri(context.SnapshotUri) };
    }

    public GetAudioEncoderConfigurationsResponse GetAudioEncoderConfigurations(GetAudioEncoderConfigurationsRequest request) =>
        new() { Configurations = [] };

    public GetMetadataConfigurationsResponse GetMetadataConfigurations(GetMetadataConfigurationsRequest request) =>
        new() { Configurations = [] };

    public GetVideoAnalyticsConfigurationsResponse GetVideoAnalyticsConfigurations(GetVideoAnalyticsConfigurationsRequest request) =>
        new() { Configurations = [] };

    public GetOSDsResponse GetOSDs(GetOSDsRequest request) => new() { OSDs = [] };

    public GetOSDOptionsResponse GetOSDOptions(GetOSDOptionsRequest request)
    {
        CheckVideoSourceConfiguration(request.ConfigurationToken);
        return new GetOSDOptionsResponse { OSDOptions = NoOsdOptions() };
    }

    private Profile Profile(VideoCodec codec) => new()
    {
        token = ProfileTokenOf(codec),
        @fixed = true,
        fixedSpecified = true,
        Name = ProfileNameOf(codec),
        VideoSourceConfiguration = VideoSourceConfiguration(context),
        VideoEncoderConfiguration = VideoEncoderConfiguration(codec),
        PTZConfiguration = context.HasPtz ? CameraPtz.Configuration() : null,
    };

    private VideoEncoderConfiguration VideoEncoderConfiguration(VideoCodec codec) =>
        codec == VideoCodec.H264 ? H264EncoderConfiguration() : JpegEncoderConfiguration();

    private VideoEncoderConfiguration JpegEncoderConfiguration() => new()
    {
        token = JpegEncoderToken,
        Name = "VideoEncoder",
        UseCount = 1,
        Encoding = VideoEncoding.JPEG,
        Resolution = new VideoResolution { Width = Camera.Video.Width, Height = Camera.Video.Height },
        Quality = Camera.Video.Quality,
        RateControl = new VideoRateControl
        {
            FrameRateLimit = Camera.Video.Fps,
            EncodingInterval = 1,
            BitrateLimit = EstimatedBitrateKbps(Camera.Video),
        },
        Multicast = NoMulticast(),
        SessionTimeout = SessionTimeout,
    };

    private VideoEncoderConfiguration H264EncoderConfiguration() => new()
    {
        token = H264EncoderToken,
        Name = "VideoEncoderH264",
        UseCount = 1,
        Encoding = VideoEncoding.H264,
        Resolution = new VideoResolution { Width = Camera.H264.Width, Height = Camera.H264.Height },
        Quality = H264Quality,
        RateControl = new VideoRateControl
        {
            FrameRateLimit = Camera.H264.Fps,
            EncodingInterval = 1,
            BitrateLimit = Camera.H264.BitrateKbps,
        },
        H264 = new H264Configuration { GovLength = Camera.H264.GovLength, H264Profile = H264Profile.Baseline },
        Multicast = NoMulticast(),
        SessionTimeout = SessionTimeout,
    };

    private T H264Options<T>() where T : H264Options, new() => new()
    {
        ResolutionsAvailable = ResolutionsOf(Camera.H264.Width, Camera.H264.Height),
        GovLengthRange = new IntRange { Min = MinGovLength, Max = MaxGovLength },
        FrameRateRange = new IntRange { Min = MinFrameRate, Max = MaxFrameRate },
        EncodingIntervalRange = new IntRange { Min = 1, Max = 1 },
        H264ProfilesSupported = [H264Profile.Baseline],
    };

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
