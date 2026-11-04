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
        Assert.Equal(19.9, ptz.Position.X, 1);
        Assert.Equal(-11.1, ptz.Position.Z, 1);
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
    public void TheMastersLamp_DrivesTheCarsSensor()
    {
        var world = World();
        var relay = Assert.Single(world.Cameras.Single(c => c.Id == "master").RelayOutputs);
        Assert.Equal("lamp", relay.Token);
        var input = Assert.Single(world.Cameras.Single(c => c.Id == MarylandWorld.VehicleCameraId).DigitalInputs);
        Assert.Equal("input", input.Token);
        var wire = Assert.Single(world.Wires);
        Assert.Equal(("master", "lamp", MarylandWorld.VehicleCameraId, "input"),
            (wire.RelayCameraId, wire.RelayToken, wire.InputCameraId, wire.InputToken));
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
