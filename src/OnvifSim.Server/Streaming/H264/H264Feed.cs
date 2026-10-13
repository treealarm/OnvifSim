using System.Diagnostics;
using System.Threading.Channels;
using OnvifSim.Ptz;
using OnvifSim.World;

namespace OnvifSim.Streaming.H264;

// A camera's H.264 stream. Like CameraFeed it runs only while somebody watches: the first subscriber
// starts ffmpeg and the frame loop, the last one leaving stops both. Frames are rendered on the same
// evenly spaced grid as the JPEG stream and piped to ffmpeg as RGBA; a change of the H.264 settings
// restarts the encoder without ending the stream.
//
// Unlike JPEG frames, access units depend on the ones before them, so a subscriber must not lose one
// in the middle of a group of pictures: a new subscriber starts at the next key frame, and one too slow
// to keep its queue from filling up skips to the next key frame rather than decode garbage.
public sealed class H264Feed(string cameraId, SceneState scene, PtzSimulator ptz, string ffmpegPath, ILogger logger)
{
    // About two seconds at 30 fps; a reader that falls further behind skips ahead.
    private const int SubscriberQueueLength = 64;

    private readonly Lock _gate = new();
    private readonly List<Subscriber> _subscribers = [];
    private readonly CachingFrameRenderer _renderer = new();
    private CancellationTokenSource? _cts;
    private (H264Model Settings, byte[] Sps, byte[] Pps)? _parameterSets;

    public string CameraId => cameraId;

    // SPS and PPS of the last key frame encoded at these settings, for the SDP; null until one exists.
    public (byte[] Sps, byte[] Pps)? ParameterSetsFor(H264Model settings)
    {
        lock (_gate)
            return _parameterSets is { } p && p.Settings == settings ? (p.Sps, p.Pps) : null;
    }

    public Subscription Subscribe()
    {
        var subscriber = new Subscriber();
        lock (_gate)
        {
            _subscribers.Add(subscriber);
            if (_cts == null)
            {
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _ = Task.Run(() => RunAsync(token), CancellationToken.None);
            }
        }
        return new Subscription(this, subscriber);
    }

    private void Unsubscribe(Subscriber subscriber)
    {
        lock (_gate)
        {
            if (!_subscribers.Remove(subscriber)) return;
            subscriber.Channel.Writer.TryComplete();
            if (_subscribers.Count == 0)
            {
                _cts?.Cancel();
                _cts = null;
            }
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        logger.LogInformation("Camera {Camera}: H.264 stream started", cameraId);
        FfmpegH264Encoder? encoder = null;
        long next = Stopwatch.GetTimestamp();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var snap = scene.Current;
                var cam = snap.World.Cameras.FirstOrDefault(c => string.Equals(c.Id, cameraId, StringComparison.OrdinalIgnoreCase));
                if (cam == null) break;

                var settings = cam.H264;
                if (encoder == null || encoder.Settings != settings || encoder.HasExited)
                {
                    if (encoder != null) await encoder.DisposeAsync();
                    encoder = new FfmpegH264Encoder(ffmpegPath, settings, unit => Publish(unit, settings), logger);
                }
                var frame = _renderer.RenderFrame(snap, cam, ptz.PositionOf(cam), settings.Width, settings.Height);
                await encoder.WriteFrameAsync(frame.Rgba, next, token);

                // Fixed-rate schedule; if rendering or encoding falls behind, skip ahead instead of bursting.
                next += Stopwatch.Frequency / Math.Clamp(settings.Fps, 1, 60);
                long now = Stopwatch.GetTimestamp();
                if (next < now) next = now;
                await Task.Delay(Stopwatch.GetElapsedTime(now, next), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // ffmpeg could not be started or died: the pipe broke under the write.
            logger.LogError(ex, "Camera {Camera}: H.264 encoder failed", cameraId);
        }
        finally
        {
            if (encoder != null) await encoder.DisposeAsync();
        }
        // Cancelled means the last subscriber left, and a new one may already have started the next run;
        // only a stream that ended by itself has subscribers to let go.
        if (!token.IsCancellationRequested) CompleteAll();
        logger.LogInformation("Camera {Camera}: H.264 stream stopped", cameraId);
    }

    // Runs on the encoder's output reader, one access unit at a time. TryWrite fails only on a full
    // queue (FullMode.Wait), which is what sends a subscriber back to waiting for a key frame.
    private void Publish(H264AccessUnit unit, H264Model settings)
    {
        bool key = unit.IsKeyFrame;
        lock (_gate)
        {
            if (key) RememberParameterSets(unit, settings);
            foreach (var subscriber in _subscribers)
            {
                if (subscriber.WaitingForKeyFrame && !key) continue;
                subscriber.WaitingForKeyFrame = !subscriber.Channel.Writer.TryWrite(unit);
            }
        }
    }

    private void RememberParameterSets(H264AccessUnit unit, H264Model settings)
    {
        var sps = unit.Nals.FirstOrDefault(n => AnnexB.NalType(n.Span) == AnnexB.Sps);
        var pps = unit.Nals.FirstOrDefault(n => AnnexB.NalType(n.Span) == AnnexB.Pps);
        if (sps.IsEmpty || pps.IsEmpty) return;
        _parameterSets = (settings, sps.ToArray(), pps.ToArray());
    }

    // The stream ended by itself (camera removed, encoder failed): subscribers see their channel close.
    private void CompleteAll()
    {
        lock (_gate)
        {
            foreach (var subscriber in _subscribers) subscriber.Channel.Writer.TryComplete();
            _subscribers.Clear();
            _cts?.Cancel();
            _cts = null;
        }
    }

    internal sealed class Subscriber
    {
        public Channel<H264AccessUnit> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<H264AccessUnit>(
            new BoundedChannelOptions(SubscriberQueueLength) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        public bool WaitingForKeyFrame { get; set; } = true;
    }

    public sealed class Subscription : IDisposable
    {
        private readonly H264Feed _feed;
        private readonly Subscriber _subscriber;

        internal Subscription(H264Feed feed, Subscriber subscriber)
        {
            _feed = feed;
            _subscriber = subscriber;
        }

        public ChannelReader<H264AccessUnit> Frames => _subscriber.Channel.Reader;
        public void Dispose() => _feed.Unsubscribe(_subscriber);
    }
}
