using Microsoft.Extensions.DependencyInjection;
using OnvifLib;
using OnvifSim.Onvif;

namespace OnvifSim.Tests;

// The live state of a camera's relays and inputs as OnvifLib collects it from Device/Trigger
// property events, against the Maryland world: subscribing replays Initialized, and every switch
// after arrives as Changed, including on the other camera an input is wired to.
public class DeviceIoStateWatcherOnvifTests(MarylandFactory factory) : IClassFixture<MarylandFactory>
{
    private int PortOf(string cameraId) => factory.Services.GetRequiredService<OnvifHost>().PortOf(cameraId)
        ?? throw new InvalidOperationException($"{cameraId} is not served");

    private Camera Connect(string cameraId) => Camera.Create("127.0.0.1", PortOf(cameraId), "admin", "admin", timeout: 10);

    private async Task<DeviceIoStateWatcher> WatchAsync(string cameraId)
    {
        var events = await Connect(cameraId).GetEventService()
            ?? throw new InvalidOperationException($"{cameraId} has no event service");
        // The default long poll would hold every switch back for five seconds; a test needs it sooner.
        events.PullTimeout = TimeSpan.FromMilliseconds(250);
        return await DeviceIoStateWatcher.CreateAsync(events);
    }

    private static async Task<DeviceIoState> WaitForAsync(DeviceIoStateWatcher watcher,
        DeviceIoKind kind, string token, Func<DeviceIoState, bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (watcher.TryGetState(kind, token, out var state) && condition(state))
                return state;
            await Task.Delay(50);
        }
        Assert.Fail($"No {kind} state for '{token}' matching the condition arrived within 10 s");
        return null!;
    }

    [Fact]
    public async Task Subscribing_ReplaysEveryRelayAndEveryWiredInput()
    {
        await using var watcher = await WatchAsync("master");

        // The master has three relays and a sensor of its own; the sensor is wired to one of them.
        foreach (var token in new[] { "lamp", "peer", "loop" })
        {
            var relay = await WaitForAsync(watcher, DeviceIoKind.Relay, token,
                state => state.PropertyOperation == "Initialized");
            Assert.False(relay.LogicalState);
            // All three are normally open, so an energised-none relay has an open contact.
            Assert.False(relay.ContactClosed);
        }

        var input = await WaitForAsync(watcher, DeviceIoKind.DigitalInput, "input",
            state => state.PropertyOperation == "Initialized");
        // The input's idle state is open, and nothing drives its circuit closed: it is idle.
        Assert.False(input.LogicalState);
        // An input has no contact of its own.
        Assert.Null(input.ContactClosed);

        Assert.False(watcher.TryGetState(DeviceIoKind.Relay, "nobody", out _));
    }

    [Fact]
    public async Task SwitchingARelay_ReportsChanged_AndItsWireChangesTheOtherCamera()
    {
        await using var masterWatcher = await WatchAsync("master");
        await using var slaveWatcher = await WatchAsync("ptz");
        await WaitForAsync(masterWatcher, DeviceIoKind.Relay, "peer", state => state.PropertyOperation == "Initialized");
        var sensorBefore = await WaitForAsync(slaveWatcher, DeviceIoKind.DigitalInput, "input",
            state => state.PropertyOperation == "Initialized");
        Assert.False(sensorBefore.LogicalState);

        var master = Connect("master");
        try
        {
            // "peer" drives the slave camera's sensor over a wire: closing its contact closes the
            // input's circuit, which is active while the idle state is open.
            await master.SetRelayOutputStateAsync("peer", true);

            var relay = await WaitForAsync(masterWatcher, DeviceIoKind.Relay, "peer",
                state => state.PropertyOperation == "Changed" && state.LogicalState);
            Assert.True(relay.ContactClosed);

            var sensor = await WaitForAsync(slaveWatcher, DeviceIoKind.DigitalInput, "input",
                state => state.PropertyOperation == "Changed" && state.LogicalState);
            Assert.Null(sensor.ContactClosed);
        }
        finally
        {
            await master.SetRelayOutputStateAsync("peer", false);
        }
    }
}
