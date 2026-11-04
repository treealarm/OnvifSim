using System.Text.Json;
using OnvifSim.Editor;
using OnvifSim.World;

namespace OnvifSim.Tests;

// The demo world: the demo's own anchor, envelope and camera poses, walls that are plates, and the
// relay, sensor and wire the demo shows on its diagram.
public class MarylandWorldTests
{
    private static WorldModel World() => MarylandWorld.Create();

    [Fact]
    public void TheWorldIsValid() =>
        WorldValidation.Check(World(), onvifBasePort: 8001);

    [Fact]
    public void TheAnchorIsTheCentreOfTheDemoBuilding()
    {
        var world = World();
        Assert.Equal(MarylandWorld.Latitude, world.Geo.Latitude, 9);
        Assert.Equal(MarylandWorld.Longitude, world.Geo.Longitude, 9);
        Assert.Equal(0, world.Geo.North);
    }

    [Fact]
    public void EveryPlacedCamera_ReportsTheDemoPlacementsGeo()
    {
        var world = World();
        foreach (var demo in MarylandWorld.Cameras)
        {
            var camera = world.Cameras.Single(c => c.Id == demo.Id);
            var geo = WorldGeo.ToGeo(world, camera.Position);
            // A millionth of a degree is about a decimetre: the demo world and the map must agree
            // closer than a marker's own size.
            Assert.Equal(demo.Latitude, geo.Latitude, 6);
            Assert.Equal(demo.Longitude, geo.Longitude, 6);
            Assert.Equal(demo.Height, geo.Elevation, 2);
            Assert.Equal(demo.Heading, camera.Heading, 3);
        }
    }

    [Fact]
    public void TheRoomCameras_StandWhereTheDemoPlacesThem()
    {
        var world = World();
        var master = world.Cameras.Single(c => c.Id == "master");
        Assert.Equal(-62.3, master.Position.X, 1);
        Assert.Equal(-11.1, master.Position.Z, 1);
        Assert.Equal(2.8, master.Position.Y, 3);

        var ptz = world.Cameras.Single(c => c.Id == "ptz");
        Assert.Equal(-49.5, ptz.Position.X, 1);
        Assert.Equal(-20.5, ptz.Position.Z, 1);
    }

    [Fact]
    public void ThePairAndTheWalker_StandInOneRoom()
    {
        // The master's boxes drive the slave's aim, so the two must watch the same space — Room 101
        // of the demo's floor plan — and the person they work on walks inside it.
        var world = World();
        foreach (var demo in MarylandWorld.Cameras.Where(d => d.Id is "master" or "ptz"))
        {
            Assert.InRange(demo.Latitude, MarylandWorld.Room101South, MarylandWorld.Room101North);
            Assert.InRange(demo.Longitude, MarylandWorld.Room101West, MarylandWorld.Room101East);
        }
        var walker = Assert.Single(world.Walkers);
        Assert.True(walker.Detectable);
        foreach (var point in walker.Route)
        {
            var geo = WorldGeo.ToGeo(world, new Vec3(point.X, 0, point.Z));
            Assert.InRange(geo.Latitude, MarylandWorld.Room101South, MarylandWorld.Room101North);
            Assert.InRange(geo.Longitude, MarylandWorld.Room101West, MarylandWorld.Room101East);
        }
    }

    [Fact]
    public void TheSlaves_ReportTheirHeadInTheTelemetry()
    {
        var world = World();
        foreach (var id in new[] { "ptz", "ptz2" })
            Assert.True(world.Cameras.Single(c => c.Id == id).Metadata.PtzStatus);
        Assert.False(world.Cameras.Single(c => c.Id == "ptz").Metadata.Analytics);
        Assert.True(world.Cameras.Single(c => c.Id == "master").Metadata.Analytics);
    }

    [Fact]
    public void TheCamerasKeepThePortOrderTheDemoProvisions()
    {
        // ta_vms's demo adds sim-master, sim-ptz, sim-master2 and sim-ptz2 at 8101..8104 from an
        // ONVIF base of 8101, so the order of the world's cameras is part of the contract.
        Assert.Equal(["master", "ptz", "master2", "ptz2", "vehicle"],
            World().Cameras.Select(c => c.Id));
    }

    [Fact]
    public void TheVehicleCameraRidesTheCar()
    {
        var world = World();
        var camera = world.Cameras.Single(c => c.Id == MarylandWorld.VehicleCameraId);
        Assert.Equal("v1", camera.MountedOnVehicleId);
        Assert.Contains(world.Vehicles, v => v.Id == camera.MountedOnVehicleId && v.Return == RouteReturn.Loop);
    }

    [Fact]
    public void TheMastersLamp_DrivesThePtzsSensor()
    {
        var world = World();
        var relay = Assert.Single(world.Cameras.Single(c => c.Id == "master").RelayOutputs);
        Assert.Equal("lamp", relay.Token);
        var ptz = world.Cameras.Single(c => c.Id == "ptz");
        var input = Assert.Single(ptz.DigitalInputs);
        Assert.Equal("input", input.Token);
        var lamp = Assert.Single(world.Lamps);
        Assert.Equal("alert", lamp.Id);
        Assert.False(lamp.NormallyClosed);
        Assert.Contains(world.Wires, wire => wire.InputToken == "input");
        Assert.Contains(world.Wires, wire => wire.LampId == "alert");
        Assert.All(world.Wires, wire =>
            Assert.Equal(("master", "lamp"), (wire.RelayCameraId, wire.RelayToken)));
        // A device on the car would move the wire, and every camera that sees it, every frame.
        Assert.Empty(world.Cameras.Single(c => c.Id == MarylandWorld.VehicleCameraId).DigitalInputs);
        Assert.Empty(world.Cameras.Single(c => c.Id == MarylandWorld.VehicleCameraId).RelayOutputs);
    }

    [Fact]
    public void TheBuildingIsPlates_NotOneSolidBox()
    {
        var world = World();
        var plates = world.Objects.Where(o => o.Id.StartsWith("building-") || o.Id.StartsWith("room-")).ToList();
        Assert.NotEmpty(plates);
        Assert.All(plates, o => Assert.Equal("box", o.Type));

        // The rooms are walls standing inside the building, and the roof is a plate above them: no
        // solid house or office stands where a camera has to see the inside.
        foreach (var camera in MarylandWorld.Cameras)
        {
            var position = WorldGeo.ToWorld(World(), camera.Latitude, camera.Longitude, camera.Height);
            Assert.DoesNotContain(world.Objects, o =>
                o.Type is "house" or "office" &&
                MathF.Abs(o.Position.X - position.X) < 15 && MathF.Abs(o.Position.Z - position.Z) < 15);
        }
    }

    [Fact]
    public void TheShippedWorldFile_IsTheBuilderOutput()
    {
        var expected = JsonSerializer.Serialize(World(), WorldStore.Json);
        var actual = File.ReadAllText(Path.Combine(RepoRoot(), "data", "worlds", MarylandWorld.Name + ".json"));
        // A checkout that normalizes line endings must not make the file look stale.
        Assert.Equal(Normalized(expected), Normalized(actual));
    }

    private static string Normalized(string json) => json.Replace("\r\n", "\n").TrimEnd();

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OnvifSim.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
