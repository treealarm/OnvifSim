using System.Diagnostics;
using OnvifSim.Analytics;
using OnvifSim.Onvif;
using OnvifSim.Recording;
using OnvifSim.Rtsp;
using OnvifSim.Rtsp.Rtp;
using OnvifSim.Security;
using OnvifSim.Streaming.H264;
using OnvifSim.World;

namespace OnvifSim.Streaming;

// The cameras as the RTSP server streams them. Each stream's own path is its address (by default
// {cameraId} for the JPEG stream, {cameraId}/h264 for the H.264 one); {stream}/track1 is its video and
// {stream}/track2 the camera's ONVIF metadata (analytics boxes, PTZ status). {cameraId}/replay is the
// camera's own recording (ReplayStreams). A stream is served only on its camera's RTSP listener. Only
// the camera's users may play them, and nobody while the camera's network is down.
public sealed class CameraStreams(
    SceneState scene, FeedManager feeds, AnalyticsManager analytics, ReplayStreams replay, CameraNetwork network,
    HttpAuthenticator authenticator, RtspEndpoints endpoints)
    : IRtspMediaSource
{
    private const string VideoTrackName = "track1";
    private const string MetadataTrackName = "track2";
    // Constrained Baseline, level 4.1: what FfmpegH264Encoder asks x264 for. Taken from the SPS itself
    // once the camera has encoded a key frame.
    private const string DefaultProfileLevelId = "42C029";
    // When there is nothing new to tell, the metadata track still sends a document this often: ffmpeg
    // (and so many NVRs) waits for a packet on every track before showing anything.
    private static readonly TimeSpan MetadataHeartbeat = TimeSpan.FromSeconds(1);

    public RtspMedia? Find(IReadOnlyList<string> path, int localPort)
    {
        if (Resolve(path, localPort) is not { } resolved || !network.IsOnline(resolved.Camera.Id)) return null;
        var (camera, stream, isReplay) = resolved;
        if (isReplay) return new RtspMedia(DisplayName(camera), [replay.VideoTrack(camera)]);
        if (stream == null) return null;
        if (stream.Codec == StreamCodec.H264 && !feeds.H264Available) return null;
        return new RtspMedia(DisplayName(camera), [VideoTrack(camera, stream), MetadataTrack(camera.Id)]);
    }

    public bool IsAuthorized(RtspRequest request, IReadOnlyList<string> path, int localPort)
    {
        var resolved = Resolve(path, localPort);
        return resolved is { } r
               && authenticator.IsAuthorized(request.Method, request.Header("Authorization"), r.Camera.Users);
    }

    public IEnumerable<string> Challenges() => authenticator.Challenges();

    // Which camera owns a stream or replay URL on a listener, for a session that must be cut off.
    public string? CameraIdOf(int localPort, IReadOnlyList<string> path) => Resolve(path, localPort)?.Camera.Id;

    // Whether the URL still names a stream, for closing sessions of a stream that was deleted.
    public bool Exists(WorldModel world, int localPort, IReadOnlyList<string> path) => Resolve(world, path, localPort) != null;

    // The camera and stream a URL names on a listener, or null. The replay path is the camera's id
    // followed by replay; stream paths are the ones the camera's world entry gives.
    private (CameraModel Camera, StreamModel? Stream, bool IsReplay)? Resolve(IReadOnlyList<string> path, int localPort) =>
        Resolve(scene.Current.World, path, localPort);

    private (CameraModel Camera, StreamModel? Stream, bool IsReplay)? Resolve(WorldModel world, IReadOnlyList<string> path, int localPort)
    {
        string text = string.Join('/', path);
        foreach (var camera in world.Cameras)
        {
            if (endpoints.PortOf(camera.Id) != localPort) continue;
            if (string.Equals(text, CameraStreamsModel.ReplayPath(camera), StringComparison.OrdinalIgnoreCase))
                return (camera, null, true);
            var stream = CameraStreamsModel.Effective(camera)
                .FirstOrDefault(candidate => string.Equals(candidate.Path, text, StringComparison.OrdinalIgnoreCase));
            if (stream != null) return (camera, stream, false);
        }
        return null;
    }

    private static string DisplayName(CameraModel camera) => camera.Name.Length > 0 ? camera.Name : camera.Id;

    private RtspTrack VideoTrack(CameraModel camera, StreamModel stream)
    {
        if (stream.Codec == StreamCodec.Jpeg)
        {
            var video = CameraStreamsModel.VideoOf(camera, stream);
            return new RtspTrack(VideoTrackName, "video", JpegPacketizer.PayloadType, "JPEG", (sender, cancellationToken) =>
                feeds.Get(camera.Id, stream.Token) is { } feed ? SendJpegAsync(feed, sender, cancellationToken) : Task.CompletedTask)
            {
                FrameRate = video.Fps,
            };
        }

        // SPS and PPS go into the SDP when the camera has already encoded a key frame at its current
        // settings; before that clients take them from the stream, where they precede every key frame.
        var h264 = CameraStreamsModel.H264Of(camera, stream);
        var parameterSets = feeds.GetH264(camera.Id, stream.Token)?.ParameterSetsFor(h264);
        return new RtspTrack(VideoTrackName, "video", H264Packetizer.PayloadType, "H264", (sender, cancellationToken) =>
            feeds.GetH264(camera.Id, stream.Token) is { } feed ? SendH264Async(feed, sender, cancellationToken) : Task.CompletedTask)
        {
            FormatParameters = parameterSets is var (sps, pps)
                ? H264Packetizer.FormatParameters(sps, pps)
                : H264Packetizer.FormatParameters(DefaultProfileLevelId),
            FrameRate = h264.Fps,
        };
    }

    private RtspTrack MetadataTrack(string cameraId) =>
        new(MetadataTrackName, "application", MetadataPacketizer.PayloadType, MetadataPacketizer.EncodingName, (sender, cancellationToken) =>
            analytics.Get(cameraId) is { } camera ? SendMetadataAsync(camera, sender, cancellationToken) : Task.CompletedTask);

    private static async Task SendJpegAsync(CameraFeed feed, RtpSender sender, CancellationToken cancellationToken)
    {
        using var subscription = feed.Subscribe();
        var batch = new RtpPacketBatch();
        await foreach (var frame in subscription.Frames.ReadAllAsync(cancellationToken))
        {
            batch.Clear();
            JpegPacketizer.Packetize(sender.Stream, frame.Jpeg, frame.CaptureTimestamp, batch);
            await sender.SendAsync(batch, cancellationToken);
        }
    }

    private static async Task SendH264Async(H264Feed feed, RtpSender sender, CancellationToken cancellationToken)
    {
        using var subscription = feed.Subscribe();
        var batch = new RtpPacketBatch();
        await foreach (var unit in subscription.Frames.ReadAllAsync(cancellationToken))
        {
            batch.Clear();
            H264Packetizer.Packetize(sender.Stream, unit.Nals, unit.CaptureTimestamp, batch);
            await sender.SendAsync(batch, cancellationToken);
        }
    }

    // One document for every look worth telling (IsWorthSending), the first one at once.
    private static async Task SendMetadataAsync(CameraAnalytics camera, RtpSender sender, CancellationToken cancellationToken)
    {
        using var looks = camera.Subscribe();
        var batch = new RtpPacketBatch();
        AnalyticsFrame? previous = null;
        var lastSent = DateTimeOffset.MinValue;
        await foreach (var frame in looks.Frames.ReadAllAsync(cancellationToken))
        {
            if (IsWorthSending(frame, previous, lastSent))
            {
                batch.Clear();
                MetadataPacketizer.Packetize(sender.Stream, MetadataDocument.Write(frame), Stopwatch.GetTimestamp(), batch);
                await sender.SendAsync(batch, cancellationToken);
                lastSent = frame.UtcTime;
            }
            previous = frame;
        }
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
}
