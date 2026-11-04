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
        Assert.Equal(new Vec3(1, 0, -3), relay.Position);
        var input = Assert.Single(read.Cameras.Single(c => c.Id == "cam2").DigitalInputs);
        Assert.Equal("sensor", input.Token);
        var wire = Assert.Single(read.Wires);
        Assert.Equal(("cam1", "lamp", "cam2", "sensor"), (wire.RelayCameraId, wire.RelayToken, wire.InputCameraId, wire.InputToken));
        Assert.Null(wire.LampCameraId);
    }

    [Fact]
    public void ALampAndItsWire_RoundTripThroughTheWorldFile()
    {
        var world = WorldWithDevices();
        var camera = world.Cameras.Single(c => c.Id == "cam2");
        camera.Lamps.Add(new LampModel { Token = "warning", Name = "Warning lamp", NormallyClosed = true, Position = new Vec3(2, 0, 1) });
        world.Wires.Add(new WireModel { RelayCameraId = "cam1", RelayToken = "lamp", LampCameraId = "cam2", LampToken = "warning" });
        var read = JsonSerializer.Deserialize<WorldModel>(JsonSerializer.Serialize(world, WorldStore.Json), WorldStore.Json)!;

        var lamp = Assert.Single(read.Cameras.Single(c => c.Id == "cam2").Lamps);
        Assert.Equal(("warning", "Warning lamp", true), (lamp.Token, lamp.Name, lamp.NormallyClosed));
        var wire = read.Wires.Single(w => w.LampToken == "warning");
        Assert.Equal(("cam1", "lamp", "cam2", "warning"),
            (wire.RelayCameraId, wire.RelayToken, wire.LampCameraId, wire.LampToken));
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
        world.Wires.Add(new WireModel { RelayCameraId = "cam1", RelayToken = "lamp", LampCameraId = "cam2", LampToken = "nobody" });
        Assert.Contains("no lamp 'nobody'", ReasonOf(() => WorldValidation.Check(world, 0, 0)));
    }

    [Fact]
    public void AWire_DrivesOneSinkOnly()
    {
        var world = WorldWithDevices();
        world.Cameras.Single(c => c.Id == "cam2").Lamps.Add(new LampModel { Token = "warning" });
        world.Wires[0].LampCameraId = "cam2";
        world.Wires[0].LampToken = "warning";
        Assert.Contains("either a digital input or a lamp", ReasonOf(() => WorldValidation.Check(world, 0, 0)));
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

        time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.False(Active(store));
    }

    [Fact]
    public void AMonostableRelay_FallsBackToAClosedIdleStateAsActive()
    {
        using var dir = new TempDir();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(20000));
        var store = StoreWithRelay(dir, RelayModeModel.Monostable, IdleStateModel.Closed);
        using var switches = new DeviceIoController(store, time);

        switches.SetRelay("cam1", "lamp", false);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(Active(store));
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
    public void TheScene_HasALampForARelay_AndALineForAWire()
    {
        var world = WorldWithDevices();
        var devices = DeviceScene.Static(world);
        Assert.Contains(devices, chunk => chunk.Name == "cam1/relay/lamp");
        Assert.Contains(devices, chunk => chunk.Name == "cam2/input/sensor");
        var wire = Assert.Single(DeviceScene.StaticWires(world));
        Assert.Equal(2, wire.Lines.Length);
        Assert.Contains(RenderScene.Build(world).Chunks, chunk => chunk.Lines.Length == 2);
    }

    [Fact]
    public void ALamp_LooksDifferentWhenItIsActive()
    {
        var world = WorldWithDevices();
        var lit = Lamp(world, active: true);
        var dark = Lamp(world, active: false);
        Assert.False(lit.Mesh.Colors.SequenceEqual(dark.Mesh.Colors), "the shade changes colour");
    }

    [Fact]
    public void AnInput_ReadsInverted_AndAnyClosedRelayPullsItToZero()
    {
        var world = WorldWithDevices();
        var relay = world.Cameras.Single(c => c.Id == "cam1").RelayOutputs[0];
        relay.Active = false;
        Assert.True(DeviceIo.InputActive(world, "cam2", "sensor"));
        relay.Active = true;
        Assert.False(DeviceIo.InputActive(world, "cam2", "sensor"));

        // A second relay in parallel: one closed contact is enough to pull the input low.
        var camera = world.Cameras.Single(c => c.Id == "cam1");
        camera.RelayOutputs.Add(new RelayOutputModel { Token = "lamp2" });
        world.Wires.Add(new WireModel { RelayCameraId = "cam1", RelayToken = "lamp2", InputCameraId = "cam2", InputToken = "sensor" });
        relay.Active = false;
        Assert.True(DeviceIo.InputActive(world, "cam2", "sensor"));
        camera.RelayOutputs[1].Active = true;
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
        var camera = world.Cameras.Single(c => c.Id == "cam2");
        camera.Lamps.Add(new LampModel { Token = "open", Name = "NO lamp" });
        camera.Lamps.Add(new LampModel { Token = "closed", Name = "NC lamp", NormallyClosed = true });
        camera.Lamps.Add(new LampModel { Token = "idle", Name = "Unwired lamp" });
        foreach (var token in new[] { "open", "closed" })
            world.Wires.Add(new WireModel { RelayCameraId = "cam1", RelayToken = "lamp", LampCameraId = "cam2", LampToken = token });
        var relay = world.Cameras.Single(c => c.Id == "cam1").RelayOutputs[0];

        relay.Active = true;
        Assert.True(DeviceIo.LampLit(world, "cam2", "open"));
        Assert.False(DeviceIo.LampLit(world, "cam2", "closed"));

        relay.Active = false;
        Assert.False(DeviceIo.LampLit(world, "cam2", "open"));
        Assert.True(DeviceIo.LampLit(world, "cam2", "closed"));

        // A lamp nothing drives is simply off, whatever its wiring.
        Assert.False(DeviceIo.LampLit(world, "cam2", "idle"));
    }

    [Fact]
    public void TheScene_DrawsALampForALampDevice()
    {
        var world = WorldWithDevices();
        world.Cameras.Single(c => c.Id == "cam2").Lamps.Add(new LampModel { Token = "warning", Position = new Vec3(2, 0, 1) });
        world.Wires.Add(new WireModel { RelayCameraId = "cam1", RelayToken = "lamp", LampCameraId = "cam2", LampToken = "warning" });

        Assert.Contains(DeviceScene.Static(world), chunk => chunk.Name == "cam2/lamp/warning");
        Assert.Equal(2, DeviceScene.StaticWires(world).Count);
    }

    [Fact]
    public void ADevice_StandsToTheRightOfItsCamera()
    {
        var world = DefaultWorld.Create();
        var camera = world.Cameras.Single(c => c.Id == "cam1");
        camera.Position = new Vec3(10, 5, 20);
        camera.Heading = 90; // facing east
        var terrain = new Terrain(world.Terrain);

        // Two metres to the camera's right is south, +Z, since it faces east.
        var point = DeviceScene.DevicePoint(camera, world, terrain, DateTimeOffset.UnixEpoch, new Vec3(2, 1, 0));
        Assert.Equal(10, point.X, 2);
        Assert.Equal(22, point.Z, 2);
        Assert.Equal(terrain.HeightAt(point.X, point.Z) + 1, point.Y, 2);
    }

    [Fact]
    public void ADeviceOnACar_MovesWithIt()
    {
        var world = DefaultWorld.Create();
        var camera = world.Cameras.Single(c => c.Id == "cam5");
        var terrain = new Terrain(world.Terrain);
        var at = DateTimeOffset.UnixEpoch.AddDays(20000);

        var first = DeviceScene.DevicePoint(camera, world, terrain, at, new Vec3(1, 0, 0));
        var later = DeviceScene.DevicePoint(camera, world, terrain, at.AddSeconds(5), new Vec3(1, 0, 0));
        Assert.True(Vector3.Distance(first, later) > 1, "the car drives");
    }

    // ---- The editor's API ----

    [Fact]
    public async Task SavingIo_PersistsTheDevicesAndTheWire()
    {
        var client = factory.CreateClient();
        await PutCamera(client, "io-save", 0);
        var put = await client.PutAsync("/api/cameras/io-save/io", Json(new
        {
            relayOutputs = new[] { Relay("lamp", "Lamp", -4) },
            digitalInputs = new[] { Input("sensor", "Sensor", -6) },
            wires = new[] { Wire("lamp", "io-save", "sensor") },
        }));

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var camera = Store.Current.Cameras.Single(c => c.Id == "io-save");
        Assert.Equal("lamp", Assert.Single(camera.RelayOutputs).Token);
        Assert.Equal("sensor", Assert.Single(camera.DigitalInputs).Token);
        Assert.Single(Store.Current.Wires, w => w.RelayCameraId == "io-save" && w.RelayToken == "lamp");
    }

    [Fact]
    public async Task SwitchingARelay_ChangesThePicture()
    {
        var client = factory.CreateClient();
        await PutCamera(client, "io-frame", 140);
        await PutIo(client, "io-frame", Relay("lamp", "Lamp", -4));
        var before = await client.GetByteArrayAsync("/api/cameras/io-frame/snapshot.jpg");

        var switched = await client.PutAsync("/api/cameras/io-frame/relays/lamp", Json(new { active = true }));
        Assert.Equal(HttpStatusCode.NoContent, switched.StatusCode);
        Assert.True(Store.Current.Cameras.Single(c => c.Id == "io-frame").RelayOutputs[0].Active);

        var after = await client.GetByteArrayAsync("/api/cameras/io-frame/snapshot.jpg");
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task SavingThePanel_KeepsTheLiveRelayState()
    {
        var client = factory.CreateClient();
        await PutCamera(client, "io-stale", 100);
        await PutIo(client, "io-stale", Relay("lamp", "Lamp", -4));
        await client.PutAsync("/api/cameras/io-stale/relays/lamp", Json(new { active = true }));

        // A panel that was open before the switch still says the relay is off.
        var put = await client.PutAsync("/api/cameras/io-stale/io", Json(new
        {
            relayOutputs = new[] { Relay("lamp", "Renamed", -4) },
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
        await PutIo(client, "io-c", null, Input("sensor", "Sensor", 0));
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsync("/api/cameras/io-a/io", Json(new { relayOutputs = new[] { Relay("lamp", "Lamp", 0) }, wires = new[] { Wire("lamp", "io-c", "sensor") } }))).StatusCode);

        var second = await client.PutAsync("/api/cameras/io-b/io",
            Json(new { relayOutputs = new[] { Relay("lamp", "Lamp", 0) }, wires = new[] { Wire("lamp", "io-c", "sensor") } }));
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }

    [Fact]
    public async Task AWireToAnUnknownInput_IsRefused()
    {
        var client = factory.CreateClient();
        await PutCamera(client, "io-d", -20);
        var put = await client.PutAsync("/api/cameras/io-d/io",
            Json(new { relayOutputs = new[] { Relay("lamp", "Lamp", 0) }, wires = new[] { Wire("lamp", "io-d", "nobody") } }));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("no digital input 'nobody'", await ReasonOf(put));
    }

    [Fact]
    public async Task DeletingACamera_TakesTheWiresIntoItsInputs()
    {
        var client = factory.CreateClient();
        await PutCamera(client, "io-e", 20);
        await PutCamera(client, "io-f", 60);
        await PutIo(client, "io-f", null, Input("sensor", "Sensor", 0));
        await client.PutAsync("/api/cameras/io-e/io",
            Json(new { relayOutputs = new[] { Relay("lamp", "Lamp", 0) }, wires = new[] { Wire("lamp", "io-f", "sensor") } }));
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
            camera.Lamps.Clear();
        }
        world.Wires.Clear();
        world.Cameras.Single(c => c.Id == "cam1").RelayOutputs.Add(new RelayOutputModel
        {
            Token = "lamp", Name = "Lamp", Mode = RelayModeModel.Monostable, DelaySeconds = 2.5f,
            IdleState = IdleStateModel.Closed, Active = true, Position = new Vec3(1, 0, -3),
        });
        world.Cameras.Single(c => c.Id == "cam2").DigitalInputs.Add(new DigitalInputModel
        {
            Token = "sensor", Name = "Sensor", Position = new Vec3(-1, 0, 2),
        });
        world.Wires.Add(new WireModel { RelayCameraId = "cam1", RelayToken = "lamp", InputCameraId = "cam2", InputToken = "sensor" });
        return world;
    }

    private static SceneChunk Lamp(WorldModel world, bool active)
    {
        world.Cameras.Single(c => c.Id == "cam1").RelayOutputs[0].Active = active;
        return DeviceScene.Static(world).Single(chunk => chunk.Name == "cam1/relay/lamp");
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

    private static bool Active(WorldStore store) =>
        store.Current.Cameras.Single(c => c.Id == "cam1").RelayOutputs[0].Active;

    private string ReasonOf(Action check)
    {
        var exception = Assert.Throws<InvalidWorldException>(check);
        return exception.Message;
    }

    private static async Task<string> ReasonOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString()!;

    private static JsonContent Json(object value) => JsonContent.Create(value, options: WorldStore.Json);

    private static object Relay(string token, string name, float z) => new
    {
        token, name, mode = "bistable", delaySeconds = 5, idleState = "open", position = new { x = 0, y = 0, z },
    };

    private static object Input(string token, string name, float z) => new
    {
        token, name, idleState = "open", position = new { x = 0, y = 0, z },
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
