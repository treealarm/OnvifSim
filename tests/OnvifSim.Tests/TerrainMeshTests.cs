using OnvifSim.Geometry;
using OnvifSim.World;

namespace OnvifSim.Tests;

public class TerrainMeshTests
{
    // The ground takes the world's colour; the straw patches are that colour shifted, never a fixed green.
    [Fact]
    public void TerrainVertices_TakeTheWorldsColour()
    {
        var world = new WorldModel { Terrain = new TerrainModel { Size = 10, Resolution = 1, Color = "#ff0000" } };
        var mesh = SceneBuilder.Build(world).Single(chunk => chunk.Name == "terrain_0_0").Mesh;

        Assert.NotEmpty(mesh.Colors);
        Assert.All(mesh.Colors, color => Assert.True(color.X > color.Y && color.X > color.Z, $"red ground, got {color}"));
    }
}
