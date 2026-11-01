namespace OnvifSim.World;

// Reading a camera's streams: the explicit list when there is one, otherwise the legacy pair made from
// Video and H264. The legacy streams point at the camera's own settings, so an ONVIF change writes back
// where it always did and old world files keep working without a migration.
public static class CameraStreamsModel
{
    public const string JpegToken = "Profile_1";
    public const string H264Token = "Profile_2";
    public const int MaxStreams = 8;

    public static List<StreamModel> Effective(CameraModel camera)
    {
        if (camera.Streams is { } streams) return streams;
        return
        [
            new StreamModel
            {
                Token = JpegToken, Name = "MainStream", Codec = StreamCodec.Jpeg, Path = camera.Id, Video = camera.Video,
            },
            new StreamModel
            {
                Token = H264Token, Name = "H264Stream", Codec = StreamCodec.H264, Path = camera.Id + "/h264", H264 = camera.H264,
            },
        ];
    }

    public static StreamModel? Find(CameraModel camera, string? token) =>
        string.IsNullOrEmpty(token)
            ? null
            : Effective(camera).FirstOrDefault(stream => string.Equals(stream.Token, token, StringComparison.OrdinalIgnoreCase));

    public static string EncoderTokenOf(StreamModel stream) =>
        stream.Token.StartsWith("Profile_", StringComparison.OrdinalIgnoreCase)
            ? "VideoEncoder_" + stream.Token["Profile_".Length..]
            : "VideoEncoder_" + stream.Token;

    // Settings of a stream: its own, or the camera's own for a legacy stream.
    public static VideoModel VideoOf(CameraModel camera, StreamModel stream) => stream.Video ?? camera.Video;

    public static H264Model H264Of(CameraModel camera, StreamModel stream) => stream.H264 ?? camera.H264;

    public static bool IsVisible(StreamModel stream, bool hasH264) => stream.Codec != StreamCodec.H264 || hasH264;

    // The stream a recording and its replay use: H.264 when available, JPEG otherwise, or whatever is
    // left when a camera has only one kind.
    public static StreamModel? RecordingStream(CameraModel camera, bool hasH264)
    {
        var visible = Effective(camera).Where(stream => IsVisible(stream, hasH264)).ToList();
        return visible.FirstOrDefault(stream => stream.Codec == (hasH264 ? StreamCodec.H264 : StreamCodec.Jpeg))
               ?? visible.FirstOrDefault();
    }

    public static int EffectiveRtspPort(CameraModel camera, int defaultPort) =>
        camera.RtspPort != 0 ? camera.RtspPort : defaultPort;

    public static string ReplayPath(CameraModel camera) => camera.Id + "/replay";
}
