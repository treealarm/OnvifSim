using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using OnvifSim.Ptz;
using OnvifSim.Recording;
using OnvifSim.Rendering;
using OnvifSim.Rtsp;
using OnvifSim.Rtsp.Rtp;
using OnvifSim.Streaming.H264;
using OnvifSim.World;

namespace OnvifSim.Streaming;

// A camera's recording as RTSP streams it for ONVIF replay: rtsp://host:port/{cameraId}/replay, the
// address GetReplayUri hands out, with one video track. No frame was ever stored (RecordingLog), so
// every session draws the past again at the camera's frame rate from the start of the PLAY's clock
// range, and encodes it with an encoder of its own: H.264 when ffmpeg is there, JPEG otherwise.
//
// What a replay client relies on (ONVIF Streaming Specification, 6): the first frame is the key frame at
// or before the start asked for, as on a real card, where footage can only be decoded from a key frame:
// the recording keys every GOV length frames from where it began, so a start between two key frames
// gets up to a GOP of footage from before it, and only the replay header says how much. RTP time runs
// with recording time, so across a hole in the recording it jumps by the hole, and a new key frame
// follows; every packet carries the replay header extension with the frame's time. When the range or the recording ends the playback returns, which ends the session
// and, over TCP, closes the connection: how the client learns it has everything.
public sealed class ReplayStreams(
    SceneState scene, RecordingLog recordings, PtzSimulator ptz, H264Support h264, TimeProvider time, ILoggerFactory loggers)
{
    public const string PathSegment = "replay";
    private const string VideoTrackName = "track1";
    // Constrained Baseline, level 4.1, as FfmpegH264Encoder asks x264 for; the stream repeats SPS and
    // PPS before every key frame, the first frame among them.
    private const string ProfileLevelId = "42C029";

    private readonly ILogger _logger = loggers.CreateLogger<ReplayStreams>();

    // One frame as the replay sends it: when it was recorded, and where it stands in the recording.
    private readonly record struct FrameInfo(DateTimeOffset Time, bool Discontinuity, bool EndOfSection, bool Terminal);

    public RtspTrack VideoTrack(CameraModel camera)
    {
        string id = camera.Id;
        if (h264.FfmpegPath is { } ffmpeg)
            return new RtspTrack(VideoTrackName, "video", H264Packetizer.PayloadType, "H264",
                (sender, cancellationToken) => ReplayAsync(id, sender, (spans, s, c) => SendH264Async(ffmpeg, spans, s, c), cancellationToken))
            {
                FormatParameters = H264Packetizer.FormatParameters(ProfileLevelId),
                FrameRate = camera.H264.Fps,
            };
        return new RtspTrack(VideoTrackName, "video", JpegPacketizer.PayloadType, "JPEG",
            (sender, cancellationToken) => ReplayAsync(id, sender, SendJpegAsync, cancellationToken))
        {
            FrameRate = camera.Video.Fps,
        };
    }

    private delegate Task SendFrames(IReadOnlyList<Section> sections, Context context, CancellationToken cancellationToken);

    // A stretch of the requested range the camera recorded without a break. Its frames are made as they
    // are sent (FramesOf): a range may reach back weeks.
    internal sealed record Section(DateTimeOffset Start, DateTimeOffset End, bool AfterGap, bool Last);

    // What one playback renders with: the world as it was when PLAY came, the camera in it, and the
    // clock its frames are paced and stamped by.
    private sealed class Context(SceneState.Snapshot snapshot, CameraModel camera, RtpSender sender, DateTimeOffset from, TimeProvider time)
    {
        // Stopwatch timestamp of the range's start: a frame recorded at t is stamped origin + (t - from),
        // which makes its RTP time follow the recording.
        private readonly long _origin = Stopwatch.GetTimestamp();
        private readonly long _startedAt = time.GetTimestamp();

        public SceneState.Snapshot Snapshot => snapshot;
        public CameraModel Camera => camera;
        public RtpSender Sender => sender;

        public long CaptureTimestampOf(DateTimeOffset frameTime) =>
            _origin + (long)((frameTime - from).TotalSeconds * Stopwatch.Frequency);

        // With Rate-Control on, a frame goes out no earlier than its place in the recording, measured
        // from when the playback began; off, as fast as the client takes them.
        public Task PaceAsync(DateTimeOffset frameTime, CancellationToken cancellationToken)
        {
            if (!sender.Request.RateControl) return Task.CompletedTask;
            var due = frameTime - from - time.GetElapsedTime(_startedAt);
            return due > TimeSpan.Zero ? Task.Delay(due, time, cancellationToken) : Task.CompletedTask;
        }

        public void Stamp(FrameInfo frame, bool cleanPoint) =>
            sender.Stream.Replay = new ReplayHeader(frame.Time, cleanPoint, frame.Discontinuity, frame.EndOfSection, frame.Terminal, sender.Request.CSeq);
    }

    private async Task ReplayAsync(string cameraId, RtpSender sender, SendFrames send, CancellationToken cancellationToken)
    {
        var snapshot = scene.Current;
        var camera = snapshot.World.Cameras.FirstOrDefault(c => string.Equals(c.Id, cameraId, StringComparison.OrdinalIgnoreCase));
        if (camera == null) return;
        var request = sender.Request;
        var spans = recordings.SpansOf(camera);
        var from = request.From ?? spans.FirstOrDefault().From;
        var to = request.To ?? DateTimeOffset.MaxValue;
        var sections = Sections(spans, from, to, KeyFrameInterval(camera));
        if (sections.Count == 0)
        {
            _logger.LogInformation("Camera {Camera}: nothing recorded in {Range}", cameraId, request.RangeHeader);
            return;
        }
        // From the first frame sent: a range that starts in a hole starts where the recording resumes, one
        // that starts between key frames at the key frame before it.
        var first = sections[0].Start;
        _logger.LogInformation("Camera {Camera}: replaying {From:O} to {To:O}", cameraId, first, sections[^1].End);
        await send(sections, new Context(snapshot, camera, sender, first, time), cancellationToken);
        _logger.LogInformation("Camera {Camera}: replay of {Range} done", cameraId, request.RangeHeader);
    }

    // How far apart the recording's key frames are: a GOV length of H.264 frames, or every JPEG frame.
    private TimeSpan KeyFrameInterval(CameraModel camera) => h264.FfmpegPath != null
        ? TimeSpan.FromSeconds((double)Math.Max(1, camera.H264.GovLength) / Math.Clamp(camera.H264.Fps, 1, 60))
        : TimeSpan.FromSeconds(1.0 / Math.Clamp(camera.Video.Fps, 1, 60));

    // The parts of the range the camera recorded, the first starting at the last key frame at or before the
    // range's start. A span keys from where it begins, as an encoder started with the recording would; of a
    // span the card has partly recorded over, from where what is left of it begins.
    internal static List<Section> Sections(IReadOnlyList<RecordedSpan> spans, DateTimeOffset from, DateTimeOffset to, TimeSpan keyFrameInterval)
    {
        var sections = new List<Section>();
        foreach (var span in spans)
        {
            var start = span.From >= from ? span.From : span.From + keyFrameInterval * Math.Floor((from - span.From) / keyFrameInterval);
            var end = span.To < to ? span.To : to;
            if (end > start) sections.Add(new Section(start, end, AfterGap: sections.Count > 0, Last: false));
        }
        if (sections.Count > 0) sections[^1] = sections[^1] with { Last = true };
        return sections;
    }

    // One frame every 1/fps from the section's start. The first after a gap says so (D); the last before
    // a gap, or of the whole range, says that too (E, T).
    private static IEnumerable<FrameInfo> FramesOf(Section section, int fps)
    {
        var step = TimeSpan.FromSeconds(1.0 / fps);
        long count = Math.Max(1, (long)Math.Ceiling((section.End - section.Start) / step));
        for (long i = 0; i < count; i++)
        {
            bool last = i == count - 1;
            yield return new FrameInfo(section.Start + step * i, section.AfterGap && i == 0, last, last && section.Last);
        }
    }

    private Frame Render(CachingFrameRenderer renderer, Context context, DateTimeOffset at, int width, int height)
    {
        var camera = context.Camera;
        var head = camera.Kind == CameraKind.Ptz && camera.Ptz != null ? ptz.StateAt(camera, at).Position : default;
        return renderer.RenderFrameAt(context.Snapshot, camera, head, width, height, at);
    }

    private async Task SendJpegAsync(IReadOnlyList<Section> sections, Context context, CancellationToken cancellationToken)
    {
        var at = DateTimeOffset.MinValue;
        var renderer = new CachingFrameRenderer(time, c => ptz.StateAt(c, at));
        var video = context.Camera.Video;
        var batch = new RtpPacketBatch();
        foreach (var frame in sections.SelectMany(s => FramesOf(s, Math.Clamp(video.Fps, 1, 60))))
        {
            at = frame.Time;
            var jpeg = JpegEncoder.Encode(Render(renderer, context, at, video.Width, video.Height), video.Quality);
            await context.PaceAsync(frame.Time, cancellationToken);
            batch.Clear();
            context.Stamp(frame, cleanPoint: true);
            JpegPacketizer.Packetize(context.Sender.Stream, jpeg, context.CaptureTimestampOf(frame.Time), batch);
            await context.Sender.SendAsync(batch, cancellationToken);
        }
    }

    // ffmpeg returns one access unit per frame, in order, so the frames' descriptions wait in a queue
    // beside it. Each section gets an encoder of its own: its first frame is then a key frame.
    private async Task SendH264Async(string ffmpeg, IReadOnlyList<Section> sections, Context context, CancellationToken cancellationToken)
    {
        var at = DateTimeOffset.MinValue;
        var renderer = new CachingFrameRenderer(time, c => ptz.StateAt(c, at));
        var settings = context.Camera.H264;
        var encoded = Channel.CreateUnbounded<(H264AccessUnit Unit, FrameInfo Frame)>(new UnboundedChannelOptions { SingleReader = true });
        var sending = SendEncodedAsync(encoded.Reader, context, cancellationToken);
        try
        {
            foreach (var section in sections)
            {
                // Written by this loop, read by the encoder's output reader.
                var pending = new ConcurrentQueue<FrameInfo>();
                var encoder = new FfmpegH264Encoder(ffmpeg, settings, unit =>
                {
                    if (pending.TryDequeue(out var frame)) encoded.Writer.TryWrite((unit, frame));
                }, _logger);
                try
                {
                    foreach (var frame in FramesOf(section, Math.Clamp(settings.Fps, 1, 60)))
                    {
                        at = frame.Time;
                        await context.PaceAsync(frame.Time, cancellationToken);
                        var picture = Render(renderer, context, at, settings.Width, settings.Height);
                        pending.Enqueue(frame);
                        await encoder.WriteFrameAsync(picture.Rgba, context.CaptureTimestampOf(frame.Time), cancellationToken);
                    }
                }
                finally
                {
                    // Lets ffmpeg flush the section's last frames before the next section starts.
                    await encoder.DisposeAsync();
                }
            }
        }
        finally
        {
            encoded.Writer.TryComplete();
        }
        await sending;
    }

    private static async Task SendEncodedAsync(ChannelReader<(H264AccessUnit Unit, FrameInfo Frame)> encoded, Context context, CancellationToken cancellationToken)
    {
        var batch = new RtpPacketBatch();
        await foreach (var (unit, frame) in encoded.ReadAllAsync(cancellationToken))
        {
            batch.Clear();
            context.Stamp(frame, unit.IsKeyFrame);
            H264Packetizer.Packetize(context.Sender.Stream, unit.Nals, unit.CaptureTimestamp, batch);
            await context.Sender.SendAsync(batch, cancellationToken);
        }
    }
}
