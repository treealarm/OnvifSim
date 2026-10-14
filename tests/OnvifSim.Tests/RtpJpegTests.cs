using System.Buffers.Binary;
using System.Diagnostics;
using OnvifSim.Rendering;
using OnvifSim.Rtsp.Rtp;
using OnvifSim.Streaming;
using StbImageSharp;

namespace OnvifSim.Tests;

public class RtpJpegTests
{
    // A frame with fine detail everywhere, so the JPEG spans many packets.
    private static Frame NoisyFrame(int width = 640, int height = 360)
    {
        var frame = new Frame(width, height);
        new Random(1).NextBytes(frame.Rgba);
        for (int i = 3; i < frame.Rgba.Length; i += 4) frame.Rgba[i] = 255;
        return frame;
    }

    private static RtpStream NewStream() => new(JpegPacketizer.PayloadType);

    private static List<byte[]> Packetize(RtpStream stream, byte[] jpeg, long captureTimestamp = 0)
    {
        var batch = new RtpPacketBatch();
        JpegPacketizer.Packetize(stream, jpeg, captureTimestamp, batch);
        return Enumerable.Range(0, batch.Count).Select(i => batch.Packet(i).ToArray()).ToList();
    }

    [Fact]
    public void Encoder_UsesTheStandardHuffmanTables_ThatRfc2435ReceiversAssume()
    {
        var jpeg = JpegEncoder.Encode(NoisyFrame(), 70);
        var expected = Rfc2435Reference.StandardHuffmanTables()
            .SelectMany(t => new[] { t.ClassAndId }.Concat(t.Bits).Concat(t.Symbols))
            .ToArray();
        Assert.Equal(expected, DhtPayloads(jpeg));
    }

    [Fact]
    public void Scan_OfEncoderOutput_Is420WithBothTables()
    {
        var scan = JpegScan.Parse(JpegEncoder.Encode(NoisyFrame(), 70));
        Assert.Equal(JpegScan.Type420, scan.Type);
        Assert.Equal((640, 360), (scan.Width, scan.Height));
        Assert.Equal(128, scan.QuantizationTables.Length);
        Assert.False(scan.EntropyData.Span.EndsWith((ReadOnlySpan<byte>)[0xFF, 0xD9]));
    }

    [Fact]
    public void Scan_RejectsWhatRtpJpegCannotCarry()
    {
        // stb switches to 4:4:4 above quality 90.
        var full = new MemoryStream();
        new StbImageWriteSharp.ImageWriter().WriteJpg(NoisyFrame().Rgba, 640, 360, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, full, 100);
        Assert.Throws<NotSupportedException>(() => JpegScan.Parse(full.ToArray()));
        Assert.Throws<NotSupportedException>(() => JpegScan.Parse(JpegEncoder.Encode(NoisyFrame(644, 360), 70)));
        Assert.Throws<NotSupportedException>(() => JpegScan.Parse([0x89, 0x50, 0x4E, 0x47]));
    }

    [Fact]
    public void Packets_RebuiltAsRfc2435Describes_DecodeToTheSamePixels()
    {
        var jpeg = JpegEncoder.Encode(NoisyFrame(), 70);
        var packets = Packetize(NewStream(), jpeg);

        var rebuilt = Rfc2435Reference.Depacketize(packets);

        var original = ImageResult.FromMemory(jpeg, ColorComponents.RedGreenBlue);
        var received = ImageResult.FromMemory(rebuilt, ColorComponents.RedGreenBlue);
        Assert.Equal((640, 360), (received.Width, received.Height));
        Assert.Equal(original.Data, received.Data);
    }

    [Fact]
    public void Packets_FitTheMtu_AndOnlyTheLastHasTheMarker()
    {
        var stream = NewStream();
        ushort firstSequence = stream.NextSequenceNumber;
        var packets = Packetize(stream, JpegEncoder.Encode(NoisyFrame(), 70));

        Assert.True(packets.Count > 10);
        Assert.All(packets, p => Assert.InRange(p.Length, 21, RtpStream.MaxPacketSize));
        for (int i = 0; i < packets.Count; i++)
        {
            var p = packets[i];
            Assert.Equal(0x80, p[0]);
            Assert.Equal(i == packets.Count - 1, (p[1] & 0x80) != 0);
            Assert.Equal(JpegPacketizer.PayloadType, p[1] & 0x7F);
            Assert.Equal((ushort)(firstSequence + i), BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2)));
            Assert.Equal(stream.InitialTimestamp, BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(4)));
            Assert.Equal(stream.Ssrc, BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(8)));
        }
    }

    [Fact]
    public void Timestamps_Follow_TheCaptureClock_At90kHz()
    {
        var stream = NewStream();
        var jpeg = JpegEncoder.Encode(NoisyFrame(64, 64), 70);
        long start = Stopwatch.GetTimestamp();
        Packetize(stream, jpeg, start);
        var oneSecondLater = Packetize(stream, jpeg, start + Stopwatch.Frequency);

        uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(oneSecondLater[0].AsSpan(4));
        Assert.Equal(stream.InitialTimestamp + 90000u, timestamp);
    }

    [Fact]
    public void SenderReport_IsACompoundSrAndSdesPacket()
    {
        var stream = NewStream();
        Packetize(stream, JpegEncoder.Encode(NoisyFrame(64, 64), 70), Stopwatch.GetTimestamp());
        var batch = new RtpPacketBatch();
        stream.WriteSenderReport(batch, "onvifsim-cam1");

        var packet = batch.Packet(0).ToArray();
        Assert.Equal(0, packet.Length % 4);
        Assert.Equal(200, packet[1]);
        int srLength = (BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)) + 1) * 4;
        Assert.Equal(stream.Ssrc, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));
        Assert.Equal(202, packet[srLength + 1]);
        int sdesLength = (BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(srLength + 2)) + 1) * 4;
        Assert.Equal(packet.Length, srLength + sdesLength);
        Assert.Equal("onvifsim-cam1", System.Text.Encoding.ASCII.GetString(packet, srLength + 10, packet[srLength + 9]));
    }

    private static byte[] DhtPayloads(byte[] jpeg)
    {
        var result = new List<byte>();
        int pos = 2;
        while (jpeg[pos + 1] != 0xDA)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            if (jpeg[pos + 1] == 0xC4) result.AddRange(jpeg.AsSpan(pos + 4, length - 2));
            pos += 2 + length;
        }
        return [.. result];
    }
}
