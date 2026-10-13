using System.Buffers.Binary;

namespace OnvifSim.Tests;

// A receiver for RTP/JPEG written from RFC 2435 alone (Appendix A/B and the JPEG standard's Annex K),
// independent of the server's encoder: it rebuilds a complete JPEG from the packets of one frame the way
// ffmpeg, live555, GStreamer and go2rtc do, using the standard Huffman tables.
public static class Rfc2435Reference
{
    public static readonly byte[] LumaDcBits = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    public static readonly byte[] ChromaDcBits = [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    public static readonly byte[] DcSymbols = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    public static readonly byte[] LumaAcBits = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D];
    public static readonly byte[] ChromaAcBits = [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];

    // Annex K tables K.5 and K.6: the symbols with codes shorter than 16 bits are listed by hand; all
    // remaining run/size symbols get 16-bit codes and follow in ascending order.
    public static readonly byte[] LumaAcSymbols = AcSymbols(
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
        0x24, 0x33, 0x62, 0x72, 0x82,
    ]);
    public static readonly byte[] ChromaAcSymbols = AcSymbols(
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
        0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1,
    ]);

    // The four DHT segments' tables in the order (class/id, bits, symbols).
    public static IEnumerable<(byte ClassAndId, byte[] Bits, byte[] Symbols)> StandardHuffmanTables() =>
    [
        (0x00, LumaDcBits, DcSymbols),
        (0x10, LumaAcBits, LumaAcSymbols),
        (0x01, ChromaDcBits, DcSymbols),
        (0x11, ChromaAcBits, ChromaAcSymbols),
    ];

    // Rebuilds the JPEG file from the RTP packets of one frame (RTP headers included, in order).
    public static byte[] Depacketize(IReadOnlyList<byte[]> packets)
    {
        int type = -1, width = 0, height = 0;
        byte[]? tables = null;
        var data = new MemoryStream();
        foreach (var packet in packets)
        {
            var payload = packet.AsSpan(12);
            int fragmentOffset = payload[1] << 16 | payload[2] << 8 | payload[3];
            type = payload[4];
            int q = payload[5];
            width = payload[6] * 8;
            height = payload[7] * 8;
            payload = payload[8..];
            if (fragmentOffset == 0)
            {
                if (q < 128) throw new NotSupportedException("The reference only handles in-band tables (Q >= 128).");
                int length = BinaryPrimitives.ReadUInt16BigEndian(payload[2..]);
                tables = payload.Slice(4, length).ToArray();
                payload = payload[(4 + length)..];
            }
            if (fragmentOffset != data.Length) throw new InvalidDataException($"Fragment at {fragmentOffset}, expected {data.Length}.");
            data.Write(payload);
        }
        if (tables == null) throw new InvalidDataException("No packet with fragment offset 0.");

        var jpeg = new MemoryStream();
        jpeg.Write([0xFF, 0xD8]);
        WriteSegment(jpeg, 0xDB, [0x00, .. tables.AsSpan(0, 64), 0x01, .. tables.AsSpan(64, 64)]);
        WriteSegment(jpeg, 0xC0,
        [
            8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3,
            1, (byte)(type == 0 ? 0x21 : 0x22), 0,
            2, 0x11, 1,
            3, 0x11, 1,
        ]);
        foreach (var (classAndId, bits, symbols) in StandardHuffmanTables())
            WriteSegment(jpeg, 0xC4, [classAndId, .. bits, .. symbols]);
        WriteSegment(jpeg, 0xDA, [3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0]);
        data.Position = 0;
        data.CopyTo(jpeg);
        jpeg.Write([0xFF, 0xD9]);
        return jpeg.ToArray();
    }

    private static void WriteSegment(Stream stream, byte marker, byte[] body)
    {
        stream.Write([0xFF, marker, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2)]);
        stream.Write(body);
    }

    private static byte[] AcSymbols(byte[] shortCodes)
    {
        var all = new List<byte> { 0x00, 0xF0 };
        for (int run = 0; run < 16; run++)
            for (int size = 1; size <= 10; size++)
                all.Add((byte)(run << 4 | size));
        return [.. shortCodes, .. all.Except(shortCodes).Order()];
    }
}
