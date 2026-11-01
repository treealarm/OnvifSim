using OnvifSim.Geometry;
using OnvifSim.World;

namespace OnvifSim.Tests;

public class RoadMeshTests
{
    // A closed road lays one more segment, from the last point back to the first.
    [Fact]
    public void AClosedRoad_HasTheSegmentThatClosesIt()
    {
        static int Vertices(bool closed)
        {
            var world = new WorldModel
            {
                Roads = [new RoadModel { Id = "r", Points = [new(0, 0), new(10, 0), new(10, 10)], Width = 2, Closed = closed }],
            };
            return SceneBuilder.Build(world).Single(chunk => chunk.Name == "r").Mesh.VertexCount;
        }

        Assert.True(Vertices(closed: true) > Vertices(closed: false));
    }
}
