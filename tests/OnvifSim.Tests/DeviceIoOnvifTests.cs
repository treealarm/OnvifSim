using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using OnvifLib;
using OnvifSim.Onvif;
using OnvifSim.World;

namespace OnvifSim.Tests;

// The camera's relay outputs and digital inputs as an ONVIF client sees them: the Device service for
// relays and the DeviceIO service for inputs and options, through OnvifLib, the library it exists to
// test.
public class DeviceIoOnvifTests(SimFactory factory) : IClassFixture<SimFactory>
{
    private const string DeviceIoNamespace = "http://www.onvif.org/ver10/deviceIO/wsdl";

    private int PortOf(string cameraId) => factory.Services.GetRequiredService<OnvifHost>().PortOf(cameraId)
        ?? throw new InvalidOperationException($"{cameraId} is not served");

    private Camera Connect(string cameraId) => Camera.Create("127.0.0.1", PortOf(cameraId), "admin", "admin", timeout: 10);

    private RelayOutputModel Relay(string cameraId) =>
        factory.Services.GetRequiredService<WorldStore>().Current.Cameras.Single(c => c.Id == cameraId).RelayOutputs.Single();

    // A relay and an input on the camera, set in the world as the editor would leave them.
    private void PutDevices(string cameraId, RelayModeModel mode = RelayModeModel.Bistable, IdleStateModel idle = IdleStateModel.Open)
    {
        factory.Services.GetRequiredService<WorldStore>().Update(world =>
        {
            var camera = world.Cameras.Single(c => c.Id == cameraId);
            camera.RelayOutputs =
            [
                new RelayOutputModel { Token = "lamp", Name = "Gate lamp", Mode = mode, DelaySeconds = 5, IdleState = idle },
            ];
            camera.DigitalInputs = [new DigitalInputModel { Token = "sensor", Name = "Door sensor", IdleState = IdleStateModel.Open }];
        });
    }

    [Fact]
    public async Task RelayOutputs_AreReadWithTheirSettings()
    {
        PutDevices("cam1", RelayModeModel.Monostable, IdleStateModel.Closed);
        var relay = Assert.Single(await Connect("cam1").GetRelayOutputsAsync());
        Assert.Equal(("lamp", "monostable", "closed", 5000), (relay.Token, relay.Mode, relay.IdleState, relay.DelayMs));
    }

    // ONVIF's RelayOutput carries settings only; the simulator adds the live logical and physical states
    // as extensions in its own namespace, so a client reading the outputs sees both.
    [Fact]
    public async Task RelayOutputs_CarryBothLiveStatesAsExtensions()
    {
        PutDevices("cam1", RelayModeModel.Bistable, IdleStateModel.Closed);
        factory.Services.GetRequiredService<WorldStore>().Update(world =>
            world.Cameras.Single(c => c.Id == "cam1").RelayOutputs[0].Active = true);

        using var http = AuthClient();
        var response = await http.PostAsync($"http://127.0.0.1:{PortOf("cam1")}/onvif/device_service", Soap(
            "<GetRelayOutputs xmlns=\"http://www.onvif.org/ver10/device/wsdl\"/>"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        // ONVIF names the elements of the response RelayOutputs, though their type is RelayOutput.
        XNamespace ons = "urn:onvifsim";
        var relay = XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!
            .Descendants().Single(element => element.Name.LocalName == "RelayOutputs");
        // Energised and normally closed: active, and the contact is open.
        Assert.Equal("active", relay.Element(ons + "LogicalState")!.Value);
        Assert.Equal("open", relay.Element(ons + "PhysicalState")!.Value);
    }

    [Fact]
    public async Task A_SwitchThroughTheDeviceService_ChangesTheWorld()
    {
        PutDevices("cam1");
        var camera = Connect("cam1");

        await camera.SetRelayOutputStateAsync("lamp", true);
        Assert.True(Relay("cam1").Active);
        await camera.SetRelayOutputStateAsync("lamp", false);
        Assert.False(Relay("cam1").Active);
    }

    [Fact]
    public async Task RelaySettings_AreSavedByTheDeviceService()
    {
        PutDevices("cam1");
        await Connect("cam1").SetRelayOutputSettingsAsync("lamp", "monostable", "closed", 3000);
        var relay = Relay("cam1");
        Assert.Equal(RelayModeModel.Monostable, relay.Mode);
        Assert.Equal(IdleStateModel.Closed, relay.IdleState);
        Assert.Equal(3, relay.DelaySeconds, 2);
    }

    [Fact]
    public async Task AnUnknownRelay_IsRefused()
    {
        PutDevices("cam1");
        var camera = Connect("cam1");
        await Assert.ThrowsAnyAsync<Exception>(() => camera.SetRelayOutputStateAsync("nobody", true));
        await Assert.ThrowsAnyAsync<Exception>(() => camera.SetRelayOutputSettingsAsync("nobody", "bistable", "open", 1000));
    }

    [Fact]
    public async Task DigitalInputs_AreReadByTheDeviceIoService()
    {
        PutDevices("cam1");
        var io = await Connect("cam1").GetDeviceIOService();
        Assert.NotNull(io);
        var input = Assert.Single(await io.GetDigitalInputsAsync());
        Assert.Equal(("sensor", "open"), (input.Token, input.IdleState));
    }

    [Fact]
    public async Task RelayOutputOptions_OfferBothModes()
    {
        PutDevices("cam1");
        var io = await Connect("cam1").GetDeviceIOService();
        var options = Assert.Single(await io!.GetRelayOutputOptionsAsync());
        Assert.Equal("lamp", options.Token);
        Assert.Contains("bistable", options.SupportedModes);
        Assert.Contains("monostable", options.SupportedModes);
        Assert.InRange(options.MinDelayMs, 1, int.MaxValue);
        Assert.True(options.MaxDelayMs >= options.MinDelayMs);
    }

    // tt:FloatList is seconds, not xs:duration. OnvifLib parses both, so only a raw read pins the
    // wire format.
    [Fact]
    public async Task RelayOutputOptions_SendDelayTimesAsASecondsList()
    {
        PutDevices("cam1");
        using var http = AuthClient();
        var response = await http.PostAsync($"http://127.0.0.1:{PortOf("cam1")}/onvif/deviceio_service", Soap(
            "<GetRelayOutputOptions xmlns=\"http://www.onvif.org/ver10/deviceIO/wsdl\"/>"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var delayTimes = XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!
            .Descendants().Single(element => element.Name.LocalName == "DelayTimes").Value;
        Assert.Equal("0.5 1 2 5 10 30 60", delayTimes);
    }

    [Fact]
    public async Task EventProperties_DescribeTheDeviceTriggerTypes()
    {
        PutDevices("cam1");
        using var http = AuthClient();
        var response = await http.PostAsync($"http://127.0.0.1:{PortOf("cam1")}/onvif/event_service", Soap(
            "<GetEventProperties xmlns=\"http://www.onvif.org/ver10/events/wsdl\"/>"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        XNamespace tt = "http://www.onvif.org/ver10/schema";
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        static XElement Leaf(XDocument document, string name) => document.Descendants()
            .Single(element => element.Name.LocalName == name
                && element.Descendants().Any(child => child.Name.LocalName == "MessageDescription"));
        static string? TypeOf(XElement leaf, string item) => (string?)leaf
            .Descendants(XName.Get("SimpleItemDescription", "http://www.onvif.org/ver10/schema"))
            .Single(element => (string?)element.Attribute("Name") == item)
            .Attribute("Type");

        Assert.Equal("tt:RelayLogicalState", TypeOf(Leaf(document, "Relay"), "LogicalState"));
        Assert.Equal("xs:boolean", TypeOf(Leaf(document, "DigitalInput"), "LogicalState"));
    }

    [Fact]
    public async Task DigitalInputOptions_WithoutAToken_AreTheDevicesGenericOnes()
    {
        PutDevices("cam1");
        using var http = AuthClient();
        var response = await http.PostAsync($"http://127.0.0.1:{PortOf("cam1")}/onvif/deviceio_service", Soap(
            "<GetDigitalInputConfigurationOptions xmlns=\"http://www.onvif.org/ver10/deviceIO/wsdl\"/>"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Contains("closed", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SetRelayOutputState_OnAnUnknownRelay_CarriesTheRelayTokenSubcode()
    {
        PutDevices("cam1");
        using var http = AuthClient();
        var response = await http.PostAsync($"http://127.0.0.1:{PortOf("cam1")}/onvif/device_service", Soap(
            "<SetRelayOutputState xmlns=\"http://www.onvif.org/ver10/device/wsdl\">"
            + "<RelayOutputToken>nobody</RelayOutputToken><LogicalState>active</LogicalState>"
            + "</SetRelayOutputState>"));
        Assert.Contains("ter:RelayToken", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AMonostableDelayOutsideItsRange_CarriesTheModeErrorSubcode()
    {
        PutDevices("cam1");
        using var http = AuthClient();
        var response = await http.PostAsync($"http://127.0.0.1:{PortOf("cam1")}/onvif/device_service", Soap(
            "<SetRelayOutputSettings xmlns=\"http://www.onvif.org/ver10/device/wsdl\""
            + " xmlns:tt=\"http://www.onvif.org/ver10/schema\">"
            + "<RelayOutputToken>lamp</RelayOutputToken><Properties>"
            + "<tt:Mode>Monostable</tt:Mode><tt:DelayTime>PT0S</tt:DelayTime><tt:IdleState>open</tt:IdleState>"
            + "</Properties></SetRelayOutputSettings>"));
        Assert.Contains("ter:ModeError", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ABistableRelay_IgnoresAMalformedDelayTime()
    {
        PutDevices("cam1");
        using var http = AuthClient();
        var response = await http.PostAsync($"http://127.0.0.1:{PortOf("cam1")}/onvif/device_service", Soap(
            "<SetRelayOutputSettings xmlns=\"http://www.onvif.org/ver10/device/wsdl\""
            + " xmlns:tt=\"http://www.onvif.org/ver10/schema\">"
            + "<RelayOutputToken>lamp</RelayOutputToken><Properties>"
            + "<tt:Mode>Bistable</tt:Mode><tt:DelayTime>not-a-duration</tt:DelayTime><tt:IdleState>open</tt:IdleState>"
            + "</Properties></SetRelayOutputSettings>"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(RelayModeModel.Bistable, Relay("cam1").Mode);
    }

    [Fact]
    public async Task DigitalInputOptions_NameTheInput_AndAnUnknownOneIsRefused()
    {
        PutDevices("cam1");
        var io = await Connect("cam1").GetDeviceIOService();
        var options = await io!.GetDigitalInputConfigurationOptionsAsync("sensor");
        Assert.NotNull(options);
        Assert.True(options.IdleStateConfigurable);
        await Assert.ThrowsAnyAsync<Exception>(() => io.GetDigitalInputConfigurationOptionsAsync("nobody"));
    }

    [Fact]
    public async Task TheDeviceIoService_IsListedOnTheCamerasOwnPort()
    {
        var services = await Connect("cam1").GetServicesAsync();
        Assert.NotNull(services);
        Assert.Equal($"http://127.0.0.1:{PortOf("cam1")}/onvif/deviceio_service", services[DeviceIoNamespace]);
    }

    [Fact]
    public async Task TheDeviceCapabilities_CountTheRelaysAndInputs()
    {
        PutDevices("cam1");
        using var http = new HttpClient();
        var response = await http.PostAsync($"http://127.0.0.1:{PortOf("cam1")}/onvif/device_service", Soap(
            "<GetCapabilities xmlns=\"http://www.onvif.org/ver10/device/wsdl\"><Category>Device</Category></GetCapabilities>"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        XNamespace tt = "http://www.onvif.org/ver10/schema";
        var capabilities = XDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("1", capabilities.Descendants(tt + "RelayOutputs").Single().Value);
        Assert.Equal("1", capabilities.Descendants(tt + "InputConnectors").Single().Value);
    }

    // The camera's ONVIF port is its own listener, not the editor's; these tests post SOAP by hand
    // where OnvifLib's typed wrappers hide the wire (the delay list, the topic-set types, a fault's
    // subcode). GetCapabilities is pre-auth; the rest need the camera's user.
    private static HttpContent Soap(string body) => new StringContent(
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?><s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body>{body}</s:Body></s:Envelope>",
        System.Text.Encoding.UTF8, "application/soap+xml");

    private static HttpClient AuthClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:admin")));
        return http;
    }
}
