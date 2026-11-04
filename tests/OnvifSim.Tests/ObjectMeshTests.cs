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
            Assert.True(SceneBuilder.ObjectMesh(new WorldObject { Type = type, Scale = new Vec3(4, 5, 4) }).TriangleCount > 0, type);
    }

    [Fact]
    public void AnOffice_IsAcceptedInAWorld()
    {
        var world = DefaultWorld.Create();
        world.Objects.Add(new WorldObject { Id = "office", Type = "office", Scale = new Vec3(24, 9, 16), Seed = 1 });
        Editor.WorldValidation.Check(world, 0, 0);
    }
}
