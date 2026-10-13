using MediaServiceReference1;
using OnvifSim.Onvif.Soap;
using OnvifSim.World;
using static OnvifSim.Onvif.Services.CameraMedia;

namespace OnvifSim.Onvif.Services;

// Media (ver10, Profile S): the one JPEG profile, its stream and snapshot URIs and its encoder settings.
// Audio, metadata and OSDs exist only as empty lists, so clients that walk them get an answer.
[OnvifService(typeof(Media), "media_service", 21, 12)]
public sealed class MediaHandlers(OnvifRequestContext context)
{
    private CameraModel Camera => context.Camera;

    public Capabilities GetServiceCapabilities() => new()
    {
        ProfileCapabilities = new ProfileCapabilities { MaximumNumberOfProfiles = 1, MaximumNumberOfProfilesSpecified = true },
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
    };

    public GetProfilesResponse GetProfiles(GetProfilesRequest request) => new() { Profiles = [Profile()] };

    public Profile GetProfile(string profileToken)
    {
        CheckProfile(profileToken);
        return Profile();
    }

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
        new() { Configurations = [VideoSourceConfiguration()] };

    public VideoSourceConfiguration GetVideoSourceConfiguration(string configurationToken)
    {
        if (configurationToken != VideoSourceConfigurationToken) throw OnvifFault.NoConfig(configurationToken);
        return VideoSourceConfiguration();
    }

    public GetVideoEncoderConfigurationsResponse GetVideoEncoderConfigurations(GetVideoEncoderConfigurationsRequest request) =>
        new() { Configurations = [VideoEncoderConfiguration()] };

    public VideoEncoderConfiguration GetVideoEncoderConfiguration(string configurationToken)
    {
        if (configurationToken != VideoEncoderConfigurationToken) throw OnvifFault.NoConfig(configurationToken);
        return VideoEncoderConfiguration();
    }

    public GetCompatibleVideoEncoderConfigurationsResponse GetCompatibleVideoEncoderConfigurations(GetCompatibleVideoEncoderConfigurationsRequest request)
    {
        CheckProfile(request.ProfileToken);
        return new GetCompatibleVideoEncoderConfigurationsResponse { Configurations = [VideoEncoderConfiguration()] };
    }

    public VideoEncoderConfigurationOptions GetVideoEncoderConfigurationOptions(string configurationToken, string profileToken)
    {
        CheckVideoEncoderConfiguration(configurationToken);
        CheckProfile(profileToken);
        return new VideoEncoderConfigurationOptions
        {
            QualityRange = new IntRange { Min = MinQuality, Max = MaxQuality },
            JPEG = new JpegOptions
            {
                ResolutionsAvailable = Resolutions(Camera).Select(r => new VideoResolution { Width = r.Width, Height = r.Height }).ToArray(),
                FrameRateRange = new IntRange { Min = MinFrameRate, Max = MaxFrameRate },
                EncodingIntervalRange = new IntRange { Min = 1, Max = 1 },
            },
        };
    }

    public void SetVideoEncoderConfiguration(VideoEncoderConfiguration configuration, bool forcePersistence)
    {
        if (configuration?.Resolution == null || configuration.RateControl == null)
            throw OnvifFault.InvalidArgValue("The configuration needs a resolution and a rate control.");
        ApplyEncoderSettings(context, configuration.token, configuration.Encoding.ToString(),
            configuration.Resolution.Width, configuration.Resolution.Height, configuration.RateControl.FrameRateLimit, configuration.Quality);
    }

    public MediaUri GetStreamUri(StreamSetup streamSetup, string profileToken)
    {
        CheckProfile(profileToken);
        if (streamSetup?.Stream == StreamType.RTPMulticast)
            throw OnvifFault.InvalidArgValue("Multicast streaming is not supported.");
        if (streamSetup?.Transport?.Protocol == TransportProtocol.HTTP)
            throw OnvifFault.InvalidArgValue("RTSP over HTTP is not supported; use RTSP, TCP or UDP.");
        return Uri(context.StreamUri);
    }

    public MediaUri GetSnapshotUri(string profileToken)
    {
        CheckProfile(profileToken);
        return Uri(context.SnapshotUri);
    }

    public GetAudioEncoderConfigurationsResponse GetAudioEncoderConfigurations(GetAudioEncoderConfigurationsRequest request) =>
        new() { Configurations = [] };

    public GetMetadataConfigurationsResponse GetMetadataConfigurations(GetMetadataConfigurationsRequest request) =>
        new() { Configurations = [] };

    public GetVideoAnalyticsConfigurationsResponse GetVideoAnalyticsConfigurations(GetVideoAnalyticsConfigurationsRequest request) =>
        new() { Configurations = [] };

    public GetOSDsResponse GetOSDs(GetOSDsRequest request) => new() { OSDs = [] };

    // No OSD slots: clients see that they cannot add one.
    public GetOSDOptionsResponse GetOSDOptions(GetOSDOptionsRequest request)
    {
        CheckVideoSourceConfiguration(request.ConfigurationToken);
        return new GetOSDOptionsResponse
        {
            OSDOptions = new OSDConfigurationOptions
            {
                MaximumNumberOfOSDs = new MaximumNumberOfOSDs { Total = 0 },
                Type = [OSDType.Text],
                PositionOption = ["UpperLeft"],
            },
        };
    }

    private Profile Profile() => new()
    {
        token = ProfileToken,
        @fixed = true,
        fixedSpecified = true,
        Name = ProfileName,
        VideoSourceConfiguration = VideoSourceConfiguration(),
        VideoEncoderConfiguration = VideoEncoderConfiguration(),
    };

    private VideoSourceConfiguration VideoSourceConfiguration() => new()
    {
        token = VideoSourceConfigurationToken,
        Name = "VideoSourceConfig",
        UseCount = 1,
        SourceToken = VideoSourceToken,
        Bounds = new IntRectangle { x = 0, y = 0, width = Camera.Video.Width, height = Camera.Video.Height },
    };

    private VideoEncoderConfiguration VideoEncoderConfiguration() => new()
    {
        token = VideoEncoderConfigurationToken,
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
        Multicast = new MulticastConfiguration
        {
            Address = new IPAddress { Type = IPType.IPv4, IPv4Address = "0.0.0.0" },
            Port = 0,
            TTL = 0,
            AutoStart = false,
        },
        SessionTimeout = SessionTimeout,
    };

    private static MediaUri Uri(string uri) => new()
    {
        Uri = uri,
        InvalidAfterConnect = false,
        InvalidAfterReboot = false,
        Timeout = "PT0S",
    };
}
