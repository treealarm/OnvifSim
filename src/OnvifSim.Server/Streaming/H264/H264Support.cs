using System.Diagnostics;

namespace OnvifSim.Streaming.H264;

// Whether this server can serve H.264, decided once at startup: it needs an ffmpeg with libx264, given
// by --ffmpeg (or ONVIFSIM_FFMPEG), bundled next to the program (ffmpeg/ffmpeg, as the release packages
// ship it) or found on PATH. Without one, cameras offer only their JPEG profile.
public sealed class H264Support
{
    private H264Support(string? ffmpegPath) => FfmpegPath = ffmpegPath;

    public string? FfmpegPath { get; }
    public bool IsAvailable => FfmpegPath != null;

    public static H264Support Detect(SimOptions options, ILogger logger)
    {
        if (string.Equals(options.Ffmpeg, "none", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("H.264 is off (--ffmpeg none); cameras stream JPEG only");
            return new H264Support(null);
        }
        string fileName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", fileName);
        var path = options.Ffmpeg ?? (File.Exists(bundled) ? bundled : FindOnPath(fileName));
        if (path == null)
        {
            logger.LogInformation("H.264 is off: no ffmpeg next to the program or on PATH (--ffmpeg <path> names one); cameras stream JPEG only");
            return new H264Support(null);
        }
        if (!HasLibx264(path, out var problem))
        {
            logger.LogWarning("H.264 is off: {Ffmpeg} {Problem}; cameras stream JPEG only", path, problem);
            return new H264Support(null);
        }
        logger.LogInformation("H.264 is on, encoded by {Ffmpeg}", path);
        return new H264Support(path);
    }

    // For tests that need to pin the outcome.
    public static H264Support Unavailable { get; } = new(null);

    private static string? FindOnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, fileName))
            .FirstOrDefault(File.Exists);

    private static bool HasLibx264(string ffmpeg, out string problem)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(ffmpeg, ["-hide_banner", "-encoders"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var encoders = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process.Kill();
                problem = "did not answer -encoders within 10 s";
                return false;
            }
            problem = "has no libx264 encoder";
            return encoders.Result.Contains(" libx264 ", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            problem = $"cannot be started: {ex.Message}";
            return false;
        }
    }
}
