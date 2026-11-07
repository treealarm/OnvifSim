using System.Globalization;
using System.Numerics;

namespace OnvifSim.Geometry;

public static class ColorUtil
{
    // "#rrggbb" to linear-ish 0..1 RGB; anything unparsable falls back to the given default.
    public static Vector3 Parse(string? hex, Vector3 fallback)
    {
        if (hex is { Length: 7 } && hex[0] == '#' &&
            int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            return new Vector3((v >> 16) & 0xff, (v >> 8) & 0xff, v & 0xff) / 255f;
        return fallback;
    }

    public static Vector3 Rgb(int r, int g, int b) => new Vector3(r, g, b) / 255f;

    // A cheap deterministic 0..1 noise from two integers. The terrain's dry patches and the grain of
    // a wall are both this, drawn from a seed and a cell's place, so a world built twice looks the same.
    public static float Hash(int x, int z)
    {
        uint h = (uint)(x * 374761393 + z * 668265263);
        h = (h ^ (h >> 13)) * 1274126177;
        return ((h ^ (h >> 16)) & 0xffff) / 65535f;
    }
}
