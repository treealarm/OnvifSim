using OnvifSim.Geometry;
using OnvifSim.World;

namespace OnvifSim.Tests;

public class CarMeshTests
{
    private static Mesh Car(string? plate) => SceneBuilder.ObjectMesh(new WorldObject
    {
        Id = "car", Type = "car", Scale = new(1.8f, 1.45f, 4.4f), Plate = plate,
    });

    [Fact]
    public void ACar_HasABodyWheelsAndTwoPlates()
    {
        var mesh = Car("A123BC");

        Assert.True(mesh.TriangleCount > 100);
        var (_, radius) = mesh.BoundingSphere();
        Assert.InRange(radius, 1f, 6f);
        // The plates sit just beyond the body at both ends.
        Assert.Contains(mesh.Positions, p => p.Z < -2.2f);
        Assert.Contains(mesh.Positions, p => p.Z > 2.2f);
    }

    [Fact]
    public void MorePlateText_MeansMoreGeometry()
    {
        var blank = Car("");
        var full = Car("A123BC");

        Assert.True(full.VertexCount > blank.VertexCount);
    }

    [Fact]
    public void PlateText_IsUppercasedAndUnknownCharactersAreDropped()
    {
        Assert.Equal(Car("A1-2 B").VertexCount, Car("a1-2 b").VertexCount);
        Assert.Equal(Car("A12 B").VertexCount, Car("a12б b!").VertexCount);
    }

    [Fact]
    public void PlateText_IsCutAtEightCharacters()
    {
        Assert.Equal(Car("ABCDEFGH").VertexCount, Car("ABCDEFGHIJKL").VertexCount);
    }

    [Fact]
    public void TheDefaultPlate_IsDrawnWhenNoneIsGiven()
    {
        Assert.Equal(Car("A123BC").VertexCount, Car(null).VertexCount);
    }
}
