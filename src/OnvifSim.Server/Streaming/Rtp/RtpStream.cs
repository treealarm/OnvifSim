using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace OnvifSim.Streaming.Rtp;

// One outgoing RTP stream (RFC 3550): owns the SSRC, the sequence numbers and the mapping from the render
// clock to the 90 kHz RTP clock, and writes RTCP sender reports for it. What goes into the packets is the
// packetizer's business (JpegPacketizer, H264Packetizer). Not thread-safe: one stream belongs to one
// RTSP session's sending loop.
public sealed class RtpStream(byte payloadType)
{
    // Every video payload format here (RFC 2435, RFC 6184) uses a 90 kHz clock.
    public const int ClockRate = 90000;
    // Whole RTP packet, so that with IP and UDP headers it stays inside a 1500-byte Ethernet MTU.
    public const int MaxPacketSize = 1400;
    public const int HeaderSize = 12;

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

    // RTP time of a frame captured at a Stopwatch timestamp; the first frame defines the origin.
    public uint TimestampOf(long captureTimestamp)
    {
        if (_firstFrameClock < 0) _firstFrameClock = captureTimestamp;
        return RtpTimeAt(captureTimestamp);
    }

    // Appends a packet with its RTP header written and returns its payload for the packetizer to fill.
    // The marker bit goes on the last packet of a frame.
    public Span<byte> AppendPacket(RtpPacketBatch batch, int payloadLength, bool marker, uint timestamp)
    {
        var packet = batch.Append(HeaderSize + payloadLength);
        packet[0] = 0x80; // version 2, no padding, no extension, no CSRC
        packet[1] = (byte)((marker ? 0x80 : 0) | payloadType);
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..], NextSequenceNumber++);
        BinaryPrimitives.WriteUInt32BigEndian(packet[4..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet[8..], Ssrc);
        _packetCount++;
        _octetCount += (uint)payloadLength;
        return packet[HeaderSize..];
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

    private static ulong ToNtpTime(DateTime utc)
    {
        var sinceEpoch = utc - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ulong seconds = (ulong)(sinceEpoch.Ticks / TimeSpan.TicksPerSecond);
        ulong fraction = (ulong)(sinceEpoch.Ticks % TimeSpan.TicksPerSecond) * (1UL << 32) / TimeSpan.TicksPerSecond;
        return seconds << 32 | fraction;
    }

    private static uint RandomUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
}
