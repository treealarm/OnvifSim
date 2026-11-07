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
        // The master stands south of the room, on the line the person walks out by, so one lens
        // holds both the room and the doorway 56 m away; the slave is the room's north-east corner.
        var master = world.Cameras.Single(c => c.Id == "master");
        Assert.Equal(-50.0, master.Position.X, 1);
        Assert.Equal(0.0, master.Position.Z, 1);
        Assert.Equal(2.8, master.Position.Y, 3);

        var ptz = world.Cameras.Single(c => c.Id == "ptz");
        Assert.Equal(-49.5, ptz.Position.X, 1);
        Assert.Equal(-20.5, ptz.Position.Z, 1);
    }

    [Fact]
    public void TheHallPairWatchesTheRoom_AndTheDoorLetsTheWalkerOut()
    {
        // The hall master's boxes drive its slave's aim, so the master must hold the whole of the
        // person's walk indoors — Room 101 and the line from it out through the hallway to the
        // doorway — and the route then leaves through that doorway, where the second pair's master
        // can pick the person up.
        var world = World();
        var slave = MarylandWorld.Cameras.Single(d => d.Id == "ptz");
        Assert.InRange(slave.Latitude, MarylandWorld.Room101South, MarylandWorld.Room101North);
        Assert.InRange(slave.Longitude, MarylandWorld.Room101West, MarylandWorld.Room101East);

        var walker = Assert.Single(world.Walkers);
        Assert.True(walker.Detectable);

        var indoor = walker.Route
            .Where(point => InsideRoom101(world, point.X, point.Z))
            .ToList();
        Assert.NotEmpty(indoor);

        // Outside the building, not merely out of Room 101: the person must cross the north wall.
        var northZ = WorldGeo.ToWorld(world, MarylandWorld.BuildingNorth, MarylandWorld.BuildingWest, 0).Z;
        var outside = walker.Route.Where(point => point.Z < northZ).ToList();
        Assert.NotEmpty(outside);

        // The hall master sees every indoor point: the person it works on never leaves its picture,
        // from the room to the doorway at the far end of the hall.
        var hallMaster = world.Cameras.Single(camera => camera.Id == "master");
        foreach (var point in walker.Route.Where(point => point.Z > northZ))
        {
            Assert.InRange(BearingDelta(world, "master", point.X, point.Z), 0.0f, hallMaster.HFov / 2);
            Assert.True(Distance(world, "master", point.X, point.Z) < 60);
        }

        // Every crossing of the north wall runs through the doorway: no route segment walks through
        // a plate. The closing segment of the loop is indoors, so the crossings are the way out and
        // the way back in.
        var path = walker.Route.Append(walker.Route[0]).ToList();
        var crossings = 0;
        foreach (var (from, to) in path.Zip(path.Skip(1)))
        {
            var fromSide = from.Z - northZ;
            var toSide = to.Z - northZ;
            if (fromSide == 0 || toSide == 0 || Math.Sign(fromSide) == Math.Sign(toSide))
                continue;
            var along = fromSide / (fromSide - toSide);
            var x = from.X + (to.X - from.X) * along;
            Assert.InRange(x, MarylandWorld.DoorX - MarylandWorld.DoorWidth / 2,
                MarylandWorld.DoorX + MarylandWorld.DoorWidth / 2);
            crossings += 1;
        }
        Assert.Equal(2, crossings);

        // The yard master watches the line the person walks out on: every point of the yard leg is
        // inside its picture, so the slave over the doorway has a box to follow the whole way out
        // and back.
        var yardMaster = world.Cameras.Single(camera => camera.Id == "master2");
        foreach (var point in walker.Route.Where(point => point.Z < northZ))
        {
            Assert.InRange(BearingDelta(world, "master2", point.X, point.Z), 0.0f, yardMaster.HFov / 2);
            Assert.InRange(Distance(world, "master2", point.X, point.Z), 25f, 50f);
        }
    }

    [Fact]
    public void TheNorthWall_HasADoorwayOnTheWalkersWayOut()
    {
        // Two plates either side of the opening and a lintel over it: the wall is still a wall, and
        // the doorway is empty geometry the person and the cameras can pass through.
        var world = World();
        var plates = world.Objects.Where(o => o.Id.StartsWith("building-wall-n")).ToList();
        Assert.Equal(3, plates.Count);

        float doorWest = MarylandWorld.DoorX - MarylandWorld.DoorWidth / 2;
        float doorEast = MarylandWorld.DoorX + MarylandWorld.DoorWidth / 2;
        foreach (var plate in plates)
        {
            float left = plate.Position.X - plate.Scale.X / 2, right = plate.Position.X + plate.Scale.X / 2;
            float bottom = plate.Position.Y, top = plate.Position.Y + plate.Scale.Y;
            var blocksTheDoorway = right > doorWest + 0.001f && left < doorEast - 0.001f
                && bottom < MarylandWorld.DoorHeight - 0.001f && top > 0.001f;
            Assert.False(blocksTheDoorway, $"{plate.Id} stands in the doorway");
        }
        Assert.Contains(plates, plate => plate.Position.Y >= MarylandWorld.DoorHeight - 0.001f);
    }

    private static bool InsideRoom101(WorldModel world, float x, float z)
    {
        var geo = WorldGeo.ToGeo(world, new Vec3(x, 0, z));
        return geo.Latitude >= MarylandWorld.Room101South && geo.Latitude <= MarylandWorld.Room101North
            && geo.Longitude >= MarylandWorld.Room101West && geo.Longitude <= MarylandWorld.Room101East;
    }

    /// <summary>How far, in degrees, a ground point is off the camera's heading.</summary>
    private static float BearingDelta(WorldModel world, string cameraId, float x, float z)
    {
        var camera = world.Cameras.Single(candidate => candidate.Id == cameraId);
        var bearing = MathF.Atan2(x - camera.Position.X, -(z - camera.Position.Z)) * (180 / MathF.PI);
        return MathF.Abs(((bearing - camera.Heading + 540) % 360) - 180);
    }

    private static float Distance(WorldModel world, string cameraId, float x, float z)
    {
        var camera = world.Cameras.Single(candidate => candidate.Id == cameraId);
        return MathF.Sqrt(MathF.Pow(x - camera.Position.X, 2) + MathF.Pow(z - camera.Position.Z, 2));
    }

    [Fact]
    public void TheSlaves_ReportTheirHeadInTheTelemetry()
    {
        var world = World();
        foreach (var id in new[] { "ptz", "ptz2" })
            Assert.True(world.Cameras.Single(c => c.Id == id).Metadata.PtzStatus);
        Assert.False(world.Cameras.Single(c => c.Id == "ptz").Metadata.Analytics);
        Assert.False(world.Cameras.Single(c => c.Id == "ptz2").Metadata.Analytics);
        Assert.True(world.Cameras.Single(c => c.Id == "master").Metadata.Analytics);
        Assert.True(world.Cameras.Single(c => c.Id == "master2").Metadata.Analytics);
    }

    [Fact]
    public void TheYardPair_StandsBetweenTheDoorwayAndTheRoad()
    {
        // The second pair works as one out in the yard: the master on a pole between the doorway and
        // the ring road, looking back along the line the person walks, and the slave hanging over
        // the doorway itself, so its picture holds the person from the moment they step out.
        var world = World();
        var master = world.Cameras.Single(c => c.Id == "master2");
        var slave = world.Cameras.Single(c => c.Id == "ptz2");

        Assert.Equal(-88.0, master.Position.X, 0);
        Assert.Equal(-72.0, master.Position.Z, 0);
        Assert.Equal(6f, master.Position.Y, 3);
        Assert.Equal(88.8f, master.Heading, 3);
        Assert.Equal(55f, master.HFov, 3);
        Assert.Equal(-9.5f, master.Pitch, 3);

        // Over the doorway: on its line, just outside the wall, above the lintel.
        Assert.Equal(MarylandWorld.DoorX, slave.Position.X, 0);
        Assert.Equal(-56.0, slave.Position.Z, 0);
        Assert.Equal(5.5f, slave.Position.Y, 3);
        Assert.Equal(0f, slave.Heading, 3);
        Assert.Equal(60f, slave.Ptz!.FovWide, 3);

        // The car is no longer what this pair follows: it drives round the ring road 40–50 m away,
        // too small in the master's picture for the detector, and the vehicle camera rides it.
        var vehicle = Assert.Single(world.Vehicles);
        Assert.Equal("v1", vehicle.Id);
        Assert.True(vehicle.Detectable);
    }

    [Fact]
    public void TheCamerasKeepThePortOrderTheDemoProvisions()
    {
        // ta_vms's demo adds sim-master, sim-ptz, sim-master2, sim-ptz2 and sim-vehicle at
        // 8101..8105 from an ONVIF base of 8101, so the order of the world's cameras is part of
        // the contract.
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
    public void TheMastersRelays_DriveEveryKindOfSink()
    {
        var world = World();
        var master = world.Cameras.Single(c => c.Id == "master");
        Assert.Equal(["lamp", "peer", "loop"], master.RelayOutputs.Select(r => r.Token));
        Assert.Equal("input", Assert.Single(master.DigitalInputs).Token);
        Assert.Equal("input", Assert.Single(world.Cameras.Single(c => c.Id == "ptz").DigitalInputs).Token);

        // One relay lights the lamp in the hall, one drives another camera's sensor, one a sensor of
        // the master itself: every kind of sink the editor can wire.
        var lamp = Assert.Single(world.Lamps);
        Assert.Equal("alert", lamp.Id);
        Assert.False(lamp.NormallyClosed);
        Assert.Contains(world.Wires, wire => wire.RelayToken == "lamp" && wire.LampId == "alert");
        Assert.Contains(world.Wires, wire =>
            wire.RelayToken == "peer" && wire.InputCameraId == "ptz" && wire.InputToken == "input");
        Assert.Contains(world.Wires, wire =>
            wire.RelayToken == "loop" && wire.InputCameraId == "master" && wire.InputToken == "input");
        Assert.All(world.Wires, wire =>
            Assert.Equal("master", wire.RelayCameraId));

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
