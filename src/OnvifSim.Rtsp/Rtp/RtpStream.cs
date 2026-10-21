using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace OnvifSim.Rtsp.Rtp;

// One outgoing RTP stream (RFC 3550): owns the SSRC, the sequence numbers and the mapping from the render
// clock to the 90 kHz RTP clock, and writes RTCP sender reports for it. What goes into the packets is the
// packetizer's business (JpegPacketizer, H264Packetizer). Not thread-safe: one stream belongs to one
// RTSP session's sending loop.
public sealed class RtpStream(byte payloadType)
{
    // Every payload format here (RFC 2435, RFC 6184, ONVIF metadata) uses a 90 kHz clock, and the SDP
    // announces every track with it.
    public const int ClockRate = 90000;
    // Whole RTP packet, so that with IP and UDP headers it stays inside a 1500-byte Ethernet MTU.
    public const int MaxPacketSize = 1400;
    public const int HeaderSize = 12;
    // The ONVIF replay extension: its own header (profile 0xABAC, length 3) and three 32-bit words.
    public const int ReplayExtensionSize = 16;
    private const ushort ReplayExtensionProfile = 0xABAC;

    private long _firstFrameClock = -1;
    private uint _packetCount;
    private uint _octetCount;

    public uint Ssrc { get; } = RandomUInt32();
    // RTP-Info of the PLAY response announces these as the values of the first packet sent.
    public ushort NextSequenceNumber { get; private set; } = (ushort)RandomUInt32();
    public uint InitialTimestamp { get; } = RandomUInt32();

    public bool HasSentFrames => _firstFrameClock >= 0;

    // Set by a replay before it packetizes each frame: every packet then carries the ONVIF replay
    // header extension (ONVIF Streaming Specification, 6.3) describing that frame. Null for live media.
    public ReplayHeader? Replay { get; set; }

    // Room for the payload in a packet, after the header and any extension.
    public int MaxPayloadSize => MaxPacketSize - HeaderSize - (Replay == null ? 0 : ReplayExtensionSize);

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
        int headerLength = HeaderSize + (Replay == null ? 0 : ReplayExtensionSize);
        var packet = batch.Append(headerLength + payloadLength);
        packet[0] = (byte)(Replay == null ? 0x80 : 0x90); // version 2, no padding, extension when replaying, no CSRC
        packet[1] = (byte)((marker ? 0x80 : 0) | payloadType);
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..], NextSequenceNumber++);
        BinaryPrimitives.WriteUInt32BigEndian(packet[4..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet[8..], Ssrc);
        if (Replay is { } replay) WriteReplayExtension(packet[HeaderSize..], replay, marker);
        _packetCount++;
        _octetCount += (uint)payloadLength;
        return packet[headerLength..];
    }

    // C and D describe the whole frame and go on all its packets; E and T mark where its data ends,
    // so only the frame's last packet carries them.
    private static void WriteReplayExtension(Span<byte> extension, ReplayHeader replay, bool lastOfFrame)
    {
        BinaryPrimitives.WriteUInt16BigEndian(extension, ReplayExtensionProfile);
        BinaryPrimitives.WriteUInt16BigEndian(extension[2..], ReplayExtensionSize / 4 - 1);
        BinaryPrimitives.WriteUInt64BigEndian(extension[4..], ToNtpTime(replay.Time.UtcDateTime));
        extension[12] = (byte)((replay.CleanPoint ? 0x80 : 0)
            | (lastOfFrame && replay.EndOfSection ? 0x40 : 0)
            | (replay.Discontinuity ? 0x20 : 0)
            | (lastOfFrame && replay.Terminal ? 0x10 : 0));
        extension[13] = replay.CSeq;
        extension[14] = 0;
        extension[15] = 0;
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
