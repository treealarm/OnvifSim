using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OnvifSim.Rtsp.Rtp;

namespace OnvifSim.Rtsp;

// One RTSP session: a stream set up for one client, with the tracks the client set up (any of the
// stream's tracks), all over TCP or all over UDP. While playing it sends every track as an RTP stream of
// its own.
internal sealed class RtspSession(IReadOnlyList<string> path, ILogger logger)
{
    // Announced in the Session header; UDP sessions that hear nothing from their client for this long
    // are closed. TCP sessions live as long as their connection.
    public const int TimeoutSeconds = 60;

    public sealed class Track(RtspTrack definition, string url, IRtpTransport transport, RtpStream stream)
    {
        public RtspTrack Definition => definition;
        // The URL the client set the track up with, echoed in RTP-Info.
        public string Url => url;
        public IRtpTransport Transport => transport;
        public RtpStream Stream => stream;
        public string TransportHeader => $"{transport.ResponseHeader};ssrc={stream.Ssrc:X8}";
    }

    private readonly Lock _gate = new();
    private readonly List<Track> _tracks = [];
    private CancellationTokenSource? _playback;
    private Task _playbackTask = Task.CompletedTask;
    private long _lastActivity = Environment.TickCount64;

    public string Id { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    // The path of the stream's media, as IRtspMediaSource.Find takes it.
    public IReadOnlyList<string> Path => path;
    public string PathText { get; } = string.Join('/', path);

    public IReadOnlyList<Track> Tracks
    {
        get { lock (_gate) return [.. _tracks]; }
    }

    public bool IsPlaying
    {
        get { lock (_gate) return _playback != null; }
    }

    public bool IsUdp => Tracks.Any(t => t.Transport is UdpTransport);

    public bool BelongsTo(RtspConnection connection) =>
        Tracks.Any(t => t.Transport is InterleavedTransport interleaved && interleaved.Connection == connection);

    public bool IsFromClient(IPEndPoint sender) => Tracks.Any(t => t.Transport is UdpTransport udp && udp.IsFromClient(sender));

    public Track? TrackOf(string control) => Tracks.FirstOrDefault(t => t.Definition.Control == control);

    // Whether interleaved packets of another track would land on a channel a track already uses.
    public bool SharesChannel(IRtpTransport transport) =>
        transport is InterleavedTransport added && Tracks.Any(t => t.Transport is InterleavedTransport used && used.Overlaps(added));

    public Track AddTrack(RtspTrack definition, string url, IRtpTransport transport)
    {
        var track = new Track(definition, url, transport, new RtpStream(definition.PayloadType));
        lock (_gate) _tracks.Add(track);
        return track;
    }

    public void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

    public bool HasTimedOut(long now) =>
        now - Interlocked.Read(ref _lastActivity) > TimeoutSeconds * 1000L;

    // Starts sending every track; does nothing if already playing. onFailed runs when a track ends on its
    // own: the media is gone or the client stopped accepting packets.
    public void Play(Action<RtspSession> onFailed)
    {
        lock (_gate)
        {
            if (_playback != null) return;
            _playback = new CancellationTokenSource();
            var token = _playback.Token;
            _playbackTask = Task.WhenAll(_tracks.Select(track => Task.Run(() => SendAsync(track, onFailed, token), CancellationToken.None)));
        }
    }

    // PAUSE and TEARDOWN: stops sending and waits until the tracks have let go of their media.
    public async Task StopAsync()
    {
        Task task;
        lock (_gate)
        {
            _playback?.Cancel();
            _playback = null;
            task = _playbackTask;
        }
        await task;
    }

    private async Task SendAsync(Track track, Action<RtspSession> onFailed, CancellationToken cancellationToken)
    {
        try
        {
            await track.Definition.Play(new RtpSender(track.Stream, track.Transport, canonicalName: Id), cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            logger.LogInformation("RTSP session {Session}: {Track} of {Path} ended", Id, track.Definition.Control, PathText);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (NotSupportedException ex)
        {
            logger.LogError(ex, "RTSP session {Session}: {Path} produces media RTP cannot carry", Id, PathText);
        }
        catch (Exception ex)
        {
            // The client stopped accepting packets: connection closed, write timed out, network gone.
            logger.LogInformation("RTSP session {Session}: {Track} stopped: {Reason}", Id, track.Definition.Control, ex.Message);
        }
        onFailed(this);
    }
}
