using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OnvifLib;
using OnvifSim.Onvif;
using OnvifSim.Streaming.H264;
using OnvifSim.Streaming.Rtp;
using OnvifSim.Streaming.Rtsp;
using OnvifSim.World;

namespace OnvifSim.Tests;

public class AnnexBTests
{
    private static byte[] Nal(byte header, int bodyLength, byte fill = 0x55) => [header, .. Enumerable.Repeat(fill, bodyLength)];

    private static byte[] Stream(params byte[][] nals) =>
        nals.SelectMany((nal, i) => (i % 2 == 0 ? new byte[] { 0, 0, 0, 1 } : [0, 0, 1]).Concat(nal)).ToArray();

    [Fact]
    public void SplitNals_HandlesThreeAndFourByteStartCodes()
    {
        var nals = AnnexB.SplitNals(Stream(Nal(0x09, 1), Nal(0x67, 5), Nal(0x68, 3), Nal(0x65, 20)));
        Assert.Equal([9, 7, 8, 5], nals.Select(n => AnnexB.NalType(n.Span)));
        Assert.Equal([2, 6, 4, 21], nals.Select(n => n.Length));
    }

    [Fact]
    public void AccessUnitReader_CutsAtEveryDelimiter_HoweverTheBytesArrive()
    {
        var aud = Nal(0x09, 1, 0xF0);
        var stream = Stream(aud, Nal(0x67, 5), Nal(0x68, 3), Nal(0x65, 40), aud, Nal(0x41, 30), aud, Nal(0x41, 10));
        var reader = new AccessUnitReader();
        var units = new List<byte[]>();
        foreach (var b in stream)
        {
            reader.Append([b]);
            while (reader.TryTake(out var unit)) units.Add(unit);
        }

        // The last access unit is not complete until the next delimiter arrives.
        Assert.Equal(2, units.Count);
        Assert.Equal([9, 7, 8, 5], AnnexB.SplitNals(units[0]).Select(n => AnnexB.NalType(n.Span)));
        Assert.Equal([9, 1], AnnexB.SplitNals(units[1]).Select(n => AnnexB.NalType(n.Span)));
    }

    [Fact]
    public void Packetizer_SendsSmallNalsWhole_SplitsLargeOnesIntoFuA_AndMarksTheLastPacket()
    {
        var sps = Nal(0x67, 10);
        var idr = Nal(0x65, 3000, 0x33);
        var unit = new H264AccessUnit([sps, idr], CaptureTimestamp: 0);
        var batch = new RtpPacketBatch();
        H264Packetizer.Packetize(new RtpStream(H264Packetizer.PayloadType), unit, batch);
        var packets = Enumerable.Range(0, batch.Count).Select(i => batch.Packet(i).ToArray()).ToList();

        Assert.All(packets, p => Assert.InRange(p.Length, 13, RtpStream.MaxPacketSize));
        Assert.Equal(sps, packets[0][12..]);
        Assert.Equal([false, false, false, true], packets.Select(p => (p[1] & 0x80) != 0));

        // FU-A: indicator keeps F/NRI with type 28, header has S on the first, E on the last fragment.
        var fragments = packets[1..];
        Assert.All(fragments, p => Assert.Equal(0x60 | 28, p[12]));
        Assert.Equal([0x80 | 5, 5, 0x40 | 5], fragments.Select(p => (int)p[13]));
        Assert.Equal(idr[1..], fragments.SelectMany(p => p[14..]).ToArray());
    }
}

// The simulator with the ffmpeg found on PATH, for the H.264 tests.
public sealed class H264SimFactory : SimFactory
{
    protected override string Ffmpeg => "";
}

// A test that needs an ffmpeg with libx264, skipped where there is none.
public sealed class FfmpegFactAttribute : FactAttribute
{
    private static readonly bool Available = H264Support.Detect(new SimOptions(), NullLogger.Instance).IsAvailable;

    public FfmpegFactAttribute()
    {
        if (!Available) Skip = "needs ffmpeg with libx264 on PATH";
    }
}

public class FfmpegH264EncoderTests
{
    [FfmpegFact]
    public async Task EveryFrameWritten_ComesBackAsOneAccessUnit_WithItsTimestamp()
    {
        var ffmpeg = H264Support.Detect(new SimOptions(), NullLogger.Instance).FfmpegPath!;
        var settings = new H264Model { Width = 320, Height = 240, Fps = 10, GovLength = 5 };
        var units = new System.Collections.Concurrent.ConcurrentQueue<H264AccessUnit>();
        var rgba = new byte[settings.Width * settings.Height * 4];
        await using (var encoder = new FfmpegH264Encoder(ffmpeg, settings, units.Enqueue, NullLogger.Instance))
        {
            for (int i = 0; i < 20; i++)
            {
                Array.Fill(rgba, (byte)(i * 10));
                await encoder.WriteFrameAsync(rgba, captureTimestamp: 1000 + i, CancellationToken.None);
            }
            for (int i = 0; i < 50 && units.Count < 19; i++) await Task.Delay(100);
        }

        // The last picture stays in the reader until a next delimiter that never comes.
        var received = units.ToList();
        Assert.Equal(Enumerable.Range(0, received.Count).Select(i => 1000L + i), received.Select(u => u.CaptureTimestamp));
        Assert.InRange(received.Count, 19, 20);
        Assert.Equal([true, false, false, false, false, true], received.Take(6).Select(u => u.IsKeyFrame));
    }
}

public class H264StreamTests(H264SimFactory factory) : IClassFixture<H264SimFactory>
{
    private RtspServer Server => factory.Services.GetRequiredService<RtspServer>();
    private WorldStore Store => factory.Services.GetRequiredService<WorldStore>();

    private string Url(string path) => RtspTestClient.Url(Server.Port, path);

    private Camera Connect(string cameraId) => Camera.Create("127.0.0.1",
        factory.Services.GetRequiredService<OnvifHost>().PortOf(cameraId)!.Value, "admin", "admin", timeout: 10);

    [FfmpegFact]
    public async Task Rtsp_StreamsH264_StartingWithAKeyFrameAndItsParameterSets()
    {
        await using var client = await RtspTestClient.ConnectAsync(Server.Port);
        (client.Username, client.Password) = ("admin", "admin");
        var describe = await client.RequestAsync("DESCRIBE", Url("cam1/h264"), ("Accept", "application/sdp"));
        Assert.Equal(200, describe.Status);
        Assert.Contains("a=rtpmap:96 H264/90000", describe.Body);
        Assert.Contains("packetization-mode=1", describe.Body);

        var setup = await client.RequestAsync("SETUP", Url("cam1/h264/track1"), ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"));
        Assert.Equal(200, setup.Status);
        Assert.Equal(200, (await client.RequestAsync("PLAY", Url("cam1/h264"), ("Session", setup.SessionId!))).Status);

        var first = await client.ReadFrameAsync(anyPacketStartsFrame: true);
        Assert.All(first, p => Assert.Equal(H264Packetizer.PayloadType, p[1] & 0x7F));
        Assert.Equal([7, 8, 5], NalTypes(first).Where(t => t is 5 or 7 or 8).Distinct());
        var second = await client.ReadFrameAsync(anyPacketStartsFrame: true);
        Assert.Contains(1, NalTypes(second));
        Assert.True(BinaryPrimitives.ReadUInt32BigEndian(second[0].AsSpan(4)) > BinaryPrimitives.ReadUInt32BigEndian(first[0].AsSpan(4)));

        // Once a key frame exists, the SDP carries the parameter sets too.
        var again = await client.RequestAsync("DESCRIBE", Url("cam1/h264"), ("Accept", "application/sdp"));
        Assert.Contains("sprop-parameter-sets=", again.Body);
    }

    [FfmpegFact]
    public async Task Media_OffersAnH264Profile_WithItsOwnStreamAndSettings()
    {
        var media = await Connect("cam1").GetMediaService() ?? throw new InvalidOperationException("no media service");
        var profiles = media.GetProfiles();
        Assert.Equal(["JPEG", "H264"], profiles.Select(p => p.Encoding));
        Assert.EndsWith("/cam1/h264", await media.GetStreamUri("Profile_2"));

        var original = Store.Current.Cameras.Single(c => c.Id == "cam1").H264;
        var h264 = (await media.GetVideoEncoderConfigsAsync()).Single(c => c.Encoding == "H264");
        try
        {
            await media.SetVideoEncoderConfigAsync(h264 with { Width = 1280, Height = 720, BitrateLimit = 2000 });
            var saved = Store.Current.Cameras.Single(c => c.Id == "cam1");
            Assert.Equal((1280, 720, 2000), (saved.H264.Width, saved.H264.Height, saved.H264.BitrateKbps));
            Assert.Equal((640, 360), (saved.Video.Width, saved.Video.Height));
        }
        finally
        {
            Store.Update(w => w.Cameras.Single(c => c.Id == "cam1").H264 = original);
        }
    }

    // Types of the NAL units in one access unit's packets: a single NAL unit, or the first FU-A fragment.
    private static IEnumerable<int> NalTypes(List<byte[]> packets) =>
        packets
            .Where(p => (p[12] & 0x1F) != 28 || (p[13] & 0x80) != 0)
            .Select(p => (p[12] & 0x1F) == 28 ? p[13] & 0x1F : p[12] & 0x1F);
}
