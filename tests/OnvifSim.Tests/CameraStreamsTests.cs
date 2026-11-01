using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using OnvifLib;
using OnvifSim.Onvif;
using OnvifSim.Rtsp;
using OnvifSim.Streaming;
using OnvifSim.World;
using StbImageSharp;

namespace OnvifSim.Tests;

// Streams are ONVIF profiles and RTSP paths: adding one offers a profile and serves its path, removing
// it takes both away, and a camera's RTSP port and bind address move its streams to their own listener.
public class CameraStreamsTests(SimFactory factory) : IClassFixture<SimFactory>
{
    private HttpClient Client => factory.CreateClient();
    private OnvifHost Onvif => factory.Services.GetRequiredService<OnvifHost>();
    private RtspEndpoints Endpoints => factory.Services.GetRequiredService<RtspEndpoints>();
    private RtspServer Server => factory.Services.GetRequiredService<RtspServer>();

    [Fact]
    public async Task AnAddedStream_OffersAProfileAndServesItsRtspPath()
    {
        var client = Client;
        await CreateCamera(client, "scam", streams: ThreeStreams());
        try
        {
            await WaitFor(() => Onvif.PortOf("scam") is not null && Endpoints.PortOf("scam") is not null);
            int onvifPort = Onvif.PortOf("scam")!.Value;
            int rtspPort = Endpoints.PortOf("scam")!.Value;
            var media = (await Camera.Create("127.0.0.1", onvifPort, "admin", "admin", timeout: 10).GetMediaService())!;

            var profiles = media.GetProfiles();
            Assert.Contains(profiles, profile => profile.Token == "Profile_3" && profile.Encoding == "JPEG");
            // No ffmpeg here: the H.264 stream stays in the world but is not offered.
            Assert.DoesNotContain(profiles, profile => profile.Token == "Profile_2");
            Assert.Equal($"rtsp://127.0.0.1:{rtspPort}/scam/second", await media.GetStreamUri("Profile_3"));

            await using var rtsp = await RtspTestClient.ConnectAsync(rtspPort);
            var setup = await rtsp.RequestAsync("SETUP", RtspTestClient.Url(rtspPort, "scam/second/track1"),
                ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"));
            Assert.Equal(200, setup.Status);
            var play = await rtsp.RequestAsync("PLAY", RtspTestClient.Url(rtspPort, "scam/second"), ("Session", setup.SessionId!));
            Assert.Equal(200, play.Status);
            var image = StbImageSharp.ImageResult.FromMemory(Rfc2435Reference.Depacketize(await rtsp.ReadFrameAsync()), StbImageSharp.ColorComponents.RedGreenBlue);
            Assert.Equal((320, 240), (image.Width, image.Height));
        }
        finally
        {
            await client.DeleteAsync("/api/cameras/scam");
        }
    }

    [Fact]
    public async Task RemovingAStream_TakesItsProfileAndPathAway()
    {
        var client = Client;
        await CreateCamera(client, "scam", streams: ThreeStreams());
        try
        {
            await WaitFor(() => Endpoints.PortOf("scam") is not null);
            int rtspPort = Endpoints.PortOf("scam")!.Value;
            await using var playing = await RtspTestClient.ConnectAsync(rtspPort);
            var setup = await playing.RequestAsync("SETUP", RtspTestClient.Url(rtspPort, "scam/second/track1"),
                ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"));
            Assert.Equal(200, setup.Status);
            Assert.Equal(200, (await playing.RequestAsync("PLAY", RtspTestClient.Url(rtspPort, "scam/second"), ("Session", setup.SessionId!))).Status);

            var world = await World(client);
            var camera = world.Cameras.Single(c => c.Id == "scam");
            camera.Streams = camera.Streams!.Where(stream => stream.Token != "Profile_3").ToList();
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync("/api/cameras/scam", Json(camera))).StatusCode);
            // The stream is gone, so its session ends with it.
            for (int attempt = 0; attempt < 100 && Server.FindSession(setup.SessionId!) != null; attempt++) await Task.Delay(50);
            Assert.Null(Server.FindSession(setup.SessionId!));

            await using var rtsp = await RtspTestClient.ConnectAsync(rtspPort);
            Assert.Equal(404, (await rtsp.RequestAsync("DESCRIBE", RtspTestClient.Url(rtspPort, "scam/second"))).Status);
            var media = (await Camera.Create("127.0.0.1", Onvif.PortOf("scam")!.Value, "admin", "admin", timeout: 10).GetMediaService())!;
            Assert.DoesNotContain(media.GetProfiles(), profile => profile.Token == "Profile_3");
        }
        finally
        {
            await client.DeleteAsync("/api/cameras/scam");
        }
    }

    [Fact]
    public async Task ACameraServesItsStreamsOnItsOwnRtspPort()
    {
        var client = Client;
        int port = FreeTcpPort();
        await CreateCamera(client, "portcam", rtspPort: port);
        try
        {
            await WaitFor(() => Endpoints.PortOf("portcam") == port && Onvif.PortOf("portcam") is not null);
            var media = (await Camera.Create("127.0.0.1", Onvif.PortOf("portcam")!.Value, "admin", "admin", timeout: 10).GetMediaService())!;
            Assert.Equal($"rtsp://127.0.0.1:{port}/portcam", await media.GetStreamUri("Profile_1"));

            await using var own = await RtspTestClient.ConnectAsync(port);
            Assert.Equal(200, (await own.RequestAsync("DESCRIBE", RtspTestClient.Url(port, "portcam"))).Status);
            await using var shared = await RtspTestClient.ConnectAsync(Server.Port);
            Assert.Equal(404, (await shared.RequestAsync("DESCRIBE", RtspTestClient.Url(Server.Port, "portcam"))).Status);
        }
        finally
        {
            await client.DeleteAsync("/api/cameras/portcam");
        }
    }

    [Fact]
    public async Task ABoundCamera_ListensOnThatAddressAndAdvertisesIt()
    {
        var client = Client;
        int port = FreeTcpPort();
        await CreateCamera(client, "boundcam", rtspPort: port, bindAddress: "127.0.0.1");
        try
        {
            await WaitFor(() => Onvif.PortOf("boundcam") is not null && Endpoints.PortOf("boundcam") == port);
            int onvifPort = Onvif.PortOf("boundcam")!.Value;
            var media = (await Camera.Create("127.0.0.1", onvifPort, "admin", "admin", timeout: 10).GetMediaService())!;
            Assert.Equal($"rtsp://127.0.0.1:{port}/boundcam", await media.GetStreamUri("Profile_1"));

            await using var rtsp = await RtspTestClient.ConnectAsync(port);
            Assert.Equal(200, (await rtsp.RequestAsync("DESCRIBE", RtspTestClient.Url(port, "boundcam"))).Status);
        }
        finally
        {
            await client.DeleteAsync("/api/cameras/boundcam");
        }
    }

    private static async Task CreateCamera(
        HttpClient client, string id, object? streams = null, int rtspPort = 0, string? bindAddress = null)
    {
        var response = await client.PutAsync($"/api/cameras/{id}", Json(new
        {
            name = $"Test {id}",
            kind = "fixed",
            position = new { x = 0, y = 3, z = 30 },
            heading = 0,
            pitch = -10,
            users = Array.Empty<object>(),
            rtspPort,
            bindAddress,
            streams,
        }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static object[] ThreeStreams() =>
    [
        new
        {
            token = "Profile_1", name = "Main", codec = "jpeg", path = "scam",
            video = new { width = 640, height = 360, fps = 10, quality = 70, rotate = 0 },
        },
        new
        {
            token = "Profile_2", name = "H264", codec = "h264", path = "scam/h264",
            h264 = new { width = 640, height = 360, fps = 10, bitrateKbps = 1024, govLength = 10 },
        },
        new
        {
            token = "Profile_3", name = "Second", codec = "jpeg", path = "scam/second",
            video = new { width = 320, height = 240, fps = 5, quality = 80, rotate = 0 },
        },
    ];

    private static async Task<WorldModel> World(HttpClient client) =>
        (await client.GetFromJsonAsync<WorldModel>("/api/world", WorldStore.Json))!;

    private static JsonContent Json(object value) => JsonContent.Create(value, options: WorldStore.Json);

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail("The listeners did not follow the world in time.");
    }
}
