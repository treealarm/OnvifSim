using OnvifSim.World;

namespace OnvifSim.Rendering;

// Brightness, contrast and saturation of the Imaging service, applied to a rendered frame the way a
// camera's image pipeline would: 50 leaves a value as rendered, 0 and 100 are the extremes.
public static class ImageAdjustments
{
    public static bool IsNeutral(ImagingModel imaging) =>
        imaging is { Brightness: 50, Contrast: 50, ColorSaturation: 50 };

    public static void Apply(Frame frame, ImagingModel imaging)
    {
        if (IsNeutral(imaging)) return;
        // Saturation 0 is grey, 100 doubles the distance from grey; contrast likewise around mid-grey;
        // brightness shifts everything by up to half the range either way.
        float saturation = imaging.ColorSaturation / 50;
        var tone = ToneCurve(imaging.Contrast / 50, (imaging.Brightness - 50) / 50 * 128);
        var rgba = frame.Rgba;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            float r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            float luma = 0.299f * r + 0.587f * g + 0.114f * b;
            rgba[i] = tone[ToByte(luma + (r - luma) * saturation)];
            rgba[i + 1] = tone[ToByte(luma + (g - luma) * saturation)];
            rgba[i + 2] = tone[ToByte(luma + (b - luma) * saturation)];
        }
    }

    private static byte[] ToneCurve(float contrast, float brightness)
    {
        var curve = new byte[256];
        for (int v = 0; v < 256; v++) curve[v] = ToByte((v - 128) * contrast + 128 + brightness);
        return curve;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp(value + 0.5f, 0, 255);
}
