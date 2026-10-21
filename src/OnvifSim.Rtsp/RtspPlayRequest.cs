using System.Globalization;

namespace OnvifSim.Rtsp;

// What a PLAY asked for, as a track's playback sees it. Live media ignores it. A replay (ONVIF
// Streaming Specification, 6) starts at From, ends at To, and with RateControl off sends as fast as the
// client takes it rather than at the pace it was recorded; CSeq goes into its RTP header extension.
public sealed record RtspPlayRequest(DateTimeOffset? From, DateTimeOffset? To, bool RateControl, byte CSeq)
{
    private const string ClockFormat = "yyyyMMdd'T'HHmmss.fff'Z'";
    private static readonly string[] ClockFormats = ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss.FFFFFFF'Z'"];

    // A PLAY without a range: from now on, at the pace of the media.
    public static RtspPlayRequest Live { get; } = new(null, null, RateControl: true, CSeq: 0);

    // Range: clock=20261021T164410.000Z-20261021T164510Z (RFC 2326 3.7: absolute time, UTC); either end
    // may be left open. Any other range (npt=, smpte=) asks a live stream for what it does anyway. Null
    // for a clock range that cannot be read.
    public static RtspPlayRequest? Parse(RtspRequest request)
    {
        bool rateControl = !string.Equals(request.Header("Rate-Control")?.Trim(), "no", StringComparison.OrdinalIgnoreCase);
        byte cseq = (byte)(int.TryParse(request.CSeq, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0);
        var range = request.Header("Range")?.Trim();
        if (range == null || !range.StartsWith("clock=", StringComparison.OrdinalIgnoreCase))
            return new RtspPlayRequest(null, null, rateControl, cseq);

        var bounds = range["clock=".Length..].Split('-', 2);
        DateTimeOffset? from = null, to = null;
        if (bounds[0].Length > 0 && !TryParseClock(bounds[0], out from)) return null;
        if (bounds.Length > 1 && bounds[1].Length > 0 && !TryParseClock(bounds[1], out to)) return null;
        if (from > to) return null;
        return new RtspPlayRequest(from, to, rateControl, cseq);
    }

    // The Range header of the PLAY response: the clock range asked for, or the live stream's.
    public string RangeHeader => From is { } from
        ? $"clock={Format(from)}-{(To is { } to ? Format(to) : "")}"
        : "npt=0.000-";

    private static string Format(DateTimeOffset time) => time.UtcDateTime.ToString(ClockFormat, CultureInfo.InvariantCulture);

    private static bool TryParseClock(string text, out DateTimeOffset? time)
    {
        bool ok = DateTime.TryParseExact(text.Trim(), ClockFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed);
        time = ok ? new DateTimeOffset(parsed, TimeSpan.Zero) : null;
        return ok;
    }
}
