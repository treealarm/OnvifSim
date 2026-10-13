using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace OnvifSim.Streaming.Rtp;

// One outgoing RTP stream of JPEG frames (RFC 3550 + RFC 2435): owns the SSRC, the sequence numbers and
// the mapping from the render clock to the 90 kHz RTP clock, and writes RTCP sender reports for it.
// Not thread-safe: one stream belongs to one RTSP session's sending loop.
public sealed class JpegRtpStream
{
    public const int PayloadType = 26;
    public const int ClockRate = 90000;
    // Whole RTP packet, so that with IP and UDP headers it stays inside a 1500-byte Ethernet MTU.
    public const int MaxPacketSize = 1400;

    private const int RtpHeaderSize = 12;
    private const int JpegHeaderSize = 8;
    private const int QuantizationHeaderSize = 4;
    private const byte DynamicQuantization = 255;

    private long _firstFrameClock = -1;
    private uint _packetCount;
    private uint _octetCount;

    public uint Ssrc { get; } = RandomUInt32();
    // RTP-Info of the PLAY response announces these as the values of the first packet sent.
    public ushort NextSequenceNumber { get; private set; } = (ushort)RandomUInt32();
    public uint InitialTimestamp { get; } = RandomUInt32();

    public bool HasSentFrames => _firstFrameClock >= 0;

    // RTP time of a Stopwatch timestamp; the first frame sent defines the origin.
    public uint RtpTimeAt(long stopwatchTimestamp)
    {
        // Int128: with a nanosecond Stopwatch, ticks * 90000 overflows a long after about a day.
        Int128 elapsed = stopwatchTimestamp - _firstFrameClock;
        return InitialTimestamp + (uint)(ulong)(elapsed * ClockRate / Stopwatch.Frequency);
    }

    // Appends the packets of one frame to the batch; the last one carries the marker bit.
    public void Packetize(EncodedFrame frame, RtpPacketBatch batch)
    {
        var scan = JpegScan.Parse(frame.Jpeg);
        if (_firstFrameClock < 0) _firstFrameClock = frame.CaptureTimestamp;
        uint timestamp = RtpTimeAt(frame.CaptureTimestamp);

        var data = scan.EntropyData.Span;
        int fragmentOffset = 0;
        do
        {
            bool first = fragmentOffset == 0;
            int headers = RtpHeaderSize + JpegHeaderSize + (first ? QuantizationHeaderSize + scan.QuantizationTables.Length : 0);
            int chunk = Math.Min(data.Length - fragmentOffset, MaxPacketSize - headers);
            bool last = fragmentOffset + chunk == data.Length;

            var packet = batch.Append(headers + chunk);
            WriteRtpHeader(packet, last, timestamp);
            var jpegHeader = packet[RtpHeaderSize..];
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
            _packetCount++;
            _octetCount += (uint)(packet.Length - RtpHeaderSize);
        }
        while (fragmentOffset < data.Length);
    }

    // Appends a compound RTCP packet: a sender report tying RTP time to wall-clock time, and the SDES
    // CNAME that RFC 3550 requires in every compound packet.
    public void WriteSenderReport(RtpPacketBatch batch, string canonicalName)
    {
        long now = Stopwatch.GetTimestamp();
        var wallClock = DateTime.UtcNow;
        var cname = Encoding.UTF8.GetBytes(canonicalName);
        if (cname.Length > 255) cname = cname[..255];

        const int senderReportSize = 28;
        // SDES: header, SSRC, CNAME item (type, length, text), end-of-list, padded to 32 bits.
        int sdesSize = (4 + 4 + 2 + cname.Length + 1 + 3) & ~3;
        var packet = batch.Append(senderReportSize + sdesSize);
        packet.Clear();

        packet[0] = 0x80;
        packet[1] = 200;
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..], senderReportSize / 4 - 1);
        BinaryPrimitives.WriteUInt32BigEndian(packet[4..], Ssrc);
        BinaryPrimitives.WriteUInt64BigEndian(packet[8..], ToNtpTime(wallClock));
        BinaryPrimitives.WriteUInt32BigEndian(packet[16..], RtpTimeAt(now));
        BinaryPrimitives.WriteUInt32BigEndian(packet[20..], _packetCount);
        BinaryPrimitives.WriteUInt32BigEndian(packet[24..], _octetCount);

        var sdes = packet[senderReportSize..];
        sdes[0] = 0x81;
        sdes[1] = 202;
        BinaryPrimitives.WriteUInt16BigEndian(sdes[2..], (ushort)(sdesSize / 4 - 1));
        BinaryPrimitives.WriteUInt32BigEndian(sdes[4..], Ssrc);
        sdes[8] = 1; // CNAME
        sdes[9] = (byte)cname.Length;
        cname.CopyTo(sdes[10..]);
    }

    private void WriteRtpHeader(Span<byte> packet, bool marker, uint timestamp)
    {
        packet[0] = 0x80; // version 2, no padding, no extension, no CSRC
        packet[1] = (byte)((marker ? 0x80 : 0) | PayloadType);
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..], NextSequenceNumber++);
        BinaryPrimitives.WriteUInt32BigEndian(packet[4..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet[8..], Ssrc);
    }

    private static ulong ToNtpTime(DateTime utc)
    {
        var sinceEpoch = utc - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ulong seconds = (ulong)(sinceEpoch.Ticks / TimeSpan.TicksPerSecond);
        ulong fraction = (ulong)(sinceEpoch.Ticks % TimeSpan.TicksPerSecond) * (1UL << 32) / TimeSpan.TicksPerSecond;
        return seconds << 32 | fraction;
    }

    private static uint RandomUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
}
