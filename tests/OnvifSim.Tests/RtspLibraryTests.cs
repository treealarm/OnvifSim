using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OnvifSim.Rtsp;
using OnvifSim.Rtsp.Rtp;

namespace OnvifSim.Tests;

// The RTSP library on its own, without the simulator: a media source written here, the server started
// by hand.
public class RtspLibraryTests
{
    // One stream at rtsp://host/live/text whose only track sends "hello" every 50 ms; open to everyone
    // with the password "secret".
    private sealed class TextSource : IRtspMediaSource
    {
        public RtspMedia? Find(IReadOnlyList<string> path) => path is ["live", "text"]
            ? new RtspMedia("Text", [new RtspTrack("words", "application", 98, "text", PlayAsync)])
            : null;

        public bool IsAuthorized(RtspRequest request, IReadOnlyList<string> path) =>
            request.Header("Authorization") == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("any:secret"));

        public IEnumerable<string> Challenges() => ["Basic realm=\"test\""];

        private static async Task PlayAsync(RtpSender sender, CancellationToken cancellationToken)
        {
            var batch = new RtpPacketBatch();
            while (true)
            {
                batch.Clear();
                MetadataPacketizer.Packetize(sender.Stream, "hello"u8.ToArray(), Stopwatch.GetTimestamp(), batch);
                await sender.SendAsync(batch, cancellationToken);
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    [Fact]
    public async Task Server_StreamsWhatItsMediaSourceGives()
    {
        using var server = new RtspServer(new RtspServerOptions { Port = 0, RtpPort = 0, ServerName = "Test/1" },
            new TextSource(), NullLogger<RtspServer>.Instance);
        await server.StartAsync(CancellationToken.None);
        try
        {
            await using var client = await RtspTestClient.ConnectAsync(server.Port);
            string url = RtspTestClient.Url(server.Port, "live/text");
            var refused = await client.RequestAsync("DESCRIBE", url);
            Assert.Equal(401, refused.Status);
            Assert.Equal("Basic realm=\"test\"", refused.Header("WWW-Authenticate"));
            Assert.Equal("Test/1", refused.Header("Server"));

            (client.Username, client.Password, client.UseBasic) = ("any", "secret", true);
            Assert.Equal(404, (await client.RequestAsync("DESCRIBE", RtspTestClient.Url(server.Port, "live"))).Status);
            var describe = await client.RequestAsync("DESCRIBE", url);
            Assert.Contains("s=Text\r\n", describe.Body);
            Assert.Contains($"m=application 0 RTP/AVP 98\r\na=rtpmap:98 text/90000\r\na=control:{url}/words\r\n", describe.Body);

            var setup = await client.RequestAsync("SETUP", url + "/words", ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"));
            Assert.Equal(200, setup.Status);
            Assert.Equal(404, (await client.RequestAsync("SETUP", url + "/nothing", ("Transport", "RTP/AVP/TCP;unicast"))).Status);
            Assert.Equal(200, (await client.RequestAsync("PLAY", url, ("Session", setup.SessionId!))).Status);

            var packet = (await client.ReadFrameAsync(anyPacketStartsFrame: true)).Single();
            Assert.Equal(98, packet[1] & 0x7F);
            Assert.Equal("hello", Encoding.UTF8.GetString(packet[12..]));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }
}
