using System.Security.Cryptography;
using System.Threading.Channels;
using OnvifSim.Streaming.Rtp;

namespace OnvifSim.Streaming.Rtsp;

// One RTSP session: a camera's stream in one codec set up for one client. While playing it subscribes to
// the camera's feed of that codec and sends every frame as RTP, plus an RTCP sender report every few
// seconds.
public sealed class RtspSession(string cameraId, VideoCodec codec, string trackUrl, IRtpTransport transport, ILogger logger)
{
    // Announced in the Session header; UDP sessions that hear nothing from their client for this long
    // are closed. TCP sessions live as long as their connection.
    public const int TimeoutSeconds = 60;

    private static readonly TimeSpan SenderReportInterval = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private CancellationTokenSource? _playback;
    private Task _playbackTask = Task.CompletedTask;
    private long _lastActivity = Environment.TickCount64;

    public string Id { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    public string CameraId => cameraId;
    public VideoCodec Codec => codec;
    // The URL the client set the track up with, echoed in RTP-Info.
    public string TrackUrl => trackUrl;
    public IRtpTransport Transport => transport;
    public RtpStream Stream { get; } = new(codec == VideoCodec.H264 ? H264Packetizer.PayloadType : JpegPacketizer.PayloadType);

    public string TransportHeader => $"{transport.ResponseHeader};ssrc={Stream.Ssrc:X8}";

    public void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

    public bool HasTimedOut(long now) =>
        now - Interlocked.Read(ref _lastActivity) > TimeoutSeconds * 1000L;

    // Starts sending frames; does nothing if already playing or if the camera has no feed of this codec
    // (any more). onFailed runs when the stream ends on its own: the camera was removed, the encoder
    // failed or the client stopped accepting packets.
    public void Play(FeedManager feeds, Action<RtspSession> onFailed)
    {
        lock (_gate)
        {
            if (_playback != null) return;
            var playback = new CancellationTokenSource();
            var token = playback.Token;
            switch (codec)
            {
                case VideoCodec.Jpeg when feeds.Get(cameraId) is { } feed:
                    var frames = feed.Subscribe();
                    _playbackTask = Task.Run(() => SendFramesAsync(frames.Frames, frames,
                        (frame, batch) => JpegPacketizer.Packetize(Stream, frame, batch), onFailed, token), CancellationToken.None);
                    break;
                case VideoCodec.H264 when feeds.GetH264(cameraId) is { } feed:
                    var units = feed.Subscribe();
                    _playbackTask = Task.Run(() => SendFramesAsync(units.Frames, units,
                        (unit, batch) => H264Packetizer.Packetize(Stream, unit, batch), onFailed, token), CancellationToken.None);
                    break;
                default:
                    playback.Dispose();
                    return;
            }
            _playback = playback;
        }
    }

    // PAUSE and TEARDOWN: stops sending and waits until the sending loop has let go of the feed.
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

    private async Task SendFramesAsync<TFrame>(
        ChannelReader<TFrame> frames, IDisposable subscription, Action<TFrame, RtpPacketBatch> packetize,
        Action<RtspSession> onFailed, CancellationToken cancellationToken)
    {
        var batch = new RtpPacketBatch();
        var canonicalName = $"onvifsim-{cameraId}";
        long nextSenderReport = 0;
        try
        {
            await foreach (var frame in frames.ReadAllAsync(cancellationToken))
            {
                batch.Clear();
                packetize(frame, batch);
                await transport.SendAsync(batch, isRtcp: false, cancellationToken);

                if (Environment.TickCount64 >= nextSenderReport)
                {
                    batch.Clear();
                    Stream.WriteSenderReport(batch, canonicalName);
                    await transport.SendAsync(batch, isRtcp: true, cancellationToken);
                    nextSenderReport = Environment.TickCount64 + (long)SenderReportInterval.TotalMilliseconds;
                }
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
}
