using System.Text.Json;
using OnvifSim.Editor;
using OnvifSim.Geometry;
using OnvifSim.World;

namespace OnvifSim.Tests;

// The demo world: the real Biomolecular Sciences building and the real roads round its block, the
// demo's own anchor and camera poses, and the relay, sensor and wire the demo shows on its diagram.
public class MarylandWorldTests
{
    private static WorldModel World() => MarylandWorld.Create();

    [Fact]
    public void TheWorldIsValid() =>
        WorldValidation.Check(World(), onvifBasePort: 8001);

    [Fact]
    public void TheAnchorIsTheDemoWorlds()
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
            Assert.True(Math.Abs(demo.Latitude - geo.Latitude) < 5e-7, $"{demo.Id}: {geo.Latitude} vs {demo.Latitude}");
            Assert.True(Math.Abs(demo.Longitude - geo.Longitude) < 5e-7, $"{demo.Id}: {geo.Longitude} vs {demo.Longitude}");
            Assert.Equal(demo.Height, geo.Elevation, 2);
            Assert.Equal(demo.Heading, camera.Heading, 3);
        }
    }

    [Fact]
    public void TheRoomCameras_StandWhereTheDemoPlacesThem()
    {
        var world = World();
        // The master stands south of Room 101, on the line the person walks out by, so one lens holds
        // both the room and the doorway; the slave is in the room's south-east corner.
        var master = world.Cameras.Single(c => c.Id == "master");
        Assert.Equal(-8.0, master.Position.X, 1);
        Assert.Equal(-57.0, master.Position.Z, 1);
        Assert.Equal(2.8, master.Position.Y, 3);

        var ptz = world.Cameras.Single(c => c.Id == "ptz");
        Assert.Equal(-1.7, ptz.Position.X, 1);
        Assert.Equal(-63.7, ptz.Position.Z, 1);
    }

    // The building is the real one: one prism of the footprint OSM gives way 24303349, at the height
    // OSM gives it. Nothing of it is a stand-in box.
    [Fact]
    public void TheBuildingIsTheRealFootprint()
    {
        var world = World();
        var building = world.Objects.Single(o => o.Id == "building");
        Assert.Equal("prism", building.Type);
        Assert.Equal(MarylandWorld.BuildingHeight, building.Scale.Y, 2);
        Assert.Equal(1f, building.Scale.X, 3);
        Assert.Equal(1f, building.Scale.Z, 3);

        var outline = WorldRing(building);
        Assert.Equal(MarylandWorld.BuildingOutline.Length, outline.Count);
        // The stored outline is the normalised one — direction and all — so every corner of the OSM
        // footprint is in it, to a centimetre.
        foreach (var expected in MarylandWorld.BuildingOutline)
            Assert.Contains(outline, actual => MathF.Abs(expected.X - actual.X) < 0.01f && MathF.Abs(expected.Z - actual.Z) < 0.01f);
        var area = MathF.Abs(Polygon.SignedArea(outline));
        Assert.InRange(area, 54.8f * 31.3f * 0.7f, 54.8f * 31.3f);
    }

    // The defect this milestone was opened for: the old hall covered two real roads. No building of
    // this world may reach into a road's own width.
    [Fact]
    public void NoBuildingStandsOnARoad()
    {
        var world = World();
        foreach (var building in world.Objects.Where(o => o.Type == "prism" && o.Scale.Y >= 1))
        {
            var ring = WorldRing(building);
            foreach (var road in world.Roads)
            {
                float clearance = PolygonToLineDistance(ring, road.Points);
                Assert.True(clearance >= road.Width / 2,
                    $"{building.Id} is {clearance:0.0} m from {road.Id}, which is {road.Width / 2:0.0} m half wide");
            }
        }
    }

    // The tree the demo draws stands on grass: not on a road, not in a lot, not under a building.
    [Fact]
    public void TheTreesStandOnGrass()
    {
        var world = World();
        var trees = world.Objects.Where(o => o.Type.StartsWith("tree.")).ToList();
        Assert.NotEmpty(trees);
        foreach (var tree in trees)
        {
            var at = new GroundPoint(tree.Position.X, tree.Position.Z);
            foreach (var road in world.Roads)
                Assert.True(DistanceToLine(road.Points, at) >= road.Width / 2, $"{tree.Id} stands on {road.Id}");
            foreach (var prism in world.Objects.Where(o => o.Type == "prism"))
                Assert.False(Polygon.Contains(WorldRing(prism), at), $"{tree.Id} stands inside {prism.Id}");
        }
    }

    [Fact]
    public void TheHallPairWatchesTheRoom_AndTheDoorLetsTheWalkerOut()
    {
        // The hall master's boxes drive its slave's aim, so the master must hold the whole of the
        // person's walk indoors — Room 101 and the line from it out to the doorway — and the route
        // then leaves through that doorway, where the second pair's master picks the person up.
        var world = World();
        var slave = MarylandWorld.Cameras.Single(d => d.Id == "ptz");
        Assert.InRange(slave.Latitude, MarylandWorld.Room101South, MarylandWorld.Room101North);
        Assert.InRange(slave.Longitude, MarylandWorld.Room101West, MarylandWorld.Room101East);

        var walker = Assert.Single(world.Walkers);
        Assert.True(walker.Detectable);

        var indoor = walker.Route.Where(point => InsideRoom101(world, point.X, point.Z)).ToList();
        Assert.NotEmpty(indoor);

        // Out of the building, not merely out of Room 101: the person must cross the north wall.
        var northZ = MarylandWorld.NorthWallZ;
        var outside = walker.Route.Where(point => point.Z < northZ).ToList();
        Assert.NotEmpty(outside);

        // Every indoor point stands under the real roof and inside the hall master's picture.
        var building = world.Objects.Single(o => o.Id == "building");
        var footprint = WorldRing(building);
        var hallMaster = world.Cameras.Single(camera => camera.Id == "master");
        foreach (var point in walker.Route.Where(point => point.Z > northZ))
        {
            Assert.True(Polygon.Contains(footprint, new GroundPoint(point.X, point.Z)), $"({point.X},{point.Z}) is outside the building");
            Assert.InRange(BearingDelta(world, "master", point.X, point.Z), 0.0f, hallMaster.HFov / 2);
            Assert.True(Distance(world, "master", point.X, point.Z) < 60);
        }

        // Every crossing of the north wall runs through the doorway. The closing segment of the loop
        // is indoors, so the crossings are the way out and the way back in.
        var path = walker.Route.Append(walker.Route[0]).ToList();
        var crossings = 0;
        foreach (var (from, to) in path.Zip(path.Skip(1)))
        {
            var fromSide = from.Z - northZ;
            var toSide = to.Z - northZ;
            if (fromSide == 0 || toSide == 0 || Math.Sign(fromSide) == Math.Sign(toSide)) continue;
            var along = fromSide / (fromSide - toSide);
            var x = from.X + (to.X - from.X) * along;
            Assert.InRange(x, MarylandWorld.DoorX - MarylandWorld.DoorWidth / 2, MarylandWorld.DoorX + MarylandWorld.DoorWidth / 2);
            crossings += 1;
        }
        Assert.Equal(2, crossings);

        // The yard master watches the line the person walks out on: every point of the parking lot is
        // inside its picture, so the slave has a box to follow the whole way out and back.
        var yardMaster = world.Cameras.Single(camera => camera.Id == "master2");
        foreach (var point in walker.Route.Where(point => point.Z < northZ))
        {
            Assert.InRange(BearingDelta(world, "master2", point.X, point.Z), 0.0f, yardMaster.HFov / 2);
            Assert.InRange(Distance(world, "master2", point.X, point.Z), 20f, 50f);
        }
    }

    // The doorway is an opening of the building's own wall, sitting on the segment the walker crosses:
    // the wall is cut there and a lintel spans the top, so the opening is empty geometry.
    [Fact]
    public void TheBuildingHasADoorwayOnTheWalkersWayOut()
    {
        var world = World();
        var building = world.Objects.Single(o => o.Id == "building");
        var opening = Assert.Single(building.Openings!);
        Assert.Equal(MarylandWorld.DoorWidth, opening.Width, 3);
        Assert.Equal(MarylandWorld.DoorHeight, opening.Height, 3);
        Assert.Equal(0f, opening.Sill, 3);
        Assert.InRange(opening.Edge, 0, building.Points!.Count - 1);

        // Its segment crosses the doorway's line at the doorway's height: the wall's own outline.
        var ring = WorldRing(building);
        var a = ring[opening.Edge];
        var b = ring[(opening.Edge + 1) % ring.Count];
        float length = MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
        var centre = new GroundPoint(a.X + (b.X - a.X) * ((opening.Offset + opening.Width / 2) / length),
            a.Z + (b.Z - a.Z) * ((opening.Offset + opening.Width / 2) / length));
        Assert.Equal(MarylandWorld.DoorX, centre.X, 1);
        Assert.Equal(MarylandWorld.NorthWallZ, centre.Z, 1);
    }

    // The demo's landmarks are marked in the world: one block per point, of the landmark's own
    // colour, standing on the ground the calibration aims at. ta_vms puts a landmark on that same
    // point (DemoWorlds.cs, mirrored in tests/onvifsim/world.py), so a camera's picture and the map
    // show one place rather than two.
    [Fact]
    public void TheDemoLandmarksAreMarkedInTheWorld()
    {
        var world = World();
        Assert.Equal(10, MarylandWorld.Landmarks.Length);
        Assert.Equal(["hall", "road"], MarylandWorld.Landmarks.Select(landmark => landmark.Pair).Distinct());
        foreach (var landmark in MarylandWorld.Landmarks)
        {
            var marker = world.Objects.Single(
                o => o.Id == $"lm-{landmark.Pair}-{landmark.Id.ToLowerInvariant()}");
            Assert.Equal("box", marker.Type);
            Assert.Equal(landmark.X, marker.Position.X, 3);
            Assert.Equal(0, marker.Position.Y, 3);
            Assert.Equal(landmark.Z, marker.Position.Z, 3);
            Assert.Equal(landmark.Color, marker.Color);
            Assert.Equal(MarylandWorld.LandmarkSize, marker.Scale.X, 3);
            Assert.Equal(MarylandWorld.LandmarkSize, marker.Scale.Y, 3);
            Assert.Equal(MarylandWorld.LandmarkSize, marker.Scale.Z, 3);
            // No pattern on it: the colour is what identifies the landmark in the picture.
            Assert.Equal("none", marker.Texture);
        }
    }

    private static bool InsideRoom101(WorldModel world, float x, float z)
    {
        var geo = WorldGeo.ToGeo(world, new Vec3(x, 0, z));
        return geo.Latitude >= MarylandWorld.Room101South && geo.Latitude <= MarylandWorld.Room101North
            && geo.Longitude >= MarylandWorld.Room101West && geo.Longitude <= MarylandWorld.Room101East;
    }

    // A prism's outline in the world's metres (none of this world's objects is turned).
    private static List<GroundPoint> WorldRing(WorldObject obj) =>
        obj.Points!.Select(p => new GroundPoint(p.X + obj.Position.X, p.Z + obj.Position.Z)).ToList();

    private static float DistanceToLine(IReadOnlyList<GroundPoint> line, GroundPoint p)
    {
        float best = float.MaxValue;
        for (int i = 0; i + 1 < line.Count; i++)
            best = MathF.Min(best, DistanceToSegment(line[i], line[i + 1], p));
        return best;
    }

    private static float DistanceToSegment(GroundPoint a, GroundPoint b, GroundPoint p)
    {
        float dx = b.X - a.X, dz = b.Z - a.Z;
        float lengthSquared = dx * dx + dz * dz;
        float t = lengthSquared <= 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Z - a.Z) * dz) / lengthSquared, 0, 1);
        float x = a.X + dx * t, z = a.Z + dz * t;
        return MathF.Sqrt((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z));
    }

    private static float PolygonToLineDistance(IReadOnlyList<GroundPoint> ring, IReadOnlyList<GroundPoint> line)
    {
        float best = float.MaxValue;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            best = MathF.Min(best, DistanceToLine(line, a));
            for (int j = 0; j + 1 < line.Count; j++)
            {
                best = MathF.Min(best, DistanceToSegment(a, b, line[j]));
                best = MathF.Min(best, SegmentDistance(a, b, line[j], line[j + 1]));
            }
        }
        return best;
    }

    // Zero when two segments meet; otherwise the distance between their nearest points.
    private static float SegmentDistance(GroundPoint a, GroundPoint b, GroundPoint c, GroundPoint d)
    {
        float d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return 0;
        return MathF.Min(
            MathF.Min(DistanceToSegment(c, d, a), DistanceToSegment(c, d, b)),
            MathF.Min(DistanceToSegment(a, b, c), DistanceToSegment(a, b, d)));
    }

    private static float Cross(GroundPoint a, GroundPoint b, GroundPoint c) =>
        (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);

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
    public void TheYardPair_StandsInTheParkingLot()
    {
        // The second pair works as one in Lot T: the master on a pole north-west of the doorway,
        // looking back along the line the person crosses, and the slave on a pole of its own between
        // that doorway and the master, nearer the master.
        var world = World();
        var master = world.Cameras.Single(c => c.Id == "master2");
        var slave = world.Cameras.Single(c => c.Id == "ptz2");

        Assert.Equal(-30.0, master.Position.X, 0);
        Assert.Equal(-112.0, master.Position.Z, 0);
        Assert.Equal(6f, master.Position.Y, 3);
        Assert.Equal(125f, master.Heading, 3);
        Assert.Equal(55f, master.HFov, 3);
        Assert.Equal(-9.5f, master.Pitch, 3);

        Assert.Equal(-20.0, slave.Position.X, 0);
        Assert.Equal(-90.0, slave.Position.Z, 0);
        Assert.Equal(5.5f, slave.Position.Y, 3);
        Assert.Equal(88f, slave.Heading, 3);
        Assert.Equal(60f, slave.Ptz!.FovWide, 3);
        // Off the master's line of sight to the door, so its own mast cannot hide the person.
        Assert.True(slave.Position.X > master.Position.X);
        Assert.True(slave.Position.Z > master.Position.Z);

        // The car is no longer what this pair follows: it drives round the block, far too small in the
        // master's picture, and the vehicle camera rides it.
        var vehicle = Assert.Single(world.Vehicles);
        Assert.Equal("v1", vehicle.Id);
        Assert.True(vehicle.Detectable);
        Assert.True(vehicle.Route.Count > 20);
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
        Assert.All(world.Wires, wire => Assert.Equal("master", wire.RelayCameraId));

        // A device on the car would move the wire, and every camera that sees it, every frame.
        Assert.Empty(world.Cameras.Single(c => c.Id == MarylandWorld.VehicleCameraId).DigitalInputs);
        Assert.Empty(world.Cameras.Single(c => c.Id == MarylandWorld.VehicleCameraId).RelayOutputs);
    }

    // The ground map is named, and the bounds it was fetched for are the extent this world covers:
    // a map is a cache, and what the world holds is where that cache belongs.
    [Fact]
    public void TheGroundMapLiesUnderTheWholeTerrain()
    {
        var world = World();
        Assert.Equal("maryland-ground", world.Terrain.Map);
        Assert.NotNull(world.Terrain.MapBounds);
        var bounds = TerrainMaps.BoundsOf(world.Geo, world.Terrain.Size);
        Assert.Equal(bounds.South, world.Terrain.MapBounds!.South, 6);
        Assert.Equal(bounds.West, world.Terrain.MapBounds.West, 6);
        Assert.Equal(bounds.North, world.Terrain.MapBounds.North, 6);
        Assert.Equal(bounds.East, world.Terrain.MapBounds.East, 6);
        Assert.False(string.IsNullOrWhiteSpace(world.Terrain.MapAttribution));
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
