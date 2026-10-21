using OnvifSim.Onvif.Wsdl;
using OnvifSim.Onvif.Wsdl.Replay;
using static OnvifSim.Onvif.Services.CameraRecording;

namespace OnvifSim.Onvif.Services;

// Replay (ver10, Profile G): where the camera's recording is streamed from. The stream itself is
// Streaming.ReplayStreams: RTSP over TCP or UDP, forwards only, at any rate.
[OnvifService(typeof(ReplayPort), "replay_service", 21, 12)]
public sealed class ReplayHandlers(OnvifRequestContext context)
{
    // Capabilities8 is trp:Capabilities.
    public GetServiceCapabilitiesResponse GetServiceCapabilities(GetServiceCapabilitiesRequest request) => new()
    {
        Capabilities = new Capabilities8
        {
            ReversePlayback = false, ReversePlaybackSpecified = true,
            SessionTimeoutRange = [1, 60],
            RTP_RTSP_TCP = true, RTP_RTSP_TCPSpecified = true,
        },
    };

    // The same address for every stream setup the request may ask for: the server takes whichever
    // transport the client sets up.
    public GetReplayUriResponse GetReplayUri(GetReplayUriRequest request)
    {
        CheckRecording(request.RecordingToken);
        return new GetReplayUriResponse { Uri = context.ReplayUri };
    }

    public GetReplayConfigurationResponse GetReplayConfiguration(GetReplayConfigurationRequest request) => new()
    {
        Configuration = new ReplayConfiguration { SessionTimeout = CameraMedia.SessionTimeout },
    };

    // The session timeout is the RTSP server's, the same for every stream; asking for another changes nothing.
    public SetReplayConfigurationResponse SetReplayConfiguration(SetReplayConfigurationRequest request) => new();
}
