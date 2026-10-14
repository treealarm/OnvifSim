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

        int rtspPort = factory.Services.GetRequiredService<Rtsp.RtspServer>().Port;
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

    // cam2 pans -180..180 at 90 deg/s and tilts -90..20 from -10: generic (0.25, 0.4545) is 45 deg right.
    [Fact]
    public async Task AbsoluteMove_IsMovingThenIdleAtTheTarget_AndThePictureTurns()
    {
        var camera = Connect("cam2");
        var ptz = await PtzAsync(camera);
        var media = await MediaAsync(camera);
        try
        {
            var before = (await media.GetImage())!.Data;
            await ptz.AbsoluteMoveAsync("Profile_1", 0.25f, 0.4545f, 0f, 1f, 1f);
            Assert.True((await ptz.GetStatusAsync("Profile_1"))!.Moving);

            var status = await WaitUntilIdleAsync(ptz);
            Assert.Equal(0.25f, status.Pan, 0.01f);
            Assert.Equal(0.4545f, status.Tilt, 0.01f);
            Assert.Equal(45, Store.Current.Cameras.Single(c => c.Id == "cam2").Ptz!.Pan, 0.5f);
            Assert.NotEqual(before, (await media.GetImage())!.Data);
        }
        finally
        {
            ResetCam2();
        }
    }

    [Fact]
    public async Task RelativeAndContinuousMoves_AndStopWithoutArguments()
    {
        var ptz = await PtzAsync(Connect("cam2"));
        try
        {
            // A generic step of 0.05 is 5% of the 110 degree tilt range.
            await ptz.RelativeMoveAsync("Profile_1", 0f, 0.05f, 0f, 1f, 1f);
            await WaitUntilIdleAsync(ptz);
            Assert.Equal(-4.5f, Store.Current.Cameras.Single(c => c.Id == "cam2").Ptz!.Tilt, 0.1f);

            await ptz.ContinuousMoveAsync("Profile_1", 0.5f, 0f, 0f, "PT30S");
            await Task.Delay(300);
            var stop = await PostAsync("cam2", Envelope(
                "<Stop xmlns=\"http://www.onvif.org/ver20/ptz/wsdl\"><ProfileToken>Profile_1</ProfileToken></Stop>",
                UsernameToken("admin", "admin", DateTime.UtcNow)), path: "ptz_service");
            Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
            Assert.False((await ptz.GetStatusAsync("Profile_1"))!.Moving);
            Assert.InRange(Store.Current.Cameras.Single(c => c.Id == "cam2").Ptz!.Pan, 10, 40);
        }
        finally
        {
            ResetCam2();
        }
    }

    [Fact]
    public async Task Presets_AreSaved_Reached_AndRemoved()
    {
        var ptz = await PtzAsync(Connect("cam2"));
        try
        {
            Store.Update(w => w.Cameras.Single(c => c.Id == "cam2").Ptz!.Pan = 30);
            var token = await ptz.SetPresetAsync("Profile_1", "Gate", "");
            Assert.Equal("Gate", Assert.Single(await ptz.GetPresetsAsync("Profile_1"), p => p.Token == token).Name);
            await Assert.ThrowsAnyAsync<Exception>(() => ptz.SetPresetAsync("Profile_1", "Gate", ""));

            Store.Update(w => w.Cameras.Single(c => c.Id == "cam2").Ptz!.Pan = 0);
            await ptz.GotoPresetAsync("Profile_1", token, 1f, 1f);
            var status = await WaitUntilIdleAsync(ptz);
            Assert.Equal((30f + 180) / 360 * 2 - 1, status.Pan, 0.01f);

            await ptz.RemovePresetAsync("Profile_1", token);
            Assert.Empty(await ptz.GetPresetsAsync("Profile_1"));
            await Assert.ThrowsAnyAsync<Exception>(() => ptz.GotoPresetAsync("Profile_1", token));
        }
        finally
        {
            ResetCam2();
        }
    }

    [Fact]
    public async Task AFixedCamera_HasNoPtzService()
    {
        var services = await Connect("cam1").GetServicesAsync();
        Assert.DoesNotContain("http://www.onvif.org/ver20/ptz/wsdl", services!.Keys);
        Assert.Contains("http://www.onvif.org/ver20/ptz/wsdl", (await Connect("cam2").GetServicesAsync())!.Keys);

        var nodes = await PostAsync("cam1", Envelope("<GetNodes xmlns=\"http://www.onvif.org/ver20/ptz/wsdl\"/>",
            UsernameToken("admin", "admin", DateTime.UtcNow)), path: "ptz_service");
        Assert.Equal(HttpStatusCode.InternalServerError, nodes.StatusCode);
        Assert.Contains("ter:ActionNotSupported", await SubcodesAsync(nodes));
    }

    [Fact]
    public async Task ImagingSettings_ChangeTheWorldAndThePicture()
    {
        var camera = Connect("cam1");
        var imaging = await camera.GetImagingService() ?? throw new InvalidOperationException("no imaging service");
        var media = await MediaAsync(camera);
        try
        {
            var settings = (await imaging.GetImagingSettingsAsync("VideoSource_1"))!;
            Assert.Equal((50f, 50f, 50f), (settings.Brightness, settings.Contrast, settings.ColorSaturation));
            var before = (await media.GetImage())!.Data;

            await imaging.SetImagingSettingsAsync("VideoSource_1", settings with { Brightness = 80, ColorSaturation = 0 });
            var saved = Store.Current.Cameras.Single(c => c.Id == "cam1").Imaging;
            Assert.Equal((80f, 50f, 0f), (saved.Brightness, saved.Contrast, saved.ColorSaturation));
            Assert.NotEqual(before, (await media.GetImage())!.Data);

            await Assert.ThrowsAnyAsync<Exception>(() => imaging.SetImagingSettingsAsync("VideoSource_1", settings with { Contrast = 150 }));
        }
        finally
        {
            Store.Update(w => w.Cameras.Single(c => c.Id == "cam1").Imaging = new ImagingModel());
        }
    }

    private WorldStore Store => factory.Services.GetRequiredService<WorldStore>();

    private static async Task<PtzService2> PtzAsync(Camera camera) =>
        await camera.GetPtzService() ?? throw new InvalidOperationException("no PTZ service");

    private static async Task<OnvifLib.PtzStatus> WaitUntilIdleAsync(PtzService2 ptz)
    {
        for (int i = 0; i < 50; i++)
        {
            var status = (await ptz.GetStatusAsync("Profile_1"))!;
            if (!status.Moving) return status;
            await Task.Delay(100);
        }
        throw new TimeoutException("The PTZ head kept moving.");
    }

    // Back to the default world's head: pan 0, tilt -10, wide, no presets.
    private void ResetCam2() => Store.Update(w =>
    {
        var ptz = w.Cameras.Single(c => c.Id == "cam2").Ptz!;
        ptz.Position = new PtzPosition(0, -10, 0);
        ptz.Presets.Clear();
    });

    private async Task<HttpResponseMessage> PostAsync(
        string cameraId, string envelope, string contentType = "application/soap+xml; charset=utf-8", string path = "device_service")
    {
        using var client = new HttpClient();
        var content = new StringContent(envelope, Encoding.UTF8);
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        return await client.PostAsync($"http://127.0.0.1:{PortOf(cameraId)}/onvif/{path}", content);
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
