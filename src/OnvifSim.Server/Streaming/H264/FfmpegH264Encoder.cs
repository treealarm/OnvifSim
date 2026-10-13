using System.Collections.Concurrent;
using System.Diagnostics;
using OnvifSim.World;

namespace OnvifSim.Streaming.H264;

// One ffmpeg process turning RGBA frames into H.264 access units at fixed settings. x264 runs with
// ultrafast/zerolatency (no B-frames, no lookahead, sliced threads), so every frame written comes back
// as exactly one access unit, in order and at once; that is how access units get the render time of
// their frame. Frame threads instead of sliced ones would hold back one frame per thread.
public sealed class FfmpegH264Encoder : IAsyncDisposable
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly Stream _input;
    private readonly ConcurrentQueue<long> _captureTimestamps = new();
    private readonly Task _readOutput;
    private readonly Task _readErrors;

    public FfmpegH264Encoder(string ffmpegPath, H264Model settings, Action<H264AccessUnit> onAccessUnit, ILogger logger)
    {
        Settings = settings;
        _process = Process.Start(new ProcessStartInfo(ffmpegPath, Arguments(settings))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException($"{ffmpegPath} did not start.");
        _input = _process.StandardInput.BaseStream;
        _readOutput = Task.Run(() => ReadAccessUnitsAsync(onAccessUnit));
        _readErrors = Task.Run(() => LogErrorsAsync(logger));
    }

    public H264Model Settings { get; }

    public bool HasExited => _process.HasExited;

    // Queues one frame of Settings.Width x Settings.Height RGBA pixels.
    public async Task WriteFrameAsync(ReadOnlyMemory<byte> rgba, long captureTimestamp, CancellationToken cancellationToken)
    {
        _captureTimestamps.Enqueue(captureTimestamp);
        await _input.WriteAsync(rgba, cancellationToken);
        await _input.FlushAsync(cancellationToken);
    }

    // Closing the input lets ffmpeg finish on its own; one that does not is killed.
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _input.DisposeAsync();
        }
        catch (IOException)
        {
            // ffmpeg is already gone and took the other end of the pipe with it.
        }
        using var timeout = new CancellationTokenSource(ExitTimeout);
        try
        {
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _process.Kill();
        }
        await Task.WhenAll(_readOutput, _readErrors);
        _process.Dispose();
    }

    // Raw RGBA in, H.264 Annex B out. Constant bitrate for the configured limit; a key frame every
    // GovLength frames with SPS/PPS repeated before it, so a client can join at any key frame; an access
    // unit delimiter before every picture, which is how AccessUnitReader finds the pictures.
    private static string[] Arguments(H264Model settings) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin",
        "-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{settings.Width}x{settings.Height}", "-r", $"{settings.Fps}", "-i", "pipe:0",
        "-an", "-fps_mode", "passthrough",
        "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
        "-profile:v", "baseline", "-level:v", "4.1", "-pix_fmt", "yuv420p",
        "-b:v", $"{settings.BitrateKbps}k", "-maxrate", $"{settings.BitrateKbps}k", "-bufsize", $"{settings.BitrateKbps}k",
        "-g", $"{settings.GovLength}", "-keyint_min", $"{settings.GovLength}",
        "-x264-params", "aud=1:repeat-headers=1:scenecut=0",
        "-flush_packets", "1", "-f", "h264", "pipe:1",
    ];

    private async Task ReadAccessUnitsAsync(Action<H264AccessUnit> onAccessUnit)
    {
        var reader = new AccessUnitReader();
        var buffer = new byte[64 * 1024];
        var output = _process.StandardOutput.BaseStream;
        int read;
        while ((read = await output.ReadAsync(buffer)) > 0)
        {
            reader.Append(buffer.AsSpan(0, read));
            while (reader.TryTake(out var unit))
            {
                if (!_captureTimestamps.TryDequeue(out long captureTimestamp)) captureTimestamp = Stopwatch.GetTimestamp();
                var nals = AnnexB.SplitNals(unit).Where(n => n.Length > 0 && AnnexB.NalType(n.Span) != AnnexB.AccessUnitDelimiter).ToList();
                onAccessUnit(new H264AccessUnit(nals, captureTimestamp));
            }
        }
    }

    private async Task LogErrorsAsync(ILogger logger)
    {
        while (await _process.StandardError.ReadLineAsync() is { } line)
            if (line.Length > 0) logger.LogWarning("ffmpeg: {Message}", line);
    }
}
