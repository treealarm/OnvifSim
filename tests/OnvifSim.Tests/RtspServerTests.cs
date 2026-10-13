using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using OnvifSim.Streaming.Rtsp;
using OnvifSim.World;
using StbImageSharp;

namespace OnvifSim.Tests;

// A simulator whose cam1 has no users, for the unauthenticated path.
public sealed class OpenCameraSimFactory : SimFactory
{
    public OpenCameraSimFactory()
    {
        var store = new WorldStore(Data.Path);
        var world = store.Current;
        world.Cameras.Single(c => c.Id == "cam1").Users.Clear();
        store.Replace(world);
    }
}

public class RtspServerTests(SimFactory factory) : IClassFixture<SimFactory>
{
    private RtspServer Server => factory.Services.GetRequiredService<RtspServer>();

    private async Task<RtspTestClient> ConnectAsync(bool withCredentials = true)
    {
        var client = await RtspTestClient.ConnectAsync(Server.Port);
        if (withCredentials)
        {
            client.Username = "admin";
            client.Password = "admin";
        }
        return client;
    }

    private string Url(string path) => RtspTestClient.Url(Server.Port, path);

    [Fact]
    public async Task Options_ListsTheSupportedMethods_WithoutCredentials()
    {
        await using var client = await ConnectAsync(withCredentials: false);
        var response = await client.RequestAsync("OPTIONS", Url("cam1"));
        Assert.Equal(200, response.Status);
        Assert.Contains("DESCRIBE", response.Header("Public"));
        Assert.Contains("GET_PARAMETER", response.Header("Public"));
    }

    [Fact]
    public async Task Describe_WithoutCredentials_ChallengesWithDigestAndBasic()
    {
        await using var client = await ConnectAsync(withCredentials: false);
        var response = await client.RequestAsync("DESCRIBE", Url("cam1"));
        Assert.Equal(401, response.Status);
        var challenges = response.Headers("WWW-Authenticate").ToList();
        Assert.StartsWith("Digest realm=\"OnvifSim\", nonce=", challenges[0]);
        Assert.DoesNotContain("qop", challenges[0]);
        Assert.Equal("Basic realm=\"OnvifSim\"", challenges[1]);
    }

    [Fact]
    public async Task Describe_WithDigest_ReturnsAJpegSdp()
    {
        await using var client = await ConnectAsync();
        var response = await client.RequestAsync("DESCRIBE", Url("cam1"));
        Assert.Equal(200, response.Status);
        Assert.Equal("application/sdp", response.Header("Content-Type"));
        Assert.Equal(Url("cam1") + "/", response.Header("Content-Base"));
        Assert.Contains("m=video 0 RTP/AVP 26\r\n", response.Body);
        Assert.Contains("a=rtpmap:26 JPEG/90000\r\n", response.Body);
        Assert.Contains($"a=control:{Url("cam1")}/track1\r\n", response.Body);
    }

    [Fact]
    public async Task Describe_WithBasic_Succeeds_AndAWrongPasswordFails()
    {
        await using var client = await ConnectAsync();
        client.UseBasic = true;
        Assert.Equal(200, (await client.RequestAsync("DESCRIBE", Url("cam1"))).Status);
        client.Password = "wrong";
        Assert.Equal(401, (await client.RequestAsync("DESCRIBE", Url("cam1"))).Status);
        client.UseBasic = false;
        Assert.Equal(401, (await client.RequestAsync("DESCRIBE", Url("cam1"))).Status);
    }

    [Fact]
    public async Task UnknownCameraOrTrack_Is404()
    {
        await using var client = await ConnectAsync();
        Assert.Equal(404, (await client.RequestAsync("DESCRIBE", Url("nope"))).Status);
        Assert.Equal(404, (await client.RequestAsync("DESCRIBE", Url("cam1/h264"))).Status);
    }

    [Fact]
    public async Task PlayOverTcp_DeliversFramesThatDecode_AndTeardownEndsTheSession()
    {
        await using var client = await ConnectAsync();
        var setup = await client.RequestAsync("SETUP", Url("cam2/track1"), ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"));
        Assert.Equal(200, setup.Status);
        Assert.StartsWith("RTP/AVP/TCP;unicast;interleaved=0-1;ssrc=", setup.Header("Transport"));
        var session = setup.SessionId!;
        Assert.EndsWith(";timeout=60", setup.Header("Session"));

        var play = await client.RequestAsync("PLAY", Url("cam2"), ("Session", session));
        Assert.Equal(200, play.Status);
        var rtpInfo = play.Header("RTP-Info")!;
        Assert.StartsWith($"url={Url("cam2/track1")};seq=", rtpInfo);
        ushort announcedSequence = ushort.Parse(rtpInfo.Split("seq=")[1].Split(';')[0]);

        var first = await client.ReadFrameAsync();
        Assert.Equal(announcedSequence, BinaryPrimitives.ReadUInt16BigEndian(first[0].AsSpan(2)));
        var second = await client.ReadFrameAsync();
        foreach (var frame in new[] { first, second })
        {
            var image = ImageResult.FromMemory(Rfc2435Reference.Depacketize(frame), ColorComponents.RedGreenBlue);
            Assert.Equal((640, 360), (image.Width, image.Height));
        }

        Assert.Equal(200, (await client.RequestAsync("TEARDOWN", Url("cam2"), ("Session", session))).Status);
        Assert.Null(Server.FindSession(session));
        Assert.Equal(454, (await client.RequestAsync("PLAY", Url("cam2"), ("Session", session))).Status);
    }

    [Fact]
    public async Task MetadataTrack_IsInTheSdp_AndPlaysBesideTheVideo()
    {
        await using var client = await ConnectAsync();
        var describe = await client.RequestAsync("DESCRIBE", Url("cam1"));
        Assert.Contains("m=application 0 RTP/AVP 107\r\na=rtpmap:107 vnd.onvif.metadata/90000\r\n", describe.Body);
        Assert.Contains($"a=control:{Url("cam1")}/track2\r\n", describe.Body);

        var session = (await client.RequestAsync("SETUP", Url("cam1/track1"), ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"))).SessionId!;
        // Not on the video's channels; without channels asked for, the second track takes the next pair.
        Assert.Equal(461, (await client.RequestAsync("SETUP", Url("cam1/track2"), ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"), ("Session", session))).Status);
        var metadata = await client.RequestAsync("SETUP", Url("cam1/track2"), ("Transport", "RTP/AVP/TCP;unicast"), ("Session", session));
        Assert.Equal(200, metadata.Status);
        Assert.Equal(session, metadata.SessionId);
        Assert.StartsWith("RTP/AVP/TCP;unicast;interleaved=2-3;ssrc=", metadata.Header("Transport"));

        var play = await client.RequestAsync("PLAY", Url("cam1"), ("Session", session));
        var rtpInfo = play.Header("RTP-Info")!.Split(',');
        Assert.Equal(2, rtpInfo.Length);
        Assert.StartsWith($"url={Url("cam1/track1")};seq=", rtpInfo[0]);
        Assert.StartsWith($"url={Url("cam1/track2")};seq=", rtpInfo[1]);

        var document = await client.ReadFrameAsync(channel: 2, anyPacketStartsFrame: true);
        Assert.Equal(107, document[0][1] & 0x7f);
        var xml = System.Text.Encoding.UTF8.GetString(document.SelectMany(packet => packet[12..]).ToArray());
        Assert.Equal("MetadataStream", System.Xml.Linq.XDocument.Parse(xml).Root!.Name.LocalName);
        Assert.NotEmpty(await client.ReadFrameAsync(channel: 0));

        Assert.Equal(200, (await client.RequestAsync("TEARDOWN", Url("cam1"), ("Session", session))).Status);
    }

    [Fact]
    public async Task MetadataTrack_PlaysAlone()
    {
        await using var client = await ConnectAsync();
        var setup = await client.RequestAsync("SETUP", Url("cam2/track2"), ("Transport", "RTP/AVP/TCP;unicast;interleaved=4-5"));
        Assert.Equal(200, setup.Status);
        Assert.Equal(200, (await client.RequestAsync("PLAY", Url("cam2"), ("Session", setup.SessionId!))).Status);
        var document = await client.ReadFrameAsync(channel: 4, anyPacketStartsFrame: true);
        Assert.StartsWith("<?xml", System.Text.Encoding.UTF8.GetString(document[0], 12, document[0].Length - 12));

        // Tracks are set up before PLAY.
        var late = await client.RequestAsync("SETUP", Url("cam2/track1"), ("Transport", "RTP/AVP/TCP;unicast"), ("Session", setup.SessionId!));
        Assert.Equal(455, late.Status);
    }

    [Fact]
    public async Task PlayOverUdp_DeliversFramesToTheClientPort()
    {
        using var rtp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var rtcp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int rtpPort = ((IPEndPoint)rtp.Client.LocalEndPoint!).Port;
        int rtcpPort = ((IPEndPoint)rtcp.Client.LocalEndPoint!).Port;
        await using var client = await ConnectAsync();

        var setup = await client.RequestAsync("SETUP", Url("cam1/track1"), ("Transport", $"RTP/AVP;unicast;client_port={rtpPort}-{rtcpPort}"));
        Assert.Equal(200, setup.Status);
        Assert.Contains($"server_port={Server.RtpPort}-{Server.RtpPort + 1}", setup.Header("Transport"));
        Assert.Equal(200, (await client.RequestAsync("PLAY", Url("cam1"), ("Session", setup.SessionId!))).Status);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var packets = new List<byte[]>();
        while (true)
        {
            var datagram = (await rtp.ReceiveAsync(timeout.Token)).Buffer;
            bool startsFrame = datagram[13] == 0 && datagram[14] == 0 && datagram[15] == 0;
            if (packets.Count == 0 && !startsFrame) continue;
            packets.Add(datagram);
            if ((datagram[1] & 0x80) != 0) break;
        }
        var image = ImageResult.FromMemory(Rfc2435Reference.Depacketize(packets), ColorComponents.RedGreenBlue);
        Assert.Equal((640, 360), (image.Width, image.Height));

        var report = (await rtcp.ReceiveAsync(timeout.Token)).Buffer;
        Assert.Equal(200, report[1]);
        Assert.Equal(200, (await client.RequestAsync("TEARDOWN", Url("cam1"), ("Session", setup.SessionId!))).Status);
    }

    [Fact]
    public async Task ClosingTheConnection_EndsItsTcpSession()
    {
        string session;
        await using (var client = await ConnectAsync())
        {
            var setup = await client.RequestAsync("SETUP", Url("cam1/track1"), ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"));
            session = setup.SessionId!;
            await client.RequestAsync("PLAY", Url("cam1"), ("Session", session));
            await client.ReadFrameAsync();
        }
        for (int i = 0; i < 50 && Server.FindSession(session) != null; i++) await Task.Delay(100);
        Assert.Null(Server.FindSession(session));
    }

    [Fact]
    public async Task PipelinedRequests_AreAllAnswered_InOrder()
    {
        await using var client = await ConnectAsync(withCredentials: false);
        await client.SendRawAsync(
            $"OPTIONS {Url("cam1")} RTSP/1.0\r\nCSeq: 7\r\n\r\n" +
            $"DESCRIBE {Url("cam1")} RTSP/1.0\r\nCSeq: 8\r\n\r\n");
        var first = await client.ReadResponseAsync();
        var second = await client.ReadResponseAsync();
        Assert.Equal(("7", 200), (first.Header("CSeq"), first.Status));
        Assert.Equal(("8", 401), (second.Header("CSeq"), second.Status));
    }

    [Fact]
    public async Task UnsupportedTransportsAndUnknownSessions_AreRefused()
    {
        await using var client = await ConnectAsync();
        var multicast = await client.RequestAsync("SETUP", Url("cam1/track1"), ("Transport", "RTP/AVP;multicast;port=5000-5001"));
        Assert.Equal(461, multicast.Status);
        Assert.Equal(454, (await client.RequestAsync("PLAY", Url("cam1"), ("Session", "0123456789abcdef"))).Status);
        Assert.Equal(454, (await client.RequestAsync("GET_PARAMETER", Url("cam1"), ("Session", "0123456789abcdef"))).Status);
    }

    [Fact]
    public async Task MalformedRequest_GetsA400_AndTheConnectionCloses()
    {
        await using var client = await ConnectAsync();
        await client.SendRawAsync("this is not rtsp\r\n\r\n");
        Assert.Equal(400, (await client.ReadResponseAsync()).Status);
        await Assert.ThrowsAsync<EndOfStreamException>(client.ReadResponseAsync);
    }
}

public class RtspOpenCameraTests(OpenCameraSimFactory factory) : IClassFixture<OpenCameraSimFactory>
{
    [Fact]
    public async Task CameraWithoutUsers_NeedsNoCredentials()
    {
        int port = factory.Services.GetRequiredService<RtspServer>().Port;
        await using var client = await RtspTestClient.ConnectAsync(port);
        Assert.Equal(200, (await client.RequestAsync("DESCRIBE", RtspTestClient.Url(port, "cam1"))).Status);
        Assert.Equal(401, (await client.RequestAsync("DESCRIBE", RtspTestClient.Url(port, "cam2"))).Status);
    }
}
