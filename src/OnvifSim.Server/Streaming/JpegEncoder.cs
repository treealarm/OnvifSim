using OnvifSim.Rendering;
using StbImageWriteSharp;

namespace OnvifSim.Streaming;

public static class JpegEncoder
{
    // Baseline JPEG with the standard Huffman tables; stb uses 4:2:0 chroma at quality <= 90, which is
    // what RTP/JPEG (RFC 2435) expects, so the quality is capped there.
    public static byte[] Encode(Frame frame, int quality)
    {
        using var ms = new MemoryStream(frame.Width * frame.Height / 4);
        new ImageWriter().WriteJpg(frame.Rgba, frame.Width, frame.Height, ColorComponents.RedGreenBlueAlpha, ms, Math.Clamp(quality, 1, 90));
        return ms.ToArray();
    }
}
