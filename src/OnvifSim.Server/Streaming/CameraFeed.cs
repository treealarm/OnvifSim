using System.Diagnostics;
using System.Threading.Channels;
using OnvifSim.Rendering;
using OnvifSim.World;

namespace OnvifSim.Streaming;

// CaptureTimestamp is the Stopwatch timestamp the frame was scheduled for: monotonic, so RTP time
// derived from it never jumps when the wall clock is adjusted, and evenly spaced at the frame rate.
public sealed record EncodedFrame(byte[] Jpeg, int Width, int Height, long CaptureTimestamp);

// Renders one camera only while somebody watches: the first subscriber starts the frame loop, the
// last one leaving stops it. Every subscriber gets the same frames through a one-slot channel that
// drops stale frames, so a slow client never holds back the others.
public sealed class CameraFeed(string cameraId, SceneState scene, ILogger logger)
{
    private readonly Lock _gate = new();
    private readonly List<Channel<EncodedFrame>> _subscribers = [];
    private CancellationTokenSource? _cts;

    public string CameraId => cameraId;

    public int SubscriberCount
    {
        get { lock (_gate) return _subscribers.Count; }
    }

    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<EncodedFrame>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true,
        });
        lock (_gate)
        {
            _subscribers.Add(channel);
            if (_cts == null)
            {
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _ = Task.Run(() => RunAsync(token), CancellationToken.None);
            }
        }
        return new Subscription(this, channel);
    }

    private void Unsubscribe(Channel<EncodedFrame> channel)
    {
        lock (_gate)
        {
            if (!_subscribers.Remove(channel)) return;
            channel.Writer.TryComplete();
            if (_subscribers.Count == 0)
            {
                _cts?.Cancel();
                _cts = null;
            }
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        logger.LogInformation("Camera {Camera}: stream started", cameraId);
        var renderer = new SoftwareRasterizer();
        Frame? frame = null;
        // Stopwatch timestamp the next frame is due at, and the one it is stamped with: frames sit on an
        // even grid like a real camera's, whatever the scheduling jitter.
        long next = Stopwatch.GetTimestamp();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var snap = scene.Current;
                var cam = snap.World.Cameras.FirstOrDefault(c => string.Equals(c.Id, cameraId, StringComparison.OrdinalIgnoreCase));
                if (cam == null)
                {
                    CompleteAll();
                    return;
                }
                var video = cam.Video;
                if (frame == null || frame.Width != video.Width || frame.Height != video.Height)
                    frame = new Frame(video.Width, video.Height);
                renderer.Render(snap.Scene, CameraPose.From(cam, snap.Scene.Terrain), frame);
                var encoded = new EncodedFrame(JpegEncoder.Encode(frame, video.Quality), frame.Width, frame.Height, next);
                lock (_gate)
                    foreach (var s in _subscribers)
                        s.Writer.TryWrite(encoded);

                // Fixed-rate schedule; if rendering falls behind, skip ahead instead of bursting.
                next += Stopwatch.Frequency / Math.Clamp(video.Fps, 1, 60);
                long now = Stopwatch.GetTimestamp();
                if (next < now) next = now;
                await Task.Delay(Stopwatch.GetElapsedTime(now, next), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Camera {Camera}: stream failed", cameraId);
            CompleteAll();
        }
        logger.LogInformation("Camera {Camera}: stream stopped", cameraId);
    }

    private void CompleteAll()
    {
        lock (_gate)
        {
            foreach (var s in _subscribers) s.Writer.TryComplete();
            _subscribers.Clear();
            _cts?.Cancel();
            _cts = null;
        }
    }

    public sealed class Subscription(CameraFeed feed, Channel<EncodedFrame> channel) : IDisposable
    {
        public ChannelReader<EncodedFrame> Frames => channel.Reader;
        public void Dispose() => feed.Unsubscribe(channel);
    }
}
