using System.Numerics;
using Microsoft.Extensions.Time.Testing;
using OnvifSim.Analytics;
using OnvifSim.Rendering;
using OnvifSim.Streaming;
using OnvifSim.World;

namespace OnvifSim.Tests;

// A camera bolted to a moving car: where it is, what it sees, and that it does not see its own car.
public class VehicleCameraTests
{
    private static WorldModel FlatWorld()
    {
        var world = new WorldModel
        {
            Terrain = new TerrainModel { Size = 200, Resolution = 16, Heights = new float[17 * 17] },
        };
        world.Vehicles.Add(new VehicleModel
        {
            Id = "car", Speed = 10, Route = [new(0, 0), new(100, 0)],
        });
        world.Objects.Add(new WorldObject
        {
            Id = "marker", Type = "box", Position = new(30, 0, -20), Scale = new(5, 5, 5), Color = "#ff0000",
        });
        world.Cameras.Add(new CameraModel
        {
            Id = "chase", MountedOnVehicleId = "car", Position = new(0, 2, 0), Heading = 0, HFov = 70,
        });
        return world;
    }

    [Fact]
    public void ACameraOnACar_FollowsTheCar()
    {
        var world = FlatWorld();
        var camera = world.Cameras[0];
        var start = DateTimeOffset.UnixEpoch;
        var first = CameraPose.From(camera, world, start, default);
        var later = CameraPose.From(camera, world, start.AddSeconds(1), default);

        Assert.Equal(0, first.Position.X, 2);
        Assert.Equal(2, first.Position.Y, 2);
        Assert.Equal(10, later.Position.X, 2);
        Assert.Equal(90, later.Heading, 1);
    }

    [Fact]
    public void TheHostCar_IsNotDetectedByItsOwnCamera()
    {
        var world = FlatWorld();
        var snapshot = new SceneState.Snapshot(world, RenderScene.Build(world));
        var sightings = ObjectDetector.Detect(snapshot, world.Cameras[0], default, DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain(sightings, s => s.ObjectId == "car");
    }

    [Fact]
    public void ThePictureMoves_AsTheCarDrives()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        store.Replace(FlatWorld());
        var scene = new SceneState(store);
        var renderer = new CachingFrameRenderer(time);
        var camera = scene.Current.World.Cameras.Single(c => c.Id == "chase");

        var first = renderer.Render(scene.Current, camera, default);
        time.Advance(TimeSpan.FromSeconds(1));
        var later = renderer.Render(scene.Current, camera, default);

        Assert.NotEqual(first, later);
        Assert.Equal(2, renderer.RenderCount);
    }
}
