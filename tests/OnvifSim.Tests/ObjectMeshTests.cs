using OnvifSim.Geometry;
using OnvifSim.World;

namespace OnvifSim.Tests;

public class ObjectMeshTests
{
    // A type in ObjectTypes that builds nothing would be accepted by the editor and be invisible in every
    // picture, which is only ever a mistake.
    [Fact]
    public void EveryObjectType_BuildsAMesh()
    {
        foreach (var type in SceneBuilder.ObjectTypes)
        {
            var obj = new WorldObject { Type = type, Scale = new Vec3(4, 5, 4) };
            // A prism's size across comes from its footprint, not from Scale.
            if (type == "prism")
                obj.Points = Polygon.Normalize([new GroundPoint(0, 0), new GroundPoint(4, 0), new GroundPoint(4, 4), new GroundPoint(0, 4)]);
            Assert.True(SceneBuilder.ObjectMesh(obj).TriangleCount > 0, type);
        }
    }

    // A surface's grain and a floor's tiles come from a seed and a cell's place, never from a clock:
    // two builds of the same world must be drawn the same, or the world file would not be the truth.
    [Fact]
    public void ASurface_IsDrawnTheSameTwice()
    {
        var box = new WorldObject { Type = "box", Scale = new Vec3(7, 3.6f, 0.4f), Seed = 3 };
        Assert.Equal(SceneBuilder.ObjectMesh(box).Colors, SceneBuilder.ObjectMesh(box).Colors);
    }

    [Fact]
    public void AFloor_IsLaidOutInTiles()
    {
        var floor = new WorldObject { Type = "floor", Scale = new Vec3(30, 0.3f, 30), Color = "#8f959d", Color2 = "#6f747b" };
        var mesh = SceneBuilder.ObjectMesh(floor);
        // The slab's sides are one seam colour; the top is a tile each, and the gradient across the
        // slab and the shade of each tile make them differ — which is what tells it from a plain box.
        Assert.True(mesh.Colors.Distinct().Count() >= 10);
    }

    // A surface's pattern is optional: "none" draws it as one flat colour, whatever its type.
    [Fact]
    public void ASmoothSurface_HasNoPattern()
    {
        var grained = new WorldObject { Type = "box", Scale = new Vec3(6, 3, 6) };
        var smooth = new WorldObject { Type = "box", Scale = new Vec3(6, 3, 6), Texture = "none" };
        Assert.True(SceneBuilder.ObjectMesh(grained).Colors.Distinct().Count() > 1);
        Assert.Single(SceneBuilder.ObjectMesh(smooth).Colors.Distinct());
    }

    [Fact]
    public void AnUnknownTexture_IsRefused()
    {
        var world = DefaultWorld.Create();
        world.Objects.Add(new WorldObject { Id = "marble", Type = "box", Scale = new Vec3(1, 1, 1), Texture = "marble" });
        Assert.Throws<InvalidWorldException>(() => Editor.WorldValidation.Check(world, 0, 0));
    }

    [Fact]
    public void AnOffice_IsAcceptedInAWorld()
    {
        var world = DefaultWorld.Create();
        world.Objects.Add(new WorldObject { Id = "office", Type = "office", Scale = new Vec3(24, 9, 16), Seed = 1 });
        Editor.WorldValidation.Check(world, 0, 0);
    }
}
