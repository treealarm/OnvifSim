namespace OnvifSim.Rendering;

// A decoded raster the renderer can draw geometry with: RGBA bytes, row-major from the top-left, as a
// PNG decoder hands them over. One instance serves every camera — decoding is done once per world.
public sealed class Texture
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Rgba { get; init; }

    // The colour at u, v (0..1 across and down the picture), bilinearly filtered and clamped at the
    // edges: a map is an island of ground, not a wall that repeats.
    public void Sample(float u, float v, out float r, out float g, out float b)
    {
        float x = Math.Clamp(u, 0, 1) * (Width - 1);
        float y = Math.Clamp(v, 0, 1) * (Height - 1);
        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
        float tx = x - x0, ty = y - y0;
        int row0 = y0 * Width, row1 = y1 * Width;
        int i00 = (row0 + x0) * 4, i10 = (row0 + x1) * 4, i01 = (row1 + x0) * 4, i11 = (row1 + x1) * 4;
        var px = Rgba;
        r = Mix(Mix(px[i00], px[i10], tx), Mix(px[i01], px[i11], tx), ty) / 255f;
        g = Mix(Mix(px[i00 + 1], px[i10 + 1], tx), Mix(px[i01 + 1], px[i11 + 1], tx), ty) / 255f;
        b = Mix(Mix(px[i00 + 2], px[i10 + 2], tx), Mix(px[i01 + 2], px[i11 + 2], tx), ty) / 255f;
    }

    private static float Mix(float a, float b, float t) => a + (b - a) * t;
}
