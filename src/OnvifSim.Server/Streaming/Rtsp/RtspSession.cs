using System.Security.Cryptography;
using OnvifSim.Streaming.Rtp;

namespace OnvifSim.Streaming.Rtsp;

// One RTSP session: a camera's stream set up for one client. While playing it subscribes to the
// camera's feed and sends every frame as RTP/JPEG, plus an RTCP sender report every few seconds.
public sealed class RtspSession(string cameraId, string trackUrl, IRtpTransport transport, ILogger logger)
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
    // The URL the client set the track up with, echoed in RTP-Info.
    public string TrackUrl => trackUrl;
    public IRtpTransport Transport => transport;
    public JpegRtpStream Stream { get; } = new();

    public string TransportHeader => $"{transport.ResponseHeader};ssrc={Stream.Ssrc:X8}";

    public void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

    public bool HasTimedOut(long now) =>
        now - Interlocked.Read(ref _lastActivity) > TimeoutSeconds * 1000L;

    // Starts sending frames; does nothing if already playing. onFailed runs when the stream ends on its
    // own: the camera was removed or the client stopped accepting packets.
    public void Play(CameraFeed feed, Action<RtspSession> onFailed)
    {
        lock (_gate)
        {
            if (_playback != null) return;
            var playback = new CancellationTokenSource();
            _playback = playback;
            var subscription = feed.Subscribe();
            _playbackTask = Task.Run(() => SendFramesAsync(subscription, onFailed, playback.Token), CancellationToken.None);
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

    private async Task SendFramesAsync(CameraFeed.Subscription subscription, Action<RtspSession> onFailed, CancellationToken cancellationToken)
    {
        var batch = new RtpPacketBatch();
        var canonicalName = $"onvifsim-{cameraId}";
        long nextSenderReport = 0;
        try
        {
            await foreach (var frame in subscription.Frames.ReadAllAsync(cancellationToken))
            {
                batch.Clear();
                Stream.Packetize(frame, batch);
                await transport.SendAsync(batch, isRtcp: false, cancellationToken);

                if (Environment.TickCount64 >= nextSenderReport)
                {
                    batch.Clear();
                    Stream.WriteSenderReport(batch, canonicalName);
                    await transport.SendAsync(batch, isRtcp: true, cancellationToken);
                    nextSenderReport = Environment.TickCount64 + (long)SenderReportInterval.TotalMilliseconds;
                }
            }
            logger.LogInformation("RTSP session {Session}: camera {Camera} is gone", Id, cameraId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (NotSupportedException ex)
        {
            logger.LogError(ex, "RTSP session {Session}: camera {Camera} produces frames RTP/JPEG cannot carry", Id, cameraId);
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
