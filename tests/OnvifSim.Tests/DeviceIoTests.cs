using System.Net;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using OnvifSim.Editor;
using OnvifSim.Geometry;
using OnvifSim.Rendering;
using OnvifSim.World;

namespace OnvifSim.Tests;

// Relay outputs, digital inputs and the wires between them: the model's rules, the one place their state
// changes, and what the editor's API and the scene make of them.
public class DeviceIoTests(SimFactory factory) : IClassFixture<SimFactory>
{
    private WorldStore Store => factory.Services.GetRequiredService<WorldStore>();

    [Fact]
    public void RelaysInputsAndWires_RoundTripThroughTheWorldFile()
    {
        var world = WorldWithDevices();
        var json = JsonSerializer.Serialize(world, WorldStore.Json);
        var read = JsonSerializer.Deserialize<WorldModel>(json, WorldStore.Json)!;

        var relay = Assert.Single(read.Cameras.Single(c => c.Id == "cam1").RelayOutputs);
        Assert.Equal("lamp", relay.Token);
        Assert.Equal(RelayModeModel.Monostable, relay.Mode);
        Assert.Equal(2.5f, relay.DelaySeconds);
        Assert.Equal(IdleStateModel.Closed, relay.IdleState);
        Assert.True(relay.Active);
        var input = Assert.Single(read.Cameras.Single(c => c.Id == "cam2").DigitalInputs);
        Assert.Equal("sensor", input.Token);
        var wire = Assert.Single(read.Wires);
        Assert.Equal(("cam1", "lamp", "cam2", "sensor"), (wire.RelayCameraId, wire.RelayToken, wire.InputCameraId, wire.InputToken));
        Assert.Null(wire.LampId);
    }

    [Fact]
    public void ALampAndItsWire_RoundTripThroughTheWorldFile()
    {
        var world = WorldWithDevices();
        world.Lamps.Add(new LampModel { Id = "warning", Name = "Warning lamp", NormallyClosed = true, Position = new Vec3(2, 0, 1) });
        world.Wires.Add(new WireModel { RelayCameraId = "cam1", RelayToken = "lamp", LampId = "warning" });
        var read = JsonSerializer.Deserialize<WorldModel>(JsonSerializer.Serialize(world, WorldStore.Json), WorldStore.Json)!;

        var lamp = Assert.Single(read.Lamps);
        Assert.Equal(("warning", "Warning lamp", true), (lamp.Id, lamp.Name, lamp.NormallyClosed));
        var wire = read.Wires.Single(w => w.LampId == "warning");
        Assert.Equal(("cam1", "lamp", "warning"), (wire.RelayCameraId, wire.RelayToken, wire.LampId));
        Assert.Null(wire.InputCameraId);
    }

    [Fact]
    public void DeviceTokens_MustBeUrlSafe()
    {
        var world = WorldWithDevices();
        world.Cameras.Single(c => c.Id == "cam1").RelayOutputs[0].Token = "front door";
        Assert.Contains("relay output token", ReasonOf(() => WorldValidation.Check(world, 0, 0)));
    }

    [Fact]
    public void DeviceTokens_AreUniqueWithinACamera()
    {
        var world = WorldWithDevices();
        var camera = world.Cameras.Single(c => c.Id == "cam1");
        camera.RelayOutputs.Add(new RelayOutputModel { Token = "LAMP" });
        Assert.Contains("relay output token 'lamp' is used more than once", ReasonOf(() => WorldValidation.Check(world, 0, 0)));
    }

    [Fact]
    public void AMonostableRelay_NeedsAPositiveDelay()
    {
        var world = WorldWithDevices();
        world.Cameras.Single(c => c.Id == "cam1").RelayOutputs[0].DelaySeconds = 0;
        Assert.Contains("monostable delay", ReasonOf(() => WorldValidation.Check(world, 0, 0)));
    }

    [Fact]
    public void AWire_MustNameAnExistingRelayAndSink()
    {
        var world = WorldWithDevices();
        world.Wires[0].InputToken = "nobody";
        Assert.Contains("no digital input 'nobody'", ReasonOf(() => WorldValidation.Check(world, 0, 0)));

        world = WorldWithDevices();
        world.Wires[0].RelayCameraId = "cam9";
        Assert.Contains("no camera 'cam9'", ReasonOf(() => WorldValidation.Check(world, 0, 0)));

        world = WorldWithDevices();
        world.Wires.Add(new WireModel { RelayCameraId = "cam1", RelayToken = "lamp", LampId = "nobody" });
        Assert.Contains("no lamp 'nobody'", ReasonOf(() => WorldValidation.Check(world, 0, 0)));
    }

    [Fact]
    public void AWire_DrivesOneSinkOnly()
    {
        var world = WorldWithDevices();
        world.Lamps.Add(new LampModel { Id = "warning" });
        world.Wires[0].LampId = "warning";
        Assert.Contains("either a digital input or a lamp", ReasonOf(() => WorldValidation.Check(world, 0, 0)));
    }

    [Fact]
    public void LampIds_AreUrlSafeAndUnique()
    {
        var world = WorldWithDevices();
        world.Lamps.Add(new LampModel { Id = "front door" });
        Assert.Contains("Lamp id", ReasonOf(() => WorldValidation.Check(world, 0, 0)));

        world = WorldWithDevices();
        world.Lamps.Add(new LampModel { Id = "warning" });
        world.Lamps.Add(new LampModel { Id = "WARNING" });
        Assert.Contains("Lamp id 'warning' is used more than once", ReasonOf(() => WorldValidation.Check(world, 0, 0)));
    }

    [Fact]
    public void ASink_MayBeDrivenBySeveralRelays()
    {
        var world = WorldWithDevices();
        world.Cameras.Single(c => c.Id == "cam1").RelayOutputs.Add(new RelayOutputModel { Token = "lamp2" });
        world.Wires.Add(new WireModel { RelayCameraId = "cam1", RelayToken = "lamp2", InputCameraId = "cam2", InputToken = "sensor" });
        WorldValidation.Check(world, 0, 0);
    }

    [Fact]
    public void AContact_FollowsTheLogicalStateAndTheIdleState()
    {
        // Normally open: energised closes the contact.
        var relay = new RelayOutputModel { IdleState = IdleStateModel.Open };
        Assert.False(DeviceIo.ContactClosed(relay));
        relay.Active = true;
        Assert.True(DeviceIo.ContactClosed(relay));

        // Normally closed: energised opens it.
        relay = new RelayOutputModel { IdleState = IdleStateModel.Closed };
        Assert.True(DeviceIo.ContactClosed(relay));
        relay.Active = true;
        Assert.False(DeviceIo.ContactClosed(relay));
    }

    [Fact]
    public void ABistableRelay_StaysSwitched()
    {
        using var dir = new TempDir();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(20000));
        var store = StoreWithRelay(dir, RelayModeModel.Bistable, IdleStateModel.Open);
        using var switches = new DeviceIoController(store, time);

        Assert.True(switches.SetRelay("cam1", "lamp", true));
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.True(Active(store));
    }

    [Fact]
    public void AMonostableRelay_FallsBackToItsIdleStateAfterItsDelay()
    {
        using var dir = new TempDir();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(20000));
        var store = StoreWithRelay(dir, RelayModeModel.Monostable, IdleStateModel.Open);
        using var switches = new DeviceIoController(store, time);

        Assert.True(switches.SetRelay("cam1", "lamp", true));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(Active(store));
        Assert.True(ContactClosed(store));

        time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.False(Active(store));
        Assert.False(ContactClosed(store));
    }

    [Fact]
    public void AMonostableRelay_DeEnergisesIntoAClosedContact()
    {
        using var dir = new TempDir();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(20000));
        var store = StoreWithRelay(dir, RelayModeModel.Monostable, IdleStateModel.Closed);
        using var switches = new DeviceIoController(store, time);

        Assert.True(switches.SetRelay("cam1", "lamp", true));
        Assert.False(ContactClosed(store), "an energised normally-closed contact is open");

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(Active(store));
        Assert.True(ContactClosed(store), "the contact is back in its normally-closed idle state");
    }

    [Fact]
    public void AnUnknownRelay_IsNotSwitched()
    {
        using var dir = new TempDir();
        var store = StoreWithRelay(dir, RelayModeModel.Bistable, IdleStateModel.Open);
        using var switches = new DeviceIoController(store, TimeProvider.System);
        Assert.False(switches.SetRelay("cam1", "nobody", true));
        Assert.False(switches.SetRelay("cam9", "lamp", true));
    }

    [Fact]
    public void TheScene_DrawsTheLampsOfTheWorld_AndNoShapeOfACamerasDevices()
    {
        var world = WorldWithDevices();
        world.Lamps.Add(new LampModel { Id = "warning", Position = new Vec3(10, 0, 0) });
        var devices = DeviceScene.Static(world);
        // A relay output or a digital input is a setting of the camera; only the lamp it drives stands
        // in the world to be seen.
        Assert.DoesNotContain(devices, chunk => chunk.Name.StartsWith("cam", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(devices, chunk => chunk.Name == "lamp/warning");
    }

    [Fact]
    public void ALamp_LooksDifferentWhenItIsActive()
    {
        var world = WorldWithDevices();
        world.Lamps.Add(new LampModel { Id = "warning", Position = new Vec3(10, 0, 0) });
        world.Wires.Add(new WireModel { RelayCameraId = "cam1", RelayToken = "lamp", LampId = "warning" });
        var lit = Lamp(world, active: true);
        var dark = Lamp(world, active: false);
        Assert.False(lit.Mesh.Colors.SequenceEqual(dark.Mesh.Colors), "the bulb changes colour");
    }

    [Fact]
    public void AnInput_IsActiveWhenItsCircuitLeavesTheIdleState()
    {
        var world = WorldWithDevices();
        var relay = world.Cameras.Single(c => c.Id == "cam1").RelayOutputs[0];
        // The relay rests closed and starts energised, so its contact is open; the input, whose idle
        // state is open, sees an open circuit and reports its idle state.
        Assert.False(DeviceIo.InputActive(world, "cam2", "sensor"));
        // De-energised, the contact closes: the circuit leaves its idle state and the input is active.
        relay.Active = false;
        Assert.True(DeviceIo.InputActive(world, "cam2", "sensor"));

        // A second relay in parallel: any closed contact closes the circuit, whatever the other does.
        var camera = world.Cameras.Single(c => c.Id == "cam1");
        var second = new RelayOutputModel { Token = "lamp2", IdleState = IdleStateModel.Open };
        camera.RelayOutputs.Add(second);
        world.Wires.Add(new WireModel { RelayCameraId = "cam1", RelayToken = "lamp2", InputCameraId = "cam2", InputToken = "sensor" });
        // Both contacts open again.
        relay.Active = true;
        Assert.False(DeviceIo.InputActive(world, "cam2", "sensor"));
        // One of them closing is enough.
        second.Active = true;
        Assert.True(DeviceIo.InputActive(world, "cam2", "sensor"));
    }

    [Fact]
    public void AnInputWithIdleStateClosed_IsActiveWhenItsCircuitOpens()
    {
        var world = WorldWithDevices();
        world.Cameras.Single(c => c.Id == "cam2").DigitalInputs[0].IdleState = IdleStateModel.Closed;
        var relay = world.Cameras.Single(c => c.Id == "cam1").RelayOutputs[0];

        // Energised, the relay's normally-closed contact opens: the input is away from its idle
        // state, so it reports active.
        Assert.True(DeviceIo.InputActive(world, "cam2", "sensor"));
        // De-energised, the contact closes into the idle state, and the input reports idle.
        relay.Active = false;
        Assert.False(DeviceIo.InputActive(world, "cam2", "sensor"));
    }

    [Fact]
    public void AnUnwiredInput_ReadsNothing()
    {
        var world = WorldWithDevices();
        world.Wires.Clear();
        Assert.Null(DeviceIo.InputActive(world, "cam2", "sensor"));
    }

    [Fact]
    public void ALamp_LightsByItsWiring()
    {
        var world = WorldWithDevices();
        world.Lamps.Add(new LampModel { Id = "open", Name = "NO lamp", Position = new Vec3(10, 0, 0) });
        world.Lamps.Add(new LampModel { Id = "closed", Name = "NC lamp", NormallyClosed = true, Position = new Vec3(12, 0, 0) });
        world.Lamps.Add(new LampModel { Id = "idle", Name = "Unwired lamp", Position = new Vec3(14, 0, 0) });
        foreach (var id in new[] { "open", "closed" })
            world.Wires.Add(new WireModel { RelayCameraId = "cam1", RelayToken = "lamp", LampId = id });
        var relay = world.Cameras.Single(c => c.Id == "cam1").RelayOutputs[0];
        // This test is about the lamps, so the relay is the plain normally-open kind: energised closes it.
        relay.IdleState = IdleStateModel.Open;

        relay.Active = true;
        Assert.True(DeviceIo.LampLit(world, "open"));
        Assert.False(DeviceIo.LampLit(world, "closed"));

        relay.Active = false;
        Assert.False(DeviceIo.LampLit(world, "open"));
        Assert.True(DeviceIo.LampLit(world, "closed"));

        // A lamp nothing drives is simply off, whatever its wiring.
        Assert.False(DeviceIo.LampLit(world, "idle"));
    }

    // ---- The editor's API ----

    [Fact]
    public async Task SavingIo_PersistsTheDevicesAndTheWire()
    {
        var client = factory.CreateClient();
        await PutCamera(client, "io-save", 0);
        var put = await client.PutAsync("/api/cameras/io-save/io", Json(new
        {
            relayOutputs = new[] { Relay("lamp", "Lamp") },
            digitalInputs = new[] { Input("sensor", "Sensor") },
            wires = new[] { Wire("lamp", "io-save", "sensor") },
        }));

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var camera = Store.Current.Cameras.Single(c => c.Id == "io-save");
        Assert.Equal("lamp", Assert.Single(camera.RelayOutputs).Token);
        Assert.Equal("sensor", Assert.Single(camera.DigitalInputs).Token);
        Assert.Single(Store.Current.Wires, w => w.RelayCameraId == "io-save" && w.RelayToken == "lamp");
    }

    [Fact]
    public async Task SwitchingARelay_SetsItsState()
    {
        var client = factory.CreateClient();
        await PutCamera(client, "io-frame", 140);
        await PutIo(client, "io-frame", Relay("lamp", "Lamp"));

        var switched = await client.PutAsync("/api/cameras/io-frame/relays/lamp", Json(new { active = true }));
        Assert.Equal(HttpStatusCode.NoContent, switched.StatusCode);
        Assert.True(Store.Current.Cameras.Single(c => c.Id == "io-frame").RelayOutputs[0].Active);
    }

    [Fact]
    public async Task SavingThePanel_KeepsTheLiveRelayState()
    {
        var client = factory.CreateClient();
        await PutCamera(client, "io-stale", 100);
        await PutIo(client, "io-stale", Relay("lamp", "Lamp"));
        await client.PutAsync("/api/cameras/io-stale/relays/lamp", Json(new { active = true }));

        // A panel that was open before the switch still says the relay is off.
        var put = await client.PutAsync("/api/cameras/io-stale/io", Json(new
        {
            relayOutputs = new[] { Relay("lamp", "Renamed") },
            digitalInputs = Array.Empty<object>(),
            wires = Array.Empty<object>(),
        }));

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var relay = Store.Current.Cameras.Single(c => c.Id == "io-stale").RelayOutputs[0];
        Assert.Equal("Renamed", relay.Name);
        Assert.True(relay.Active);
    }

    [Fact]
    public async Task AnInputWithTwoWires_TakesBoth()
    {
        var client = factory.CreateClient();
        await PutCamera(client, "io-a", -140);
        await PutCamera(client, "io-b", -100);
        await PutCamera(client, "io-c", -60);
        await PutIo(client, "io-c", null, Input("sensor", "Sensor"));
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsync("/api/cameras/io-a/io", Json(new { relayOutputs = new[] { Relay("lamp", "Lamp") }, wires = new[] { Wire("lamp", "io-c", "sensor") } }))).StatusCode);

        var second = await client.PutAsync("/api/cameras/io-b/io",
            Json(new { relayOutputs = new[] { Relay("lamp", "Lamp") }, wires = new[] { Wire("lamp", "io-c", "sensor") } }));
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }

    [Fact]
    public async Task AWireToAnUnknownInput_IsRefused()
    {
        var client = factory.CreateClient();
        await PutCamera(client, "io-d", -20);
        var put = await client.PutAsync("/api/cameras/io-d/io",
            Json(new { relayOutputs = new[] { Relay("lamp", "Lamp") }, wires = new[] { Wire("lamp", "io-d", "nobody") } }));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("no digital input 'nobody'", await ReasonOf(put));
    }

    [Fact]
    public async Task DeletingACamera_TakesTheWiresIntoItsInputs()
    {
        var client = factory.CreateClient();
        await PutCamera(client, "io-e", 20);
        await PutCamera(client, "io-f", 60);
        await PutIo(client, "io-f", null, Input("sensor", "Sensor"));
        await client.PutAsync("/api/cameras/io-e/io",
            Json(new { relayOutputs = new[] { Relay("lamp", "Lamp") }, wires = new[] { Wire("lamp", "io-f", "sensor") } }));
        Assert.Contains(Store.Current.Wires, w => w.InputCameraId == "io-f");

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/cameras/io-f")).StatusCode);
        Assert.DoesNotContain(Store.Current.Wires, w => w.InputCameraId == "io-f" || w.RelayCameraId == "io-f");
    }

    [Fact]
    public async Task SwitchingAnUnknownRelay_Is404()
    {
        var client = factory.CreateClient();
        await PutCamera(client, "io-g", 120);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsync("/api/cameras/io-g/relays/nobody", Json(new { active = true }))).StatusCode);
    }

    // ---- Helpers ----

    private WorldModel WorldWithDevices()
    {
        var world = DefaultWorld.Create();
        // The default world ships devices of its own; these tests want only theirs.
        foreach (var camera in world.Cameras)
        {
            camera.RelayOutputs.Clear();
            camera.DigitalInputs.Clear();
        }
        world.Lamps.Clear();
        world.Wires.Clear();
        world.Cameras.Single(c => c.Id == "cam1").RelayOutputs.Add(new RelayOutputModel
        {
            Token = "lamp", Name = "Lamp", Mode = RelayModeModel.Monostable, DelaySeconds = 2.5f,
            IdleState = IdleStateModel.Closed, Active = true,
        });
        world.Cameras.Single(c => c.Id == "cam2").DigitalInputs.Add(new DigitalInputModel
        {
            Token = "sensor", Name = "Sensor",
        });
        world.Wires.Add(new WireModel { RelayCameraId = "cam1", RelayToken = "lamp", InputCameraId = "cam2", InputToken = "sensor" });
        return world;
    }

    private static SceneChunk Lamp(WorldModel world, bool active)
    {
        world.Cameras.Single(c => c.Id == "cam1").RelayOutputs[0].Active = active;
        return DeviceScene.Static(world).Single(chunk => chunk.Name == "lamp/warning");
    }

    private static WorldStore StoreWithRelay(TempDir dir, RelayModeModel mode, IdleStateModel idle)
    {
        var store = new WorldStore(dir.Path);
        store.Update(world => world.Cameras.Single(c => c.Id == "cam1").RelayOutputs =
        [
            new RelayOutputModel { Token = "lamp", Mode = mode, DelaySeconds = 3, IdleState = idle },
        ]);
        return store;
    }

    private static RelayOutputModel RelayOf(WorldStore store) =>
        store.Current.Cameras.Single(c => c.Id == "cam1").RelayOutputs[0];

    private static bool Active(WorldStore store) => RelayOf(store).Active;

    private static bool ContactClosed(WorldStore store) => DeviceIo.ContactClosed(RelayOf(store));

    private string ReasonOf(Action check)
    {
        var exception = Assert.Throws<InvalidWorldException>(check);
        return exception.Message;
    }

    private static async Task<string> ReasonOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString()!;

    private static JsonContent Json(object value) => JsonContent.Create(value, options: WorldStore.Json);

    private static object Relay(string token, string name) => new
    {
        token, name, mode = "bistable", delaySeconds = 5, idleState = "open",
    };

    private static object Input(string token, string name) => new
    {
        token, name, idleState = "open",
    };

    private static object Wire(string relayToken, string inputCameraId, string inputToken) => new
    {
        relayToken, inputCameraId, inputToken,
    };

    // A fixed camera looking north at the place the devices are put, so its picture shows them. Each
    // test's camera stands apart along X, so another test's camera never blocks its view.
    private static async Task PutCamera(HttpClient client, string id, float x)
    {
        var response = await client.PutAsync($"/api/cameras/{id}", Json(new
        {
            id, name = id, kind = "fixed", position = new { x, y = 3, z = 20 }, heading = 0, pitch = -10, hFov = 75,
            users = Array.Empty<object>(),
        }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task PutIo(HttpClient client, string id, object? relay = null, object? input = null)
    {
        var response = await client.PutAsync($"/api/cameras/{id}/io", Json(new
        {
            relayOutputs = relay == null ? Array.Empty<object>() : new[] { relay },
            digitalInputs = input == null ? Array.Empty<object>() : new[] { input },
            wires = Array.Empty<object>(),
        }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
