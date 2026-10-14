using OnvifSim.Rtsp.Rtp;

namespace OnvifSim.Rtsp;

// What an RtspServer streams. The application finds the media a request's URL names and says who may
// have it; the server does the rest: sessions, transports, the SDP, RTCP.
public interface IRtspMediaSource
{
    // The media at a path: the URL's segments after the host, unescaped (rtsp://host/cam1/h264 is
    // ["cam1", "h264"]); null when there is none. A track is addressed by one more segment after its
    // media's path, its control name.
    RtspMedia? Find(IReadOnlyList<string> path);

    // Whether a request (its method and Authorization header) may have the media at path.
    bool IsAuthorized(RtspRequest request, IReadOnlyList<string> path);

    // The WWW-Authenticate values of a 401 response.
    IEnumerable<string> Challenges();
}

// A stream as DESCRIBE announces it. A SETUP of the stream's own URL sets up its first track.
public sealed record RtspMedia(string Name, IReadOnlyList<RtspTrack> Tracks);

// One track of a stream: its SDP media type ("video", "application"), RTP payload type and encoding
// name (the rtpmap, at RtpStream.ClockRate), and what sends it.
public sealed record RtspTrack(string Control, string MediaType, byte PayloadType, string Encoding, TrackPlayback Play)
{
    // The SDP fmtp of the payload format, when it has one.
    public string? FormatParameters { get; init; }
    public int? FrameRate { get; init; }
}

// Sends a track while its session plays: packetizes the media into sender.Stream and sends it until
// cancelled. Returning or throwing before that ends the session: the media is gone or the client stopped
// taking packets.
public delegate Task TrackPlayback(RtpSender sender, CancellationToken cancellationToken);

// A playing track's way out: its RTP stream and the transport its client set up. An RTCP sender report
// goes along every few seconds; every track of a session gives the same CNAME, which tells the client
// to play them on one timeline.
public sealed class RtpSender
{
    private static readonly TimeSpan SenderReportInterval = TimeSpan.FromSeconds(5);

    private readonly IRtpTransport _transport;
    private readonly string _canonicalName;
    private readonly RtpPacketBatch _report = new();
    private long _nextReport;

    internal RtpSender(RtpStream stream, IRtpTransport transport, string canonicalName)
    {
        Stream = stream;
        _transport = transport;
        _canonicalName = canonicalName;
    }

    public RtpStream Stream { get; }

    public async ValueTask SendAsync(RtpPacketBatch batch, CancellationToken cancellationToken)
    {
        await _transport.SendAsync(batch, isRtcp: false, cancellationToken);
        if (Environment.TickCount64 < _nextReport) return;
        _report.Clear();
        Stream.WriteSenderReport(_report, _canonicalName);
        await _transport.SendAsync(_report, isRtcp: true, cancellationToken);
        _nextReport = Environment.TickCount64 + (long)SenderReportInterval.TotalMilliseconds;
    }
}
