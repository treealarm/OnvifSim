using System.Buffers.Binary;

namespace OnvifSim.Rtsp.Rtp;

// What RTP/JPEG (RFC 2435) needs from a baseline JPEG file: the frame size, the chroma subsampling as an
// RFC type, the two quantization tables and the entropy-coded scan. Everything else in the file (JFIF,
// Huffman tables, SOF/SOS headers) is dropped: the receiver rebuilds it from the RTP header and the
// standard Huffman tables, so the encoder must use exactly those tables.
public readonly record struct JpegScan(int Type, int Width, int Height, ReadOnlyMemory<byte> QuantizationTables, ReadOnlyMemory<byte> EntropyData)
{
    // RFC 2435 types: 0 is 4:2:2 (luma sampled 2x1), 1 is 4:2:0 (luma sampled 2x2); both with chroma 1x1.
    public const int Type422 = 0;
    public const int Type420 = 1;

    private const int QuantizationTableSize = 64;

    // Throws NotSupportedException for anything RFC 2435 cannot carry without restart markers or
    // custom tables; the encoder is ours, so that is a programming error, not bad input.
    public static JpegScan Parse(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new NotSupportedException("Not a JPEG file.");

        var tables = new byte[2 * QuantizationTableSize];
        bool[] tablePresent = [false, false];
        int type = -1, width = 0, height = 0;
        int pos = 2;
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF) throw new NotSupportedException($"Expected a marker at offset {pos}.");
            byte marker = jpeg[pos + 1];
            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            int segmentStart = pos + 4;
            int segmentEnd = pos + 2 + segmentLength;
            if (segmentEnd > jpeg.Length) throw new NotSupportedException("Truncated JPEG segment.");
            var segment = jpeg.AsSpan(segmentStart, segmentEnd - segmentStart);

            switch (marker)
            {
                case 0xDB:
                    ReadQuantizationTables(segment, tables, tablePresent);
                    break;
                case 0xC0:
                    (type, width, height) = ReadBaselineFrameHeader(segment);
                    break;
                case >= 0xC1 and <= 0xCF when marker is not (0xC4 or 0xC8 or 0xCC):
                    throw new NotSupportedException($"JPEG process SOF{marker - 0xC0} is not baseline.");
                case 0xDD when BinaryPrimitives.ReadUInt16BigEndian(segment) != 0:
                    throw new NotSupportedException("Restart markers are not supported.");
                case 0xDA:
                    if (type < 0) throw new NotSupportedException("Scan before the frame header.");
                    if (!tablePresent[0] || !tablePresent[1]) throw new NotSupportedException("Missing quantization tables.");
                    int dataEnd = jpeg.Length;
                    if (jpeg[dataEnd - 2] == 0xFF && jpeg[dataEnd - 1] == 0xD9) dataEnd -= 2;
                    return new JpegScan(type, width, height, tables, jpeg.AsMemory(segmentEnd, dataEnd - segmentEnd));
            }
            pos = segmentEnd;
        }
        throw new NotSupportedException("JPEG has no scan.");
    }

    private static void ReadQuantizationTables(ReadOnlySpan<byte> segment, byte[] tables, bool[] tablePresent)
    {
        while (!segment.IsEmpty)
        {
            int precision = segment[0] >> 4;
            int id = segment[0] & 0x0F;
            if (precision != 0) throw new NotSupportedException("16-bit quantization tables are not supported.");
            if (id > 1) throw new NotSupportedException($"Quantization table {id} is not supported.");
            segment.Slice(1, QuantizationTableSize).CopyTo(tables.AsSpan(id * QuantizationTableSize));
            tablePresent[id] = true;
            segment = segment[(1 + QuantizationTableSize)..];
        }
    }

    // Accepts what RFC 2435 can describe: 8-bit YCbCr, components 1/2/3, luma on table 0 sampled 2x1 or
    // 2x2, both chroma components on table 1 sampled 1x1.
    private static (int Type, int Width, int Height) ReadBaselineFrameHeader(ReadOnlySpan<byte> segment)
    {
        int precision = segment[0];
        int height = BinaryPrimitives.ReadUInt16BigEndian(segment[1..]);
        int width = BinaryPrimitives.ReadUInt16BigEndian(segment[3..]);
        int components = segment[5];
        if (precision != 8 || components != 3)
            throw new NotSupportedException("Only 8-bit three-component JPEG is supported.");
        var luma = segment.Slice(6, 3);
        var blue = segment.Slice(9, 3);
        var red = segment.Slice(12, 3);
        bool chromaOk = blue[1] == 0x11 && blue[2] == 1 && red[1] == 0x11 && red[2] == 1;
        int type = luma[1] switch { 0x21 => Type422, 0x22 => Type420, _ => -1 };
        if (luma[2] != 0 || !chromaOk || type < 0)
            throw new NotSupportedException("Only 4:2:2 and 4:2:0 JPEG with tables 0 (luma) and 1 (chroma) is supported.");
        // The RTP header carries the size in 8-pixel blocks, one byte per side.
        if (width % 8 != 0 || height % 8 != 0 || width > 2040 || height > 2040)
            throw new NotSupportedException($"RTP/JPEG cannot carry {width}x{height}: sides must be multiples of 8 up to 2040.");
        return (type, width, height);
    }
}
