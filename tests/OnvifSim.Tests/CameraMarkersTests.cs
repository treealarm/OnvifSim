using System.Numerics;
using Microsoft.Extensions.Time.Testing;
using OnvifSim.Geometry;
using OnvifSim.Ptz;
using OnvifSim.Rendering;
using OnvifSim.Streaming;
using OnvifSim.World;

namespace OnvifSim.Tests;

// Other cameras in a camera's picture: their bodies, the wire frame of where they look, and the lines
// that draw it.
public class CameraMarkersTests
{
    private static WorldModel FlatWorld()
    {
        var world = new WorldModel { Terrain = new TerrainModel { Size = 200, Resolution = 16, Heights = new float[17 * 17] } };
        world.Cameras.Add(new CameraModel { Id = "viewer", Position = new(0, 2, 0), HFov = 60 });
        return world;
    }

    private static Frame Render(RenderScene scene, CameraModel camera, IReadOnlyList<RenderScene.Chunk> moving)
    {
        var frame = new Frame(160, 90);
        new SoftwareRasterizer().Render(scene, moving, CameraPose.From(camera, scene.Terrain), frame);
        return frame;
    }

    private static Vector3 Pixel(Frame f, int x, int y)
    {
        int o = (y * f.Width + x) * 4;
        return new Vector3(f.Rgba[o], f.Rgba[o + 1], f.Rgba[o + 2]);
    }

    private static RenderScene.Chunk Line(Vector3 from, Vector3 to, Vector3 color)
    {
        var mesh = new Mesh();
        mesh.AddVertex(from, Vector3.UnitY, color);
        mesh.AddVertex(to, Vector3.UnitY, color);
        return RenderScene.Unlit(mesh, 1, [0, 1]);
    }

    [Fact]
    public void ALine_IsDrawnInFrontOfABox_AndHiddenBehindIt()
    {
        var world = FlatWorld();
        world.Objects.Add(new WorldObject { Id = "b", Type = "box", Position = new(0, 0, -20), Scale = new(4, 4, 4), Color = "#ff0000" });
        var scene = RenderScene.Build(world);
        var blue = new Vector3(0, 0, 1);
        // Across the picture at the camera's height, in front of the box and behind it.
        var before = Render(scene, world.Cameras[0], [Line(new(-3, 2, -10), new(3, 2, -10), blue)]);
        var behind = Render(scene, world.Cameras[0], [Line(new(-30, 2, -40), new(30, 2, -40), blue)]);
        var front = Pixel(before, 80, 45);
        Assert.True(front.Z > 200 && front.X < 60, $"the line in front shows over the box: {front}");
        var hidden = Pixel(behind, 80, 45);
        Assert.True(hidden.X > 150 && hidden.Z < 80, $"the box hides the line behind it: {hidden}");
    }

    [Fact]
    public void OtherCameras_AreDrawn_ButNeverTheCameraItself()
    {
        var world = FlatWorld();
        world.Cameras.Add(new CameraModel { Id = "other", Position = new(0, 2, -12), Heading = 180 });
        var scene = RenderScene.Build(world);
        var chunks = CameraMarkers.Build(scene, world, "viewer", _ => default);
        var body = Assert.Single(chunks);
        Assert.True(Vector3.Distance(body.Center, new Vector3(0, 2, -12)) < 3, "the other camera's body");

        // Its body, lens towards us, sits in the middle of the picture.
        var frame = Render(scene, world.Cameras[0], chunks);
        Assert.True(Enumerable.Range(70, 20).Any(x => Pixel(frame, x, 45) is var p && p.Z > p.X + 40 && p.Z > p.Y),
            "the fixed camera's blue body is in the picture");

        world.Environment.ShowCameras = false;
        Assert.Empty(CameraMarkers.Build(RenderScene.Build(world), world, "viewer", _ => default));
    }

    [Fact]
    public void TheFootprint_EndsOnTheGround_OrAtTheFrustumLength()
    {
        var world = FlatWorld();
        var scene = RenderScene.Build(world);
        // Level and 10 m up: the lower edge reaches the ground, the upper one never does.
        var pose = new CameraPose(new Vector3(0, 10, 0), 0, 0, 0, 60);
        float aspect = 16f / 9;
        var grid = ViewFootprint.Cast(pose, aspect, scene.Terrain, 600, 40);
        int columns = ViewFootprint.Columns, rows = ViewFootprint.Rows;
        for (int i = 0; i < columns; i++)
        {
            Assert.False(grid.OnGround[i, 0]);
            Assert.Equal(40, Vector3.Distance(grid.Eye, grid.Points[i, 0]), 3);
            Assert.True(grid.OnGround[i, rows - 1]);
            Assert.Equal(0, grid.Points[i, rows - 1].Y, 3);
        }
        // Straight ahead along the lower edge: 10 m down at tan(vfov / 2) below the horizon.
        float tanV = MathF.Tan(30 * MathF.PI / 180) / aspect;
        Assert.Equal(-10 / tanV, grid.Points[columns / 2, rows - 1].Z, 1);
    }

    [Fact]
    public void AnotherCamerasHeadTurning_RendersTheFrameAgain_UntilItStops()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(20000));
        var world = FlatWorld();
        world.Environment.ShowFrustums = true;
        world.Cameras.Add(new CameraModel { Id = "ptz", Kind = CameraKind.Ptz, Position = new(0, 6, -15), Ptz = new PtzModel() });
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        store.Replace(world);
        var scene = new SceneState(store);
        var head = new PtzState(new PtzPosition(150, -30, 0), true, false);
        var renderer = new CachingFrameRenderer(time, _ => head);
        var viewer = scene.Current.World.Cameras.Single(c => c.Id == "viewer");

        var first = renderer.Render(scene.Current, viewer, default);
        time.Advance(TimeSpan.FromMilliseconds(100));
        head = head with { Position = new PtzPosition(-150, -30, 0) };
        var turned = renderer.Render(scene.Current, viewer, default);
        Assert.NotEqual(first, turned);

        // Stopped where it was last drawn: one more frame, then the same one for as long as it rests.
        head = head with { PanTiltMoving = false };
        renderer.Render(scene.Current, viewer, default);
        time.Advance(TimeSpan.FromMilliseconds(100));
        renderer.Render(scene.Current, viewer, default);
        Assert.Equal(3, renderer.RenderCount);
    }
}
