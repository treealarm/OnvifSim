using OnvifSim.Onvif.Wsdl;
using OnvifSim.Onvif.Wsdl.Media;
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

    // Capabilities1 and ProfileCapabilities1 are trt:Capabilities and trt:ProfileCapabilities: svcutil
    // numbers classes whose names other namespaces already took.
    public GetServiceCapabilitiesResponse GetServiceCapabilities(GetServiceCapabilitiesRequest request) => new()
    {
        Capabilities = new Capabilities1
        {
            ProfileCapabilities = new ProfileCapabilities1 { MaximumNumberOfProfiles = 1, MaximumNumberOfProfilesSpecified = true },
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

    public GetProfilesResponse GetProfiles(GetProfilesRequest request) => new() { Profiles = [Profile()] };

    public GetProfileResponse GetProfile(GetProfileRequest request)
    {
        CheckProfile(request.ProfileToken);
        return new GetProfileResponse { Profile = Profile() };
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
        new() { Configurations = [VideoSourceConfiguration(Camera)] };

    public GetVideoSourceConfigurationResponse GetVideoSourceConfiguration(GetVideoSourceConfigurationRequest request)
    {
        if (request.ConfigurationToken != VideoSourceConfigurationToken) throw OnvifFault.NoConfig(request.ConfigurationToken);
        return new GetVideoSourceConfigurationResponse { Configuration = VideoSourceConfiguration(Camera) };
    }

    public GetVideoEncoderConfigurationsResponse GetVideoEncoderConfigurations(GetVideoEncoderConfigurationsRequest request) =>
        new() { Configurations = [VideoEncoderConfiguration()] };

    public GetVideoEncoderConfigurationResponse GetVideoEncoderConfiguration(GetVideoEncoderConfigurationRequest request)
    {
        if (request.ConfigurationToken != VideoEncoderConfigurationToken) throw OnvifFault.NoConfig(request.ConfigurationToken);
        return new GetVideoEncoderConfigurationResponse { Configuration = VideoEncoderConfiguration() };
    }

    public GetCompatibleVideoEncoderConfigurationsResponse GetCompatibleVideoEncoderConfigurations(GetCompatibleVideoEncoderConfigurationsRequest request)
    {
        CheckProfile(request.ProfileToken);
        return new GetCompatibleVideoEncoderConfigurationsResponse { Configurations = [VideoEncoderConfiguration()] };
    }

    public GetVideoEncoderConfigurationOptionsResponse GetVideoEncoderConfigurationOptions(GetVideoEncoderConfigurationOptionsRequest request)
    {
        CheckVideoEncoderConfiguration(request.ConfigurationToken);
        CheckProfile(request.ProfileToken);
        return new GetVideoEncoderConfigurationOptionsResponse
        {
            Options = new VideoEncoderConfigurationOptions
            {
                QualityRange = new IntRange { Min = MinQuality, Max = MaxQuality },
                JPEG = new JpegOptions
                {
                    ResolutionsAvailable = Resolutions(Camera).Select(r => new VideoResolution { Width = r.Width, Height = r.Height }).ToArray(),
                    FrameRateRange = new IntRange { Min = MinFrameRate, Max = MaxFrameRate },
                    EncodingIntervalRange = new IntRange { Min = 1, Max = 1 },
                },
            },
        };
    }

    public SetVideoEncoderConfigurationResponse SetVideoEncoderConfiguration(SetVideoEncoderConfigurationRequest request)
    {
        var configuration = request.Configuration;
        if (configuration?.Resolution == null || configuration.RateControl == null)
            throw OnvifFault.InvalidArgValue("The configuration needs a resolution and a rate control.");
        ApplyEncoderSettings(context, configuration.token, configuration.Encoding.ToString(),
            configuration.Resolution.Width, configuration.Resolution.Height, configuration.RateControl.FrameRateLimit, configuration.Quality);
        return new SetVideoEncoderConfigurationResponse();
    }

    public GetStreamUriResponse GetStreamUri(GetStreamUriRequest request)
    {
        CheckProfile(request.ProfileToken);
        if (request.StreamSetup?.Stream == StreamType.RTPMulticast)
            throw OnvifFault.InvalidArgValue("Multicast streaming is not supported.");
        if (request.StreamSetup?.Transport?.Protocol == TransportProtocol.HTTP)
            throw OnvifFault.InvalidArgValue("RTSP over HTTP is not supported; use RTSP, TCP or UDP.");
        return new GetStreamUriResponse { MediaUri = Uri(context.StreamUri) };
    }

    public GetSnapshotUriResponse GetSnapshotUri(GetSnapshotUriRequest request)
    {
        CheckProfile(request.ProfileToken);
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

    private Profile Profile() => new()
    {
        token = ProfileToken,
        @fixed = true,
        fixedSpecified = true,
        Name = ProfileName,
        VideoSourceConfiguration = VideoSourceConfiguration(Camera),
        VideoEncoderConfiguration = VideoEncoderConfiguration(),
        PTZConfiguration = context.HasPtz ? CameraPtz.Configuration() : null,
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
        Multicast = NoMulticast(),
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
