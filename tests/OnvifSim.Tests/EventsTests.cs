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

public class EventsTests
{
    private const string TopicsNs = "http://www.onvif.org/ver10/topics";

    [Fact]
    public void TopicFilter_LetsThroughWhatTheExpressionsName()
    {
        Assert.True(TopicFilter.Of(null)(MotionEvents.CellMotionTopic));

        var exact = TopicFilter.Of(Filter($"x:{MotionEvents.CellMotionTopic}", ("x", TopicsNs)));
        Assert.True(exact(MotionEvents.CellMotionTopic));
        Assert.False(exact(MotionEvents.MotionAlarmTopic));

        var either = TopicFilter.Of(Filter("tns1:VideoSource//.|tns1:RuleEngine/LineDetector/Crossed", ("tns1", TopicsNs)));
        Assert.True(either(MotionEvents.MotionAlarmTopic));
        Assert.False(either(MotionEvents.CellMotionTopic));

        // Same path, someone else's topic namespace.
        Assert.False(TopicFilter.Of(Filter($"v:{MotionEvents.CellMotionTopic}", ("v", "urn:vendor")))(MotionEvents.CellMotionTopic));
    }

    // The analytics look every 200 ms of the fake clock; the test moves the clock and waits (for real)
    // until the pull point has seen the looks.
    [Fact]
    public async Task PullPoint_SaysMotionWhileSomeoneIsInView_AndHoldsItASecond()
    {
        using var dir = new TempDir();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(20000));
        var store = new WorldStore(dir.Path);
        store.Update(w => (w.Objects, w.Walkers) = ([], [new WalkerModel { Id = "p", Route = [new GroundPoint(3, 40)] }]));
        var scene = new SceneState(store);
        using var ptz = new PtzSimulator(store, time, NullLogger<PtzSimulator>.Instance);
        using var pullPoints = new PullPoints(new AnalyticsManager(scene, ptz, time, NullLoggerFactory.Instance), time, NullLogger<PullPoints>.Instance);

        var subscription = pullPoints.Create("cam1", null, TimeSpan.FromMinutes(1));
        Assert.Equal([("Initialized", "false"), ("Initialized", "false")], Read(await PullAsync(subscription, 2)));
        Assert.Equal([("Changed", "true"), ("Changed", "true")], Read(await PullAsync(subscription, 2)));

        store.Update(w => w.Walkers = []);
        for (int i = 0; i < 4; i++) await StepAsync(time);
        Assert.Empty(await subscription.PullAsync(TimeSpan.Zero, 10, CancellationToken.None));
        for (int i = 0; i < 2; i++) await StepAsync(time);
        Assert.Equal([("Changed", "false"), ("Changed", "false")], Read(await PullAsync(subscription, 2)));

        subscription.Synchronize();
        Assert.Equal([("Initialized", "false"), ("Initialized", "false")], Read(await PullAsync(subscription, 2)));
        pullPoints.Remove(subscription);
    }

    private static FilterType Filter(string expression, (string Prefix, string Namespace) declared)
    {
        var document = new XmlDocument();
        var element = document.CreateElement("wsnt", "TopicExpression", "http://docs.oasis-open.org/wsn/b-2");
        element.SetAttribute($"xmlns:{declared.Prefix}", declared.Namespace);
        element.SetAttribute("Dialect", MotionEvents.ConcreteSetDialect);
        element.InnerText = expression;
        return new FilterType { Any = [element] };
    }

    private static async Task StepAsync(FakeTimeProvider time)
    {
        time.Advance(CameraAnalytics.Interval);
        await Task.Delay(50);
    }

    private static async Task<NotificationMessageHolderType[]> PullAsync(PullPoint subscription, int count)
    {
        var pulled = new List<NotificationMessageHolderType>();
        for (int i = 0; i < 100 && pulled.Count < count; i++)
        {
            pulled.AddRange(await subscription.PullAsync(TimeSpan.Zero, count - pulled.Count, CancellationToken.None));
            if (pulled.Count < count) await Task.Delay(20);
        }
        return [.. pulled];
    }

    // PropertyOperation and the one data value of each notification.
    private static (string, string)[] Read(NotificationMessageHolderType[] notifications) =>
        notifications.Select(n => (n.Message.GetAttribute("PropertyOperation"),
            n.Message.GetElementsByTagName("SimpleItem", "http://www.onvif.org/ver10/schema").Cast<XmlElement>().Last().GetAttribute("Value"))).ToArray();
}

// The event service as a client sees it, over HTTP: pull points created, pulled, renewed and ended.
public class EventServiceTests(SimFactory factory) : IClassFixture<SimFactory>
{
    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Tev = "http://www.onvif.org/ver10/events/wsdl";
    private static readonly XNamespace Wsnt = "http://docs.oasis-open.org/wsn/b-2";
    private static readonly XNamespace Wsa = "http://www.w3.org/2005/08/addressing";
    private static readonly XNamespace Tds = "http://www.onvif.org/ver10/device/wsdl";

    [Fact]
    public async Task PullPoint_IsCreatedPulledRenewedAndEnded()
    {
        var created = await PostAsync($"http://127.0.0.1:{Port}/onvif/event_service",
            $"<CreatePullPointSubscription xmlns=\"{Tev}\"><InitialTerminationTime>PT20S</InitialTerminationTime></CreatePullPointSubscription>");
        var address = created.Descendants(Wsa + "Address").Single().Value;
        Assert.Matches($"^http://127.0.0.1:{Port}/onvif/event_service/subscription/[0-9a-f]+$", address);

        var pulled = await PostAsync(address, $"<PullMessages xmlns=\"{Tev}\"><Timeout>PT1S</Timeout><MessageLimit>10</MessageLimit></PullMessages>");
        var topics = pulled.Descendants(Wsnt + "Topic").Select(t => t.Value).ToList();
        Assert.Contains("tns1:VideoSource/MotionAlarm", topics);
        Assert.Contains("tns1:RuleEngine/CellMotionDetector/Motion", topics);
        // The topic's prefix is declared where the topic is read.
        var topic = pulled.Descendants(Wsnt + "Topic").First();
        Assert.Equal("http://www.onvif.org/ver10/topics", topic.GetNamespaceOfPrefix("tns1")?.NamespaceName);

        var renewed = await PostAsync(address, $"<wsnt:Renew xmlns:wsnt=\"{Wsnt}\"><wsnt:TerminationTime>PT40S</wsnt:TerminationTime></wsnt:Renew>");
        var until = System.DateTime.Parse(renewed.Descendants(Wsnt + "TerminationTime").Single().Value).ToUniversalTime();
        var now = System.DateTime.Parse(renewed.Descendants(Wsnt + "CurrentTime").Single().Value).ToUniversalTime();
        Assert.InRange((until - now).TotalSeconds, 39, 41);

        await PostAsync(address, $"<wsnt:Unsubscribe xmlns:wsnt=\"{Wsnt}\"/>");
        var gone = await SendAsync(address, $"<PullMessages xmlns=\"{Tev}\"><Timeout>PT1S</Timeout><MessageLimit>10</MessageLimit></PullMessages>");
        Assert.Equal(HttpStatusCode.BadRequest, gone.StatusCode);
        Assert.Contains("ResourceUnknown", await gone.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EventService_IsListedOnce_AndInTheCapabilities()
    {
        var services = await PostAsync($"http://127.0.0.1:{Port}/onvif/device_service",
            $"<GetServices xmlns=\"{Tds}\"><IncludeCapability>false</IncludeCapability></GetServices>");
        Assert.Single(services.Descendants(Tds + "Namespace"), n => n.Value == Tev.NamespaceName);
        Assert.DoesNotContain(services.Descendants(Tds + "Namespace"), n => n.Value.StartsWith("http://docs.oasis-open.org"));

        var capabilities = await PostAsync($"http://127.0.0.1:{Port}/onvif/device_service",
            $"<GetCapabilities xmlns=\"{Tds}\"><Category>Events</Category></GetCapabilities>");
        XNamespace tt = "http://www.onvif.org/ver10/schema";
        var events = capabilities.Descendants(tt + "Events").Single();
        Assert.Equal($"http://127.0.0.1:{Port}/onvif/event_service", events.Element(tt + "XAddr")!.Value);
        Assert.Equal("true", events.Element(tt + "WSPullPointSupport")!.Value);
    }

    private int Port => factory.Services.GetRequiredService<OnvifHost>().PortOf("cam1")!.Value;

    private static async Task<XElement> PostAsync(string url, string body)
    {
        var response = await SendAsync(url, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!.Element(Soap + "Body")!;
    }

    private static async Task<HttpResponseMessage> SendAsync(string url, string body)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:admin")));
        var content = new StringContent($"<s:Envelope xmlns:s=\"{Soap}\"><s:Body>{body}</s:Body></s:Envelope>", Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/soap+xml; charset=utf-8");
        return await client.PostAsync(url, content);
    }
}
