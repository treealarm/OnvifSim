using OnvifSim.Onvif.Soap;
using OnvifSim.Onvif.Wsdl;
using OnvifSim.World;

namespace OnvifSim.Onvif.Services;

// The media configuration every simulated camera exposes through Media1 and Media2 alike: one fixed
// profile with one video source and one JPEG encoder. The encoder settings are the camera's VideoModel
// in world.json, so a change made over ONVIF is what the stream, the snapshot and the editor show.
public static class CameraMedia
{
    public const string ProfileToken = "Profile_1";
    public const string ProfileName = "MainStream";
    public const string VideoSourceToken = "VideoSource_1";
    public const string VideoSourceConfigurationToken = "VideoSourceConfig_1";
    public const string VideoEncoderConfigurationToken = "VideoEncoder_1";
    public const string SessionTimeout = "PT60S";

    public const int MinFrameRate = 1;
    public const int MaxFrameRate = 30;
    public const int MinQuality = 1;
    public const int MaxQuality = 100;

    // Media2 lists frame rates instead of a range; any whole rate in the range is accepted anyway.
    public static readonly float[] FrameRates = [1, 2, 3, 5, 7, 10, 12, 15, 20, 25, 30];

    // RTP/JPEG carries sizes in 8-pixel blocks up to 2040 (RFC 2435), so every size here fits.
    private static readonly (int Width, int Height)[] StandardResolutions =
        [(320, 240), (640, 360), (640, 480), (1280, 720), (1920, 1080)];

    // The standard sizes, plus the camera's own if world.json sets another one.
    public static IReadOnlyList<(int Width, int Height)> Resolutions(CameraModel camera)
    {
        var current = (camera.Video.Width, camera.Video.Height);
        return StandardResolutions.Contains(current) ? StandardResolutions : [.. StandardResolutions, current];
    }

    // MJPEG has no bitrate control; this is what such a stream roughly takes, for clients that want a number.
    public static int EstimatedBitrateKbps(VideoModel video) =>
        Math.Max(64, (int)(video.Width * (long)video.Height * video.Fps * 3 / 4 / 1000));

    public static VideoSourceConfiguration VideoSourceConfiguration(CameraModel camera) => new()
    {
        token = VideoSourceConfigurationToken,
        Name = "VideoSourceConfig",
        UseCount = 1,
        SourceToken = VideoSourceToken,
        Bounds = new IntRectangle { x = 0, y = 0, width = camera.Video.Width, height = camera.Video.Height },
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

    public static void CheckProfile(string? token)
    {
        if (!string.IsNullOrEmpty(token) && token != ProfileToken) throw OnvifFault.NoProfile(token);
    }

    public static void CheckVideoEncoderConfiguration(string? token)
    {
        if (!string.IsNullOrEmpty(token) && token != VideoEncoderConfigurationToken) throw OnvifFault.NoConfig(token);
    }

    public static void CheckVideoSourceConfiguration(string? token)
    {
        if (!string.IsNullOrEmpty(token) && token != VideoSourceConfigurationToken) throw OnvifFault.NoConfig(token);
    }

    // SetVideoEncoderConfiguration of either Media version. Out-of-range values are refused with
    // ConfigModify, as the options told the client what is allowed.
    public static void ApplyEncoderSettings(OnvifRequestContext context, string? token, string encoding, int width, int height, float frameRate, float quality)
    {
        if (string.IsNullOrEmpty(token)) throw OnvifFault.NoConfig("");
        CheckVideoEncoderConfiguration(token);
        if (!encoding.Equals("JPEG", StringComparison.OrdinalIgnoreCase))
            throw OnvifFault.ConfigModify($"Encoding {encoding} is not supported; this camera streams JPEG.");
        if (!Resolutions(context.Camera).Contains((width, height)))
            throw OnvifFault.ConfigModify($"Resolution {width}x{height} is not offered.");
        int fps = (int)MathF.Round(frameRate);
        if (fps is < MinFrameRate or > MaxFrameRate)
            throw OnvifFault.ConfigModify($"Frame rate {frameRate} is outside {MinFrameRate}..{MaxFrameRate}.");
        int jpegQuality = (int)MathF.Round(quality);
        if (jpegQuality is < MinQuality or > MaxQuality)
            throw OnvifFault.ConfigModify($"Quality {quality} is outside {MinQuality}..{MaxQuality}.");

        context.Store.Update(world =>
        {
            var video = world.Cameras.Single(c => c.Id == context.Camera.Id).Video;
            video.Width = width;
            video.Height = height;
            video.Fps = fps;
            video.Quality = jpegQuality;
        });
    }
}
