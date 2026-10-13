using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using OnvifLib;
using OnvifSim.Onvif;
using OnvifSim.World;
using StbImageSharp;

namespace OnvifSim.Tests;

// The simulator in-process with OnvifLib, the library it exists to test, as the client.
public class OnvifServerTests(SimFactory factory) : IClassFixture<SimFactory>
{
    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Ter = "http://www.onvif.org/ver10/error";

    private int PortOf(string cameraId) => factory.Services.GetRequiredService<OnvifHost>().PortOf(cameraId)
        ?? throw new InvalidOperationException($"{cameraId} is not served");

    private Camera Connect(string cameraId, string password = "admin") =>
        Camera.Create("127.0.0.1", PortOf(cameraId), "admin", password, timeout: 10);

    private static async Task<MediaService> MediaAsync(Camera camera) =>
        await camera.GetMediaService() ?? throw new InvalidOperationException("no media service");

    [Fact]
    public async Task DeviceInformation_DescribesTheSimulatedCamera()
    {
        var info = await Connect("cam2").GetDeviceInformationAsync();
        Assert.Equal("OnvifSim", info.Manufacturer);
        Assert.Equal("Simulated PTZ camera", info.Model);
        Assert.Equal("cam2", info.SerialNumber);
    }

    [Fact]
    public async Task Services_AdvertiseDeviceMediaAndMedia2_OnTheCamerasOwnPort()
    {
        var services = await Connect("cam1").GetServicesAsync();
        Assert.NotNull(services);
        Assert.Equal($"http://127.0.0.1:{PortOf("cam1")}/onvif/media2_service", services["http://www.onvif.org/ver20/media/wsdl"]);
        Assert.Contains("http://www.onvif.org/ver10/media/wsdl", services.Keys);
        Assert.Contains("http://www.onvif.org/ver10/device/wsdl", services.Keys);
    }

    [Fact]
    public async Task Media_GivesTheRtspUri_AndASnapshotOfTheConfiguredSize()
    {
        var media = await MediaAsync(Connect("cam1"));
        var profile = Assert.Single(media.GetProfiles());
        Assert.Equal(("Profile_1", "JPEG", 640, 360), (profile.Token, profile.Encoding, profile.Width, profile.Height));

        int rtspPort = factory.Services.GetRequiredService<Streaming.Rtsp.RtspServer>().Port;
        Assert.Equal($"rtsp://127.0.0.1:{rtspPort}/cam1", await media.GetStreamUri(profile.Token));

        var jpeg = (await media.GetImage())?.Data;
        Assert.NotNull(jpeg);
        var decoded = StbImageSharp.ImageResult.FromMemory(jpeg, ColorComponents.RedGreenBlue);
        Assert.Equal((640, 360), (decoded.Width, decoded.Height));
    }

    [Fact]
    public async Task SetVideoEncoderConfig_ChangesTheWorld_AndOutOfRangeValuesAreRefused()
    {
        var store = factory.Services.GetRequiredService<WorldStore>();
        var original = store.Current.Cameras.Single(c => c.Id == "cam2").Video;
        var media = await MediaAsync(Connect("cam2"));
        var config = (await media.GetVideoEncoderConfigsAsync()).Single();
        try
        {
            // OnvifLib 1.2.0 does not pass Quality on through Media2; SetQualityOverMedia2 covers it.
            await media.SetVideoEncoderConfigAsync(config with { Width = 1280, Height = 720, FrameRateLimit = 5 });
            var video = store.Current.Cameras.Single(c => c.Id == "cam2").Video;
            Assert.Equal((1280, 720, 5), (video.Width, video.Height, video.Fps));
            Assert.Equal((1280, 720), (await media.GetVideoEncoderConfigsAsync()).Select(c => (c.Width, c.Height)).Single());

            await Assert.ThrowsAnyAsync<Exception>(() => media.SetVideoEncoderConfigAsync(config with { Width = 1000, Height = 500 }));
            await Assert.ThrowsAnyAsync<Exception>(() => media.SetVideoEncoderConfigAsync(config with { FrameRateLimit = 120 }));
            Assert.Equal(1280, store.Current.Cameras.Single(c => c.Id == "cam2").Video.Width);
        }
        finally
        {
            store.Update(w =>
            {
                var video = w.Cameras.Single(c => c.Id == "cam2").Video;
                (video.Width, video.Height, video.Fps, video.Quality) = (original.Width, original.Height, original.Fps, original.Quality);
            });
        }
    }

    [Fact]
    public async Task SetQualityOverMedia2_ChangesTheWorld()
    {
        var store = factory.Services.GetRequiredService<WorldStore>();
        int original = store.Current.Cameras.Single(c => c.Id == "cam1").Video.Quality;
        var response = await PostAsync("cam1", Envelope(
            "<SetVideoEncoderConfiguration xmlns=\"http://www.onvif.org/ver20/media/wsdl\" xmlns:tt=\"http://www.onvif.org/ver10/schema\">" +
            "<Configuration token=\"VideoEncoder_1\"><tt:Name>VideoEncoder</tt:Name><tt:UseCount>1</tt:UseCount><tt:Encoding>JPEG</tt:Encoding>" +
            "<tt:Resolution><tt:Width>640</tt:Width><tt:Height>360</tt:Height></tt:Resolution>" +
            "<tt:RateControl><tt:FrameRateLimit>10</tt:FrameRateLimit><tt:BitrateLimit>2000</tt:BitrateLimit></tt:RateControl>" +
            "<tt:Quality>42</tt:Quality></Configuration></SetVideoEncoderConfiguration>",
            UsernameToken("admin", "admin", DateTime.UtcNow)));
        try
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(42, store.Current.Cameras.Single(c => c.Id == "cam1").Video.Quality);
        }
        finally
        {
            store.Update(w => w.Cameras.Single(c => c.Id == "cam1").Video.Quality = original);
        }
    }

    [Fact]
    public async Task WrongPassword_IsRefused()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Connect("cam1", password: "wrong").GetDeviceInformationAsync());
    }

    [Fact]
    public async Task PreAuthOperations_AnswerWithoutCredentials_OthersChallenge()
    {
        var time = await PostAsync("cam1", Envelope("<GetSystemDateAndTime xmlns=\"http://www.onvif.org/ver10/device/wsdl\"/>"));
        Assert.Equal(HttpStatusCode.OK, time.StatusCode);

        var info = await PostAsync("cam1", Envelope("<GetDeviceInformation xmlns=\"http://www.onvif.org/ver10/device/wsdl\"/>"));
        Assert.Equal(HttpStatusCode.Unauthorized, info.StatusCode);
        Assert.Contains(info.Headers.WwwAuthenticate, h => h.Scheme == "Digest");
    }

    [Fact]
    public async Task UsernameToken_IsChecked_ForPasswordAndClock()
    {
        const string request = "<GetDeviceInformation xmlns=\"http://www.onvif.org/ver10/device/wsdl\"/>";
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("cam1", Envelope(request, UsernameToken("admin", "admin", DateTime.UtcNow)))).StatusCode);

        var wrong = await PostAsync("cam1", Envelope(request, UsernameToken("admin", "nope", DateTime.UtcNow)));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(["ter:NotAuthorized"], await SubcodesAsync(wrong));

        var stale = await PostAsync("cam1", Envelope(request, UsernameToken("admin", "admin", DateTime.UtcNow.AddHours(-1))));
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
    }

    [Fact]
    public async Task UnknownOperation_IsActionNotSupported_AndBadXml_IsWellFormed()
    {
        var unknown = await PostAsync("cam1", Envelope("<NoSuchThing xmlns=\"http://www.onvif.org/ver10/device/wsdl\"/>"));
        Assert.Equal(HttpStatusCode.InternalServerError, unknown.StatusCode);
        Assert.Equal(["ter:ActionNotSupported"], await SubcodesAsync(unknown));

        var broken = await PostAsync("cam1", "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body>");
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Equal(["ter:WellFormed"], await SubcodesAsync(broken));
    }

    [Fact]
    public async Task Soap11Request_GetsASoap11Answer()
    {
        var response = await PostAsync("cam1",
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>" +
            "<GetSystemDateAndTime xmlns=\"http://www.onvif.org/ver10/device/wsdl\"/></s:Body></s:Envelope>",
            "text/xml; charset=utf-8");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/xml", response.Content.Headers.ContentType!.ToString());
        Assert.Equal("http://schemas.xmlsoap.org/soap/envelope/", XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!.Name.NamespaceName);
    }

    [Fact]
    public async Task Media1_ReturnsTheSameProfile()
    {
        var response = await PostAsync("cam1", Envelope(
            "<GetProfiles xmlns=\"http://www.onvif.org/ver10/media/wsdl\"/>", UsernameToken("admin", "admin", DateTime.UtcNow)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        XNamespace trt = "http://www.onvif.org/ver10/media/wsdl", tt = "http://www.onvif.org/ver10/schema";
        var profile = XDocument.Parse(await response.Content.ReadAsStringAsync()).Descendants(trt + "Profiles").Single();
        Assert.Equal("Profile_1", profile.Attribute("token")?.Value);
        Assert.Equal("JPEG", profile.Element(tt + "VideoEncoderConfiguration")?.Element(tt + "Encoding")?.Value);
    }

    [Fact]
    public async Task Listeners_FollowCamerasAddedAndRemoved()
    {
        var store = factory.Services.GetRequiredService<WorldStore>();
        var host = factory.Services.GetRequiredService<OnvifHost>();
        store.Update(w => w.Cameras.Add(new CameraModel { Id = "cam9", Name = "Added", Position = new(0, 5, 0) }));
        try
        {
            for (int i = 0; i < 50 && host.PortOf("cam9") == null; i++) await Task.Delay(100);
            var info = await Camera.Create("127.0.0.1", host.PortOf("cam9") ?? 0, "", "", timeout: 10).GetDeviceInformationAsync();
            Assert.Equal("cam9", info.SerialNumber);
        }
        finally
        {
            store.Update(w => w.Cameras.RemoveAll(c => c.Id == "cam9"));
        }
        for (int i = 0; i < 50 && host.PortOf("cam9") != null; i++) await Task.Delay(100);
        Assert.Null(host.PortOf("cam9"));
    }

    private async Task<HttpResponseMessage> PostAsync(string cameraId, string envelope, string contentType = "application/soap+xml; charset=utf-8")
    {
        using var client = new HttpClient();
        var content = new StringContent(envelope, Encoding.UTF8);
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        return await client.PostAsync($"http://127.0.0.1:{PortOf(cameraId)}/onvif/device_service", content);
    }

    private static async Task<string[]> SubcodesAsync(HttpResponseMessage response) =>
        XDocument.Parse(await response.Content.ReadAsStringAsync())
            .Descendants(Soap + "Subcode").Select(s => s.Element(Soap + "Value")!.Value).ToArray();

    private static string Envelope(string body, string? security = null) =>
        $"<s:Envelope xmlns:s=\"{Soap}\"><s:Header>{security}</s:Header><s:Body>{body}</s:Body></s:Envelope>";

    private static string UsernameToken(string user, string password, DateTime created)
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        var createdText = created.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var digest = Convert.ToBase64String(SHA1.HashData([.. nonce, .. Encoding.UTF8.GetBytes(createdText + password)]));
        return "<wsse:Security xmlns:wsse=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\" " +
               "xmlns:wsu=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\"><wsse:UsernameToken>" +
               $"<wsse:Username>{user}</wsse:Username>" +
               $"<wsse:Password Type=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest\">{digest}</wsse:Password>" +
               $"<wsse:Nonce>{Convert.ToBase64String(nonce)}</wsse:Nonce><wsu:Created>{createdText}</wsu:Created>" +
               "</wsse:UsernameToken></wsse:Security>";
    }
}

// Discovery on a private port, so the test neither depends on nor disturbs 3702.
public sealed class DiscoverySimFactory : SimFactory
{
    public int Port { get; } = FreeUdpPort();

    protected override int DiscoveryPort => Port;

    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}

public class WsDiscoveryTests(DiscoverySimFactory factory) : IClassFixture<DiscoverySimFactory>
{
    private static readonly XNamespace Disc = "http://schemas.xmlsoap.org/ws/2005/04/discovery";

    [Fact]
    public async Task Probe_GetsOneMatchPerCamera_WithItsOwnXAddr()
    {
        var host = factory.Services.GetRequiredService<OnvifHost>();
        var matches = await ProbeAsync("<d:Types xmlns:dn=\"http://www.onvif.org/ver10/network/wsdl\">dn:NetworkVideoTransmitter</d:Types>");
        var xaddrs = matches.Select(m => m.Element(Disc + "XAddrs")!.Value).Order().ToList();
        Assert.Equal(
            new[] { "cam1", "cam2" }.Select(id => $"http://127.0.0.1:{host.PortOf(id)}/onvif/device_service").Order(),
            xaddrs);
        Assert.All(matches, m => Assert.Contains("onvif://www.onvif.org/type/video_encoder", m.Element(Disc + "Scopes")!.Value));
    }

    [Fact]
    public async Task Probe_ForOtherTypes_IsIgnored()
    {
        Assert.Empty(await ProbeAsync("<d:Types xmlns:p=\"urn:printers\">p:Printer</d:Types>"));
    }

    private async Task<List<XElement>> ProbeAsync(string types)
    {
        _ = factory.Services; // starts the host
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var probe =
            "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:a=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" " +
            $"xmlns:d=\"{Disc}\"><s:Header><a:MessageID>urn:uuid:{Guid.NewGuid()}</a:MessageID>" +
            "<a:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</a:Action></s:Header>" +
            $"<s:Body><d:Probe>{types}</d:Probe></s:Body></s:Envelope>";
        var bytes = Encoding.UTF8.GetBytes(probe);
        await client.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, factory.Port));

        var matches = new List<XElement>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        try
        {
            while (true)
            {
                var reply = await client.ReceiveAsync(timeout.Token);
                matches.AddRange(XDocument.Parse(Encoding.UTF8.GetString(reply.Buffer)).Descendants(Disc + "ProbeMatch"));
            }
        }
        catch (OperationCanceledException) { }
        return matches;
    }
}
