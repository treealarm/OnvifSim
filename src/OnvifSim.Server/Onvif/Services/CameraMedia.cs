using OnvifSim.Onvif.Soap;
using OnvifSim.Onvif.Wsdl;
using OnvifSim.Streaming;
using OnvifSim.World;

namespace OnvifSim.Onvif.Services;

// The media configuration every simulated camera exposes through Media1 and Media2 alike: one video
// source and one fixed profile per encoder. Profile_1 with the JPEG encoder is always there; Profile_2
// with the H.264 encoder is there when ffmpeg is. The encoder settings are the camera's VideoModel and
// H264Model in world.json, so a change made over ONVIF is what the streams, the snapshot and the editor
// show.
public static class CameraMedia
{
    public const string JpegProfileToken = "Profile_1";
    public const string H264ProfileToken = "Profile_2";
    public const string JpegEncoderToken = "VideoEncoder_1";
    public const string H264EncoderToken = "VideoEncoder_2";
    public const string VideoSourceToken = "VideoSource_1";
    public const string VideoSourceConfigurationToken = "VideoSourceConfig_1";
    public const string SessionTimeout = "PT60S";

    public const int MinFrameRate = 1;
    public const int MaxFrameRate = 30;
    public const int MinQuality = 1;
    public const int MaxQuality = 100;
    public const int MinBitrateKbps = 64;
    public const int MaxBitrateKbps = 16384;
    public const int MinGovLength = 1;
    public const int MaxGovLength = 300;
    // x264 works to the bitrate, so quality means nothing for H.264; this is what its configuration
    // reports, and whatever a client sets is accepted and ignored.
    public const float H264Quality = 50;

    // Media2 lists frame rates instead of a range; any whole rate in the range is accepted anyway.
    public static readonly float[] FrameRates = [1, 2, 3, 5, 7, 10, 12, 15, 20, 25, 30];

    // RTP/JPEG carries sizes in 8-pixel blocks up to 2040 (RFC 2435) and x264 wants even sizes, so every
    // size here fits both.
    private static readonly (int Width, int Height)[] StandardResolutions =
        [(320, 240), (640, 360), (640, 480), (1280, 720), (1920, 1080)];

    public static IReadOnlyList<VideoCodec> Codecs(OnvifRequestContext context) =>
        context.HasH264 ? [VideoCodec.Jpeg, VideoCodec.H264] : [VideoCodec.Jpeg];

    public static string ProfileTokenOf(VideoCodec codec) => codec == VideoCodec.H264 ? H264ProfileToken : JpegProfileToken;

    public static string ProfileNameOf(VideoCodec codec) => codec == VideoCodec.H264 ? "H264Stream" : "MainStream";

    public static string EncoderTokenOf(VideoCodec codec) => codec == VideoCodec.H264 ? H264EncoderToken : JpegEncoderToken;

    // The codec of a profile the camera has; NoProfile for any other token.
    public static VideoCodec CodecOfProfile(OnvifRequestContext context, string? token)
    {
        foreach (var codec in Codecs(context))
            if (ProfileTokenOf(codec) == token) return codec;
        throw OnvifFault.NoProfile(token ?? "");
    }

    // The codec of an encoder configuration the camera has; NoConfig for any other token.
    public static VideoCodec CodecOfEncoder(OnvifRequestContext context, string? token)
    {
        foreach (var codec in Codecs(context))
            if (EncoderTokenOf(codec) == token) return codec;
        throw OnvifFault.NoConfig(token ?? "");
    }

    // The encoders a request with optional configuration and profile tokens is about: the one named, or
    // the one of the profile named, or all of them.
    public static IReadOnlyList<VideoCodec> SelectCodecs(OnvifRequestContext context, string? encoderToken, string? profileToken)
    {
        VideoCodec? byProfile = string.IsNullOrEmpty(profileToken) ? null : CodecOfProfile(context, profileToken);
        if (!string.IsNullOrEmpty(encoderToken)) return [CodecOfEncoder(context, encoderToken)];
        return byProfile is { } codec ? [codec] : Codecs(context);
    }

    // An optional profile token: absent, or one of the camera's profiles.
    public static void CheckProfile(OnvifRequestContext context, string? token)
    {
        if (!string.IsNullOrEmpty(token)) CodecOfProfile(context, token);
    }

    public static void CheckVideoSourceConfiguration(string? token)
    {
        if (!string.IsNullOrEmpty(token) && token != VideoSourceConfigurationToken) throw OnvifFault.NoConfig(token);
    }

    public static string StreamUriOf(OnvifRequestContext context, VideoCodec codec) =>
        codec == VideoCodec.H264 ? context.StreamUri + "/h264" : context.StreamUri;

    // The standard sizes, plus the encoder's own if world.json sets another one.
    public static IReadOnlyList<(int Width, int Height)> Resolutions(int width, int height) =>
        StandardResolutions.Contains((width, height)) ? StandardResolutions : [.. StandardResolutions, (width, height)];

    // MJPEG has no bitrate control; this is what such a stream roughly takes, for clients that want a number.
    public static int EstimatedBitrateKbps(VideoModel video) =>
        Math.Max(MinBitrateKbps, (int)(video.Width * (long)video.Height * video.Fps * 3 / 4 / 1000));

    // The one video source, used by every profile.
    public static VideoSourceConfiguration VideoSourceConfiguration(OnvifRequestContext context) => new()
    {
        token = VideoSourceConfigurationToken,
        Name = "VideoSourceConfig",
        UseCount = Codecs(context).Count,
        SourceToken = VideoSourceToken,
        Bounds = new IntRectangle { x = 0, y = 0, width = context.Camera.Video.Width, height = context.Camera.Video.Height },
    };

    // Encoder configurations must carry a multicast section; this one is unused.
    public static MulticastConfiguration NoMulticast() => new()
    {
        Address = new IPAddress { Type = IPType.IPv4, IPv4Address = "0.0.0.0" },
        Port = 0,
        TTL = 0,
        AutoStart = false,
    };

    // No OSD slots: clients see that they cannot add one.
    public static OSDConfigurationOptions NoOsdOptions() => new()
    {
        MaximumNumberOfOSDs = new MaximumNumberOfOSDs { Total = 0 },
        Type = [OSDType.Text],
        PositionOption = ["UpperLeft"],
    };

    // SetVideoEncoderConfiguration of the JPEG encoder, either Media version. Out-of-range values are
    // refused with ConfigModify, as the options told the client what is allowed.
    public static void ApplyJpegSettings(OnvifRequestContext context, string encoding, int width, int height, float frameRate, float quality)
    {
        CheckEncoding(encoding, "JPEG", JpegEncoderToken);
        var current = context.Camera.Video;
        CheckResolution(width, height, Resolutions(current.Width, current.Height));
        int fps = CheckedFrameRate(frameRate);
        int jpegQuality = (int)MathF.Round(quality);
        if (jpegQuality is < MinQuality or > MaxQuality)
            throw OnvifFault.ConfigModify($"Quality {quality} is outside {MinQuality}..{MaxQuality}.");

        context.Store.Update(world =>
        {
            var video = world.Cameras.Single(c => c.Id == context.Camera.Id).Video;
            (video.Width, video.Height, video.Fps, video.Quality) = (width, height, fps, jpegQuality);
        });
    }

    // SetVideoEncoderConfiguration of the H.264 encoder; a running stream picks the change up by
    // restarting its encoder. Absent bitrate or GOV length keep their current values.
    public static void ApplyH264Settings(OnvifRequestContext context, string encoding, int width, int height, float frameRate, int? bitrateKbps, int? govLength)
    {
        CheckEncoding(encoding, "H264", H264EncoderToken);
        var current = context.Camera.H264;
        CheckResolution(width, height, Resolutions(current.Width, current.Height));
        int fps = CheckedFrameRate(frameRate);
        int bitrate = bitrateKbps ?? current.BitrateKbps;
        if (bitrate is < MinBitrateKbps or > MaxBitrateKbps)
            throw OnvifFault.ConfigModify($"Bitrate {bitrate} kbit/s is outside {MinBitrateKbps}..{MaxBitrateKbps}.");
        int gov = govLength ?? current.GovLength;
        if (gov is < MinGovLength or > MaxGovLength)
            throw OnvifFault.ConfigModify($"GOV length {gov} is outside {MinGovLength}..{MaxGovLength}.");

        context.Store.Update(world =>
            world.Cameras.Single(c => c.Id == context.Camera.Id).H264 = new H264Model
            {
                Width = width, Height = height, Fps = fps, BitrateKbps = bitrate, GovLength = gov,
            });
    }

    private static void CheckEncoding(string encoding, string expected, string token)
    {
        if (!encoding.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw OnvifFault.ConfigModify($"{token} encodes {expected}, not {encoding}.");
    }

    private static void CheckResolution(int width, int height, IReadOnlyList<(int Width, int Height)> offered)
    {
        if (!offered.Contains((width, height))) throw OnvifFault.ConfigModify($"Resolution {width}x{height} is not offered.");
    }

    private static int CheckedFrameRate(float frameRate)
    {
        int fps = (int)MathF.Round(frameRate);
        return fps is >= MinFrameRate and <= MaxFrameRate
            ? fps
            : throw OnvifFault.ConfigModify($"Frame rate {frameRate} is outside {MinFrameRate}..{MaxFrameRate}.");
    }
}
