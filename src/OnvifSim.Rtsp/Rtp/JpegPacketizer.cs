using System.Buffers.Binary;

namespace OnvifSim.Rtsp.Rtp;

// RTP payload format for JPEG (RFC 2435): the entropy-coded scan split across packets behind an 8-byte
// JPEG header, with the quantization tables in-band in the first packet (Q = 255).
public static class JpegPacketizer
{
    public const byte PayloadType = 26;

    private const int JpegHeaderSize = 8;
    private const int QuantizationHeaderSize = 4;
    private const byte DynamicQuantization = 255;

    // Appends the packets of one frame (a JPEG file captured at a Stopwatch timestamp) to the batch; the
    // last one carries the marker bit.
    public static void Packetize(RtpStream stream, byte[] jpeg, long captureTimestamp, RtpPacketBatch batch)
    {
        var scan = JpegScan.Parse(jpeg);
        uint timestamp = stream.TimestampOf(captureTimestamp);

        var data = scan.EntropyData.Span;
        int fragmentOffset = 0;
        do
        {
            bool first = fragmentOffset == 0;
            int headers = JpegHeaderSize + (first ? QuantizationHeaderSize + scan.QuantizationTables.Length : 0);
            int chunk = Math.Min(data.Length - fragmentOffset, stream.MaxPayloadSize - headers);
            bool last = fragmentOffset + chunk == data.Length;

            var jpegHeader = stream.AppendPacket(batch, headers + chunk, last, timestamp);
            jpegHeader[0] = 0; // type-specific
            jpegHeader[1] = (byte)(fragmentOffset >> 16);
            jpegHeader[2] = (byte)(fragmentOffset >> 8);
            jpegHeader[3] = (byte)fragmentOffset;
            jpegHeader[4] = (byte)scan.Type;
            jpegHeader[5] = DynamicQuantization;
            jpegHeader[6] = (byte)(scan.Width / 8);
            jpegHeader[7] = (byte)(scan.Height / 8);
            var payload = jpegHeader[JpegHeaderSize..];
            if (first)
            {
                payload[0] = 0; // MBZ
                payload[1] = 0; // precision: all tables 8-bit
                BinaryPrimitives.WriteUInt16BigEndian(payload[2..], (ushort)scan.QuantizationTables.Length);
                scan.QuantizationTables.Span.CopyTo(payload[QuantizationHeaderSize..]);
                payload = payload[(QuantizationHeaderSize + scan.QuantizationTables.Length)..];
            }
            data.Slice(fragmentOffset, chunk).CopyTo(payload);
            fragmentOffset += chunk;
        }
        while (fragmentOffset < data.Length);
    }
}
