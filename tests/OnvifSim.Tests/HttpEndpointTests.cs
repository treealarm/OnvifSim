using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using OnvifSim.World;
using StbImageSharp;

namespace OnvifSim.Tests;

public class SimFactory : WebApplicationFactory<Program>
{
    public TempDir Data { get; } = new();

    // Off unless a test class needs it: 3702 is shared with every other WS-Discovery listener.
    protected virtual int DiscoveryPort => 0;

    // JPEG only unless a test class needs H.264, so tests do not depend on what the machine has
    // installed; "" looks for ffmpeg on PATH.
    protected virtual string Ffmpeg => "none";

    // Settings reach the app as --name=value arguments, so every factory has its own data directory
    // even when test classes run in parallel; port 0 picks free RTSP and RTP ports, which also keeps the
    // tests clear of a simulator started by hand.
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("data", Data.Path);
        builder.UseSetting("rtsp-port", "0");
        builder.UseSetting("rtp-port", "0");
        builder.UseSetting("onvif-base-port", "0");
        builder.UseSetting("discovery-port", DiscoveryPort.ToString());
        builder.UseSetting("ffmpeg", Ffmpeg);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Data.Dispose();
    }
}

public class HttpEndpointTests(SimFactory factory) : IClassFixture<SimFactory>
{
    [Fact]
    public async Task Snapshot_IsAJpegOfTheConfiguredSize()
    {
        var bytes = await factory.CreateClient().GetByteArrayAsync("/api/cameras/cam2/snapshot.jpg");
        var img = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlue);
        Assert.Equal(640, img.Width);
        Assert.Equal(360, img.Height);
    }

    [Fact]
    public async Task UnknownCamera_Is404()
    {
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/cameras/nope/snapshot.jpg")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/cameras/nope/mjpeg")).StatusCode);
    }

    [Fact]
    public async Task Mjpeg_StreamsFrames()
    {
        var client = factory.CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resp = await client.GetAsync("/api/cameras/cam2/mjpeg", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.StartsWith("multipart/x-mixed-replace", resp.Content.Headers.ContentType!.ToString());
        var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        var buf = new byte[1 << 16];
        var text = new StringBuilder();
        while (text.ToString().Split("--onvifsimframe").Length - 1 < 2)
        {
            int n = await stream.ReadAsync(buf, cts.Token);
            Assert.True(n > 0, "stream ended");
            text.Append(Encoding.Latin1.GetString(buf, 0, n));
        }
    }

    [Fact]
    public async Task PutWorld_ChangesWhatCamerasSee()
    {
        var client = factory.CreateClient();
        var before = await client.GetByteArrayAsync("/api/cameras/cam1/snapshot.jpg");
        var world = JsonSerializer.Deserialize<WorldModel>(await client.GetStringAsync("/api/world"), WorldStore.Json)!;
        world.Cameras.Single(c => c.Id == "cam1").Heading += 90;
        var put = await client.PutAsync("/api/world",
            new StringContent(JsonSerializer.Serialize(world, WorldStore.Json), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var after = await client.GetByteArrayAsync("/api/cameras/cam1/snapshot.jpg");
        Assert.NotEqual(before, after);
    }
}
