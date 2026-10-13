using System.Numerics;
using System.Security.Cryptography;
using OnvifSim.Rendering;
using OnvifSim.World;

namespace OnvifSim.Tests;

public class RasterizerTests
{
    // Flat ground and one red box north of a camera standing at the origin, 2 m up.
    private static WorldModel BoxWorld(float boxDistance = 20)
    {
        var world = new WorldModel { Terrain = new TerrainModel { Size = 200, Resolution = 16 } };
        world.Terrain.Heights = new float[17 * 17];
        world.Objects.Add(new WorldObject { Id = "b", Type = "box", Position = new(0, 0, -boxDistance), Scale = new(4, 4, 4), Color = "#ff0000" });
        world.Cameras.Add(new CameraModel { Id = "c", Position = new(0, 2, 0), HFov = 60 });
        return world;
    }

    private static Frame Render(WorldModel world, CameraPose? pose = null, int w = 160, int h = 90)
    {
        var scene = RenderScene.Build(world);
        var frame = new Frame(w, h);
        new SoftwareRasterizer().Render(scene, pose ?? CameraPose.From(world.Cameras[0], scene.Terrain), frame);
        return frame;
    }

    private static (byte R, byte G, byte B) Pixel(Frame f, int x, int y)
    {
        int o = (y * f.Width + x) * 4;
        return (f.Rgba[o], f.Rgba[o + 1], f.Rgba[o + 2]);
    }

    private static bool IsRed((byte R, byte G, byte B) p) => p.R > 150 && p.G < 80 && p.B < 80;

    [Fact]
    public void BoxAhead_IsDrawnInTheMiddle_SkyAbove_GroundBelow()
    {
        var f = Render(BoxWorld());
        Assert.True(IsRed(Pixel(f, 80, 45)), $"centre {Pixel(f, 80, 45)}");
        var top = Pixel(f, 80, 2);
        Assert.True(top.B > top.R && top.B > 150, $"sky {top}");
        var bottom = Pixel(f, 80, 88);
        Assert.True(bottom.G > bottom.R && bottom.G > bottom.B, $"ground {bottom}");
    }

    [Fact]
    public void TurningAround_LosesTheBox()
    {
        var world = BoxWorld();
        world.Cameras[0].Heading = 180;
        var f = Render(world);
        for (int x = 0; x < f.Width; x++)
            Assert.False(IsRed(Pixel(f, x, 45)), $"red at x={x}");
    }

    [Fact]
    public void Output_IsDeterministic()
    {
        Assert.Equal(SHA256.HashData(Render(BoxWorld()).Rgba), SHA256.HashData(Render(BoxWorld()).Rgba));
    }

    [Fact]
    public void BeyondFogEnd_TheBoxIsGone()
    {
        Assert.False(IsRed(Pixel(Render(BoxWorld(boxDistance: 900)), 80, 45)));
    }

    [Theory]
    [InlineData(-90f)]
    [InlineData(90f)]
    public void LookingStraightUpOrDown_Works(float pitch)
    {
        var f = Render(BoxWorld(), new CameraPose(new Vector3(0, 2, 0), 0, pitch, 0, 60));
        var p = Pixel(f, 80, 45);
        if (pitch < 0)
            Assert.True(p.G > p.B, $"ground {p}");
        else
            Assert.True(p.B > p.G, $"sky {p}");
    }

    [Fact]
    public void FaceCrossingTheNearPlane_IsClippedNotDropped()
    {
        // Camera 1 m in front of the box's south face, looking at it with a wide lens: the face spans
        // far past the screen edges and the ground triangles under the camera cross the near plane.
        var f = Render(BoxWorld(), new CameraPose(new Vector3(0, 2, -17), 0, 0, 0, 100));
        Assert.True(IsRed(Pixel(f, 80, 45)), $"centre {Pixel(f, 80, 45)}");
        Assert.True(IsRed(Pixel(f, 2, 45)) && IsRed(Pixel(f, 157, 45)), "face should fill the width");
    }

    [Fact]
    public void Roll_TiltsTheHorizon()
    {
        var world = BoxWorld();
        world.Objects.Clear();
        var level = Render(world, new CameraPose(new Vector3(0, 2, 0), 0, 0, 0, 60));
        var rolled = Render(world, new CameraPose(new Vector3(0, 2, 0), 0, 0, 20, 60));
        static bool SkyAt(Frame f, int x, int y) { var p = Pixel(f, x, y); return p.B > p.G; }
        int levelDiffer = 0, rolledDiffer = 0;
        for (int y = 20; y < 70; y++)
        {
            if (SkyAt(level, 2, y) != SkyAt(level, 157, y)) levelDiffer++;
            if (SkyAt(rolled, 2, y) != SkyAt(rolled, 157, y)) rolledDiffer++;
        }
        Assert.True(levelDiffer <= 1, $"level rows differing {levelDiffer}");
        Assert.True(rolledDiffer > 10, $"rolled rows differing {rolledDiffer}");
    }

    [Fact]
    public void PtzZoom_IsGeometricInFocalLength()
    {
        Assert.Equal(60f, CameraPose.ZoomToHFov(0, 60, 3), 3);
        Assert.Equal(3f, CameraPose.ZoomToHFov(1, 60, 3), 3);
        static float F(float fov) => 1 / MathF.Tan(fov * MathF.PI / 360);
        Assert.Equal(MathF.Sqrt(F(60) * F(3)), F(CameraPose.ZoomToHFov(0.5f, 60, 3)), 2);
    }
}
