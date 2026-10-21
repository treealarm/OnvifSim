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

// The cameras as the RTSP server streams them. rtsp://host:port/{cameraId} is the JPEG stream and
// {cameraId}/h264 the H.264 one (when ffmpeg is available); {stream}/track1 is a stream's video and
// {stream}/track2 the camera's ONVIF metadata (analytics boxes, PTZ status). {cameraId}/replay is the
// camera's own recording (ReplayStreams). Only the camera's users may play them, and nobody while the
// camera's network is down.
public sealed class CameraStreams(
    SceneState scene, FeedManager feeds, AnalyticsManager analytics, ReplayStreams replay, CameraNetwork network, HttpAuthenticator authenticator)
    : IRtspMediaSource
{
    private const string VideoTrackName = "track1";
    private const string MetadataTrackName = "track2";
    private const string H264Path = "h264";
    // Constrained Baseline, level 4.1: what FfmpegH264Encoder asks x264 for. Taken from the SPS itself
    // once the camera has encoded a key frame.
    private const string DefaultProfileLevelId = "42C029";
    // When there is nothing new to tell, the metadata track still sends a document this often: ffmpeg
    // (and so many NVRs) waits for a packet on every track before showing anything.
    private static readonly TimeSpan MetadataHeartbeat = TimeSpan.FromSeconds(1);

    public RtspMedia? Find(IReadOnlyList<string> path)
    {
        if (path is not ([_] or [_, H264Path] or [_, ReplayStreams.PathSegment]) || scene.FindCamera(path[0]) is not { } camera
            || !network.IsOnline(camera.Id))
            return null;
        if (path is [_, ReplayStreams.PathSegment])
            return new RtspMedia($"{(camera.Name.Length > 0 ? camera.Name : camera.Id)} recording", [replay.VideoTrack(camera)]);
        var codec = path.Count == 2 ? VideoCodec.H264 : VideoCodec.Jpeg;
        if (codec == VideoCodec.H264 && !feeds.H264Available) return null;
        return new RtspMedia(camera.Name.Length > 0 ? camera.Name : camera.Id, [VideoTrack(camera, codec), MetadataTrack(camera.Id)]);
    }

    public bool IsAuthorized(RtspRequest request, IReadOnlyList<string> path) =>
        scene.FindCamera(path[0]) is { } camera && authenticator.IsAuthorized(request.Method, request.Header("Authorization"), camera.Users);

    public IEnumerable<string> Challenges() => authenticator.Challenges();

    private RtspTrack VideoTrack(CameraModel camera, VideoCodec codec)
    {
        string id = camera.Id;
        if (codec == VideoCodec.Jpeg)
            return new RtspTrack(VideoTrackName, "video", JpegPacketizer.PayloadType, "JPEG", (sender, cancellationToken) =>
                feeds.Get(id) is { } feed ? SendJpegAsync(feed, sender, cancellationToken) : Task.CompletedTask)
            {
                FrameRate = camera.Video.Fps,
            };

        // SPS and PPS go into the SDP when the camera has already encoded a key frame at its current
        // settings; before that clients take them from the stream, where they precede every key frame.
        var parameterSets = feeds.GetH264(id)?.ParameterSetsFor(camera.H264);
        return new RtspTrack(VideoTrackName, "video", H264Packetizer.PayloadType, "H264", (sender, cancellationToken) =>
            feeds.GetH264(id) is { } feed ? SendH264Async(feed, sender, cancellationToken) : Task.CompletedTask)
        {
            FormatParameters = parameterSets is var (sps, pps)
                ? H264Packetizer.FormatParameters(sps, pps)
                : H264Packetizer.FormatParameters(DefaultProfileLevelId),
            FrameRate = camera.H264.Fps,
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
