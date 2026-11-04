using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OnvifSim.Analytics;
using OnvifSim.Onvif;
using OnvifSim.Onvif.Events;
using OnvifSim.Onvif.Wsdl;
using OnvifSim.Ptz;
using OnvifSim.World;

namespace OnvifSim.Tests;

// Relay outputs and digital inputs as ONVIF property events: the current state on subscribing, and every
// change after, including on the camera whose input a relay elsewhere drives over a wire.
public class DeviceEventsTests
{
    private const string Schema = "http://www.onvif.org/ver10/schema";

    [Fact]
    public async Task Initialized_ReportsEachRelayAndEachWiredInput()
    {
        using var rig = new Rig(WithDevices);
        var relayPoint = rig.Points.Create("cam1", DevicesOnly, TimeSpan.FromMinutes(1));
        var inputPoint = rig.Points.Create("cam2", DevicesOnly, TimeSpan.FromMinutes(1));

        Assert.Equal([("Device/Trigger/Relay", "lamp", "inactive", "Initialized")], Read(await PullAsync(relayPoint, 1)));
        // An open relay leaves the input pulled up, so it reads true.
        Assert.Equal([("Device/Trigger/DigitalInput", "sensor", "true", "Initialized")], Read(await PullAsync(inputPoint, 1)));
    }

    [Fact]
    public async Task AnUnwiredInput_HasNoStateToReport()
    {
        using var rig = new Rig(world =>
        {
            Camera(world, "cam1").DigitalInputs.Add(new DigitalInputModel { Token = "loose" });
        });
        var point = rig.Points.Create("cam1", DevicesOnly, TimeSpan.FromMinutes(1));
        Assert.Empty(await point.PullAsync(TimeSpan.Zero, 10, CancellationToken.None));
    }

    [Fact]
    public async Task SwitchingARelay_ChangesItOnItsCamera_AndTheInputItDrivesElsewhere()
    {
        using var rig = new Rig(WithDevices);
        var relayPoint = rig.Points.Create("cam1", DevicesOnly, TimeSpan.FromMinutes(1));
        var inputPoint = rig.Points.Create("cam2", DevicesOnly, TimeSpan.FromMinutes(1));
        await PullAsync(relayPoint, 1);
        await PullAsync(inputPoint, 1);

        Assert.True(rig.Switches.SetRelay("cam1", "lamp", true));

        Assert.Equal([("Device/Trigger/Relay", "lamp", "active", "Changed")], Read(await PullAsync(relayPoint, 1)));
        // A closed contact shunts the input to zero.
        Assert.Equal([("Device/Trigger/DigitalInput", "sensor", "false", "Changed")], Read(await PullAsync(inputPoint, 1)));
    }

    [Fact]
    public async Task Synchronize_RepeatsTheCurrentDeviceState()
    {
        using var rig = new Rig(WithDevices);
        var point = rig.Points.Create("cam1", DevicesOnly, TimeSpan.FromMinutes(1));
        await PullAsync(point, 1);
        rig.Switches.SetRelay("cam1", "lamp", true);
        await PullAsync(point, 1);

        point.Synchronize();

        Assert.Equal([("Device/Trigger/Relay", "lamp", "active", "Initialized")], Read(await PullAsync(point, 1)));
    }

    [Fact]
    public async Task ATopicFilter_LetsOnlyTheTopicsItNamesThrough()
    {
        using var rig = new Rig(world =>
        {
            WithDevices(world);
            // The same camera drives an input of its own, so both topics have something to say.
            Camera(world, "cam1").DigitalInputs.Add(new DigitalInputModel { Token = "local" });
            world.Wires.Add(new WireModel { RelayCameraId = "cam1", RelayToken = "lamp", InputCameraId = "cam1", InputToken = "local" });
        });
        var relayOnly = rig.Points.Create("cam1", EventsTests.Filter("tns1:Device/Trigger/Relay", ("tns1", MotionEvents.TopicsNamespace)), TimeSpan.FromMinutes(1));

        Assert.Equal([("Device/Trigger/Relay", "lamp", "inactive", "Initialized")], Read(await PullAsync(relayOnly, 1)));
        Assert.Empty(await relayOnly.PullAsync(TimeSpan.Zero, 10, CancellationToken.None));
    }

    private static void WithDevices(WorldModel world)
    {
        Camera(world, "cam1").RelayOutputs.Add(new RelayOutputModel { Token = "lamp", IdleState = IdleStateModel.Open });
        Camera(world, "cam2").DigitalInputs.Add(new DigitalInputModel { Token = "sensor" });
        world.Wires.Add(new WireModel { RelayCameraId = "cam1", RelayToken = "lamp", InputCameraId = "cam2", InputToken = "sensor" });
    }

    private static CameraModel Camera(WorldModel world, string id) => world.Cameras.Single(c => c.Id == id);

    private static readonly FilterType DevicesOnly =
        EventsTests.Filter("tns1:Device//.", ("tns1", MotionEvents.TopicsNamespace));

    private static async Task<NotificationMessageHolderType[]> PullAsync(PullPoint point, int count)
    {
        var pulled = new List<NotificationMessageHolderType>();
        for (int i = 0; i < 100 && pulled.Count < count; i++)
        {
            pulled.AddRange(await point.PullAsync(TimeSpan.Zero, count - pulled.Count, CancellationToken.None));
            if (pulled.Count < count) await Task.Delay(20);
        }
        return [.. pulled];
    }

    // Topic, the device's token, its logical state and how the notification relates to the property.
    private static (string, string, string, string)[] Read(NotificationMessageHolderType[] notifications) =>
        notifications.Select(notification =>
        {
            var items = notification.Message.GetElementsByTagName("SimpleItem", Schema).Cast<XmlElement>().ToList();
            string Token() => items.First(item => item.GetAttribute("Name") is "RelayToken" or "InputToken").GetAttribute("Value");
            return (
                ((XmlText)notification.Topic!.Any![0]!).Value.Replace("tns1:", ""),
                Token(),
                items.Single(item => item.GetAttribute("Name") == "LogicalState").GetAttribute("Value"),
                notification.Message.GetAttribute("PropertyOperation"));
        }).ToArray();

    private sealed class Rig : IDisposable
    {
        public Rig(Action<WorldModel>? setup = null)
        {
            Store = new WorldStore(Directory.Path);
            // The default world brings a lamp and a sensor of its own; each test says which devices
            // it means, so the default ones are not in the way of its pull points.
            Store.Update(world =>
            {
                foreach (var camera in world.Cameras)
                {
                    camera.RelayOutputs.Clear();
                    camera.DigitalInputs.Clear();
                    camera.Lamps.Clear();
                }
                world.Wires.Clear();
            });
            if (setup != null) Store.Update(setup);
            var scene = new SceneState(Store);
            Clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(20000));
            Ptz = new PtzSimulator(Store, Clock, NullLogger<PtzSimulator>.Instance);
            Switches = new DeviceIoController(Store, Clock);
            Points = new PullPoints(
                new AnalyticsManager(scene, Ptz, Clock, NullLoggerFactory.Instance), Store, Clock, NullLogger<PullPoints>.Instance);
        }

        public TempDir Directory { get; } = new();
        public WorldStore Store { get; }
        public FakeTimeProvider Clock { get; }
        public PtzSimulator Ptz { get; }
        public DeviceIoController Switches { get; }
        public PullPoints Points { get; }

        public void Dispose()
        {
            Points.Dispose();
            Switches.Dispose();
            Ptz.Dispose();
            Directory.Dispose();
        }
    }
}

// The same events over HTTP, as a client creates a pull point and reads them.
public class DeviceEventServiceTests(SimFactory factory) : IClassFixture<SimFactory>
{
    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Tev = "http://www.onvif.org/ver10/events/wsdl";
    private static readonly XNamespace Wsnt = "http://docs.oasis-open.org/wsn/b-2";
    private static readonly XNamespace Wsa = "http://www.w3.org/2005/08/addressing";
    private static readonly XNamespace Tt = "http://www.onvif.org/ver10/schema";

    [Fact]
    public async Task APullPoint_ReportsEveryDeviceWithItsState()
    {
        factory.Services.GetRequiredService<WorldStore>().Update(world =>
        {
            var camera = world.Cameras.Single(c => c.Id == "cam1");
            camera.RelayOutputs = [new RelayOutputModel { Token = "lamp", IdleState = IdleStateModel.Closed, Active = true }];
            camera.DigitalInputs = [new DigitalInputModel { Token = "sensor" }];
            world.Wires.RemoveAll(w => w.InputCameraId == "cam1");
            world.Wires.Add(new WireModel { RelayCameraId = "cam1", RelayToken = "lamp", InputCameraId = "cam1", InputToken = "sensor" });
        });

        var created = await PostAsync($"http://127.0.0.1:{Port}/onvif/event_service",
            $"<CreatePullPointSubscription xmlns=\"{Tev}\"><Filter><wsnt:TopicExpression xmlns:wsnt=\"{Wsnt}\" Dialect=\"{MotionEvents.ConcreteSetDialect}\" xmlns:tns1=\"{MotionEvents.TopicsNamespace}\">tns1:Device//.</wsnt:TopicExpression></Filter><InitialTerminationTime>PT20S</InitialTerminationTime></CreatePullPointSubscription>");
        var address = created.Descendants(Wsa + "Address").Single().Value;

        var pulled = await PostAsync(address, $"<PullMessages xmlns=\"{Tev}\"><Timeout>PT1S</Timeout><MessageLimit>10</MessageLimit></PullMessages>");
        var topics = pulled.Descendants(Wsnt + "Topic").Select(t => t.Value).OrderBy(t => t).ToList();
        Assert.Equal(["tns1:Device/Trigger/DigitalInput", "tns1:Device/Trigger/Relay"], topics);
        var messages = pulled.Descendants(Tt + "Message").ToList();
        Assert.All(messages, message => Assert.Equal("Initialized", message.Attribute("PropertyOperation")?.Value));
        Assert.Contains(messages, message => Item(message, "RelayToken") == "lamp" && Item(message, "LogicalState") == "active");
        // The relay is closed, so the input it drives reads zero.
        Assert.Contains(messages, message => Item(message, "InputToken") == "sensor" && Item(message, "LogicalState") == "false");
    }

    private static string? Item(XElement message, string name) => message
        .Descendants(Tt + "SimpleItem").FirstOrDefault(item => item.Attribute("Name")?.Value == name)?.Attribute("Value")?.Value;

    private int Port => factory.Services.GetRequiredService<OnvifHost>().PortOf("cam1")!.Value;

    private static async Task<XElement> PostAsync(string url, string body)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:admin")));
        var content = new StringContent($"<s:Envelope xmlns:s=\"{Soap}\"><s:Body>{body}</s:Body></s:Envelope>", Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/soap+xml; charset=utf-8");
        var response = await client.PostAsync(url, content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!.Element(Soap + "Body")!;
    }
}
