using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using OnvifSim.Analytics;
using OnvifSim.Onvif;
using OnvifSim.Streaming.Rtp;

namespace OnvifSim.Streaming.Rtsp;

// The tracks of a camera's stream: its video and its ONVIF metadata (analytics boxes, PTZ status).
public enum TrackKind { Video, Metadata }

// One RTSP session: a camera's stream in one codec set up for one client, with the tracks the client set
// up (the video, the metadata or both), all over TCP or all over UDP. While playing it sends every
// track as an RTP stream of its own, plus an RTCP sender report for each every few seconds.
public sealed class RtspSession(string cameraId, VideoCodec codec, ILogger logger)
{
    // Announced in the Session header; UDP sessions that hear nothing from their client for this long
    // are closed. TCP sessions live as long as their connection.
    public const int TimeoutSeconds = 60;

    private static readonly TimeSpan SenderReportInterval = TimeSpan.FromSeconds(5);
    // When there is nothing new to tell, the metadata track still sends a document this often: ffmpeg
    // (and so many NVRs) waits for a packet on every track before showing anything.
    private static readonly TimeSpan MetadataHeartbeat = TimeSpan.FromSeconds(1);

    public sealed class Track(TrackKind kind, string url, IRtpTransport transport, RtpStream stream)
    {
        public TrackKind Kind => kind;
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
    public string CameraId => cameraId;
    public VideoCodec Codec => codec;

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

    public Track? TrackOf(TrackKind kind) => Tracks.FirstOrDefault(t => t.Kind == kind);

    // Whether interleaved packets of another track would land on a channel a track already uses.
    public bool SharesChannel(IRtpTransport transport) =>
        transport is InterleavedTransport added && Tracks.Any(t => t.Transport is InterleavedTransport used && used.Overlaps(added));

    public Track AddTrack(TrackKind kind, string url, IRtpTransport transport)
    {
        byte payloadType = kind == TrackKind.Metadata ? MetadataPacketizer.PayloadType
            : codec == VideoCodec.H264 ? H264Packetizer.PayloadType
            : JpegPacketizer.PayloadType;
        var track = new Track(kind, url, transport, new RtpStream(payloadType));
        lock (_gate) _tracks.Add(track);
        return track;
    }

    public void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

    public bool HasTimedOut(long now) =>
        now - Interlocked.Read(ref _lastActivity) > TimeoutSeconds * 1000L;

    // Starts sending every track; does nothing if already playing or if the camera is gone. onFailed runs
    // when a track ends on its own: the camera was removed, the encoder failed or the client stopped
    // accepting packets.
    public void Play(FeedManager feeds, AnalyticsManager analytics, Action<RtspSession> onFailed)
    {
        lock (_gate)
        {
            if (_playback != null) return;
            var playback = new CancellationTokenSource();
            var loops = new List<Task>();
            foreach (var track in _tracks)
                if (Start(track, feeds, analytics, onFailed, playback.Token) is { } loop)
                    loops.Add(loop);
            if (loops.Count < _tracks.Count)
            {
                playback.Cancel();
                playback.Dispose();
                return;
            }
            _playback = playback;
            _playbackTask = Task.WhenAll(loops);
        }
    }

    // PAUSE and TEARDOWN: stops sending and waits until the sending loops have let go of their feeds.
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

    private Task? Start(Track track, FeedManager feeds, AnalyticsManager analytics, Action<RtspSession> onFailed, CancellationToken token)
    {
        switch (track.Kind, codec)
        {
            case (TrackKind.Metadata, _) when analytics.Get(cameraId) is { } camera:
                var looks = camera.Subscribe();
                return Task.Run(() => SendMetadataAsync(track, looks, onFailed, token), CancellationToken.None);
            case (TrackKind.Video, VideoCodec.Jpeg) when feeds.Get(cameraId) is { } feed:
                var frames = feed.Subscribe();
                return Task.Run(() => SendFramesAsync(track, frames.Frames, frames,
                    (frame, batch) => JpegPacketizer.Packetize(track.Stream, frame, batch), onFailed, token), CancellationToken.None);
            case (TrackKind.Video, VideoCodec.H264) when feeds.GetH264(cameraId) is { } feed:
                var units = feed.Subscribe();
                return Task.Run(() => SendFramesAsync(track, units.Frames, units,
                    (unit, batch) => H264Packetizer.Packetize(track.Stream, unit, batch), onFailed, token), CancellationToken.None);
            default:
                return null;
        }
    }

    private async Task SendFramesAsync<TFrame>(
        Track track, ChannelReader<TFrame> frames, IDisposable subscription, Action<TFrame, RtpPacketBatch> packetize,
        Action<RtspSession> onFailed, CancellationToken cancellationToken)
    {
        var batch = new RtpPacketBatch();
        long nextSenderReport = 0;
        try
        {
            await foreach (var frame in frames.ReadAllAsync(cancellationToken))
            {
                batch.Clear();
                packetize(frame, batch);
                await track.Transport.SendAsync(batch, isRtcp: false, cancellationToken);
                nextSenderReport = await SendSenderReportWhenDueAsync(track, batch, nextSenderReport, cancellationToken);
            }
            logger.LogInformation("RTSP session {Session}: the {Codec} stream of camera {Camera} ended", Id, codec, cameraId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (NotSupportedException ex)
        {
            logger.LogError(ex, "RTSP session {Session}: camera {Camera} produces frames RTP cannot carry", Id, cameraId);
        }
        catch (Exception ex)
        {
            // The client stopped accepting packets: connection closed, write timed out, network gone.
            logger.LogInformation("RTSP session {Session}: stream stopped: {Reason}", Id, ex.Message);
        }
        finally
        {
            subscription.Dispose();
        }
        onFailed(this);
    }

    // One document for every look worth telling (IsWorthSending), the first one at once.
    private async Task SendMetadataAsync(
        Track track, CameraAnalytics.Subscription looks, Action<RtspSession> onFailed, CancellationToken cancellationToken)
    {
        var batch = new RtpPacketBatch();
        long nextSenderReport = 0;
        AnalyticsFrame? previous = null;
        var lastSent = DateTimeOffset.MinValue;
        try
        {
            await foreach (var frame in looks.Frames.ReadAllAsync(cancellationToken))
            {
                if (IsWorthSending(frame, previous, lastSent))
                {
                    batch.Clear();
                    MetadataPacketizer.Packetize(track.Stream, MetadataDocument.Write(frame), Stopwatch.GetTimestamp(), batch);
                    await track.Transport.SendAsync(batch, isRtcp: false, cancellationToken);
                    nextSenderReport = await SendSenderReportWhenDueAsync(track, batch, nextSenderReport, cancellationToken);
                    lastSent = frame.UtcTime;
                }
                previous = frame;
            }
            logger.LogInformation("RTSP session {Session}: the metadata of camera {Camera} ended", Id, cameraId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogInformation("RTSP session {Session}: metadata stopped: {Reason}", Id, ex.Message);
        }
        finally
        {
            looks.Dispose();
        }
        onFailed(this);
    }

    // Like a camera, the track talks while there is something to tell: someone in view, someone who has
    // just left (the empty frame says so), a PTZ head on the move or just come to rest. Otherwise it only
    // says once in a while that the scene is empty.
    private static bool IsWorthSending(AnalyticsFrame frame, AnalyticsFrame? previous, DateTimeOffset lastSent) =>
        frame.Objects.Count > 0 || previous?.Objects.Count > 0
        || HeadMoves(frame) || (previous != null && HeadMoves(previous))
        || frame.UtcTime - lastSent >= MetadataHeartbeat;

    private static bool HeadMoves(AnalyticsFrame frame) =>
        frame.Camera.Metadata.PtzStatus && frame.Ptz is { } head && (head.PanTiltMoving || head.ZoomMoving);

    // The RTCP sender report of a track, every SenderReportInterval; returns when the next one is due.
    // All tracks give the same CNAME, which tells the client to play them on one timeline.
    private async ValueTask<long> SendSenderReportWhenDueAsync(Track track, RtpPacketBatch batch, long due, CancellationToken cancellationToken)
    {
        if (Environment.TickCount64 < due) return due;
        batch.Clear();
        track.Stream.WriteSenderReport(batch, $"onvifsim-{cameraId}");
        await track.Transport.SendAsync(batch, isRtcp: true, cancellationToken);
        return Environment.TickCount64 + (long)SenderReportInterval.TotalMilliseconds;
    }
}
