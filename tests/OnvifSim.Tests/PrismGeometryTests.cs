using OnvifSim.Geometry;
using OnvifSim.World;

namespace OnvifSim.Tests;

// Footprints and the prism built from them: what an outline has to be before it is a polygon at all,
// and what the mesh builder makes of it.
public class PrismGeometryTests
{
    private static List<GroundPoint> Ring(params (float X, float Z)[] points) =>
        points.Select(p => new GroundPoint(p.X, p.Z)).ToList();

    // The stored direction: counter-clockwise in the ground plane as the renderer writes it, so the
    // walls face out and the roof faces up.
    private static List<GroundPoint> Square(float half = 5) =>
        Ring((-half, -half), (-half, half), (half, half), (half, -half));

    [Fact]
    public void TheOtherDirection_IsFlippedToTheStoredOne()
    {
        var other = Square();
        other.Reverse();
        Assert.True(Polygon.SignedArea(other) > 0);
        Assert.Equal(Square(), Polygon.Normalize(other));
    }

    [Fact]
    public void RepeatedAndCollinearPoints_AreDropped()
    {
        var ring = Ring((0, 0), (0, 0.005f), (10, 0), (20, 0), (20, 10), (10, 10), (0, 10), (0, 10.004f));
        var normal = Polygon.Normalize(ring);
        // The near-duplicates go, and the points halfway along the two long edges lie on a straight line.
        Assert.Equal(4, normal.Count);
        Assert.Equal(200, MathF.Abs(Polygon.SignedArea(normal)), 1);
    }

    [Fact]
    public void ARingReturnedToItsFirstPoint_IsTheSameRing()
    {
        var closed = Ring((0, 0), (10, 0), (10, 10), (0, 10), (0, 0));
        Assert.Equal(4, Polygon.Normalize(closed).Count);
    }

    // A bow tie has no inside: it is refused rather than triangulated into something arbitrary.
    [Fact]
    public void ASelfCrossingRing_IsNotSimple()
    {
        var bowTie = Ring((0, 0), (10, 10), (10, 0), (0, 10));
        Assert.False(Polygon.IsSimple(bowTie));
        Assert.True(Polygon.IsSimple(Square()));
    }

    [Fact]
    public void AConcaveFootprint_IsTriangulatedIntoItsWholeArea()
    {
        // An L: the notch must not be covered by a triangle.
        var l = Polygon.Normalize(Ring((0, 0), (10, 0), (10, 4), (4, 4), (4, 10), (0, 10)));
        var triangles = Polygon.Triangulate(l);
        Assert.Equal((l.Count - 2) * 3, triangles.Count);

        float area = 0;
        for (int i = 0; i + 2 < triangles.Count; i += 3)
            area += MathF.Abs(Polygon.Cross(l[triangles[i]], l[triangles[i + 1]], l[triangles[i + 2]])) / 2;
        // 10×4 plus 4×6 = 64 m², whatever the shape of the triangles.
        Assert.Equal(64, area, 1);
    }

    [Fact]
    public void APointInsideTheFootprint_IsFound()
    {
        var ring = Polygon.Normalize(Ring((0, 0), (10, 0), (10, 4), (4, 4), (4, 10), (0, 10)));
        Assert.True(Polygon.Contains(ring, new GroundPoint(2, 2)));
        Assert.False(Polygon.Contains(ring, new GroundPoint(8, 8)));   // the notch
        Assert.True(Polygon.Contains(ring, new GroundPoint(0, 5)));    // on an edge
    }

    [Fact]
    public void APrism_IsWallsAndTwoCapsFromBothSides()
    {
        var prism = new WorldObject
        {
            Id = "b", Type = "prism", Scale = new Vec3(1, 6, 1), Texture = "none",
            Points = Square(),
        };
        var mesh = SceneBuilder.ObjectMesh(prism);
        // Four walls, each drawn outside and inside (8 quads), plus roof and floor from both sides
        // (2 triangles each way, twice).
        Assert.Equal(8 * 2 + 2 * 4, mesh.TriangleCount);
    }

    // The opening cuts a hole: the wall it sits on becomes two panels beside it and one above.
    [Fact]
    public void AnOpening_CutsItsWall()
    {
        var prism = new WorldObject
        {
            Id = "b", Type = "prism", Scale = new Vec3(1, 6, 1), Texture = "none",
            Points = Square(),
            Openings = [new OpeningModel { Edge = 0, Offset = 4, Width = 2, Height = 2.2f }],
        };
        // The plain prism's 8 wall quads become 12: the extra two, outside and in.
        Assert.Equal((8 + 4) * 2 + 2 * 4, SceneBuilder.ObjectMesh(prism).TriangleCount);
    }

    // A parking apron carries its bays: white dividers along the long edges, every 2.5 m, five metres
    // in — and none of them runs off the apron, so a lot of any shape keeps its own outline.
    [Fact]
    public void AParkingApron_IsPaintedWithItsBays()
    {
        var ring = Polygon.Normalize(Ring((0, 0), (40, 0), (40, 20), (0, 20)));
        var apron = new WorldObject { Id = "lot", Type = "prism", Scale = new Vec3(1, 0.04f, 1), Texture = "parking", Points = ring };
        var plain = new WorldObject { Id = "lot", Type = "prism", Scale = new Vec3(1, 0.04f, 1), Texture = "none", Points = ring };

        var painted = SceneBuilder.ObjectMesh(apron);
        var bare = SceneBuilder.ObjectMesh(plain);
        Assert.True(painted.TriangleCount > bare.TriangleCount, "the apron is drawn with no bays at all");

        // Every painted corner lies on the apron, a hair above its top: a bay that reached past the
        // outline would be a line drawn on the grass.
        var paint = new HashSet<(float, float)>();
        for (int i = 0; i < painted.Positions.Count; i++)
        {
            var p = painted.Positions[i];
            if (p.Y <= 0.04f + 0.001f || p.Y > 0.06f) continue;
            paint.Add((MathF.Round(p.X, 2), MathF.Round(p.Z, 2)));
            Assert.True(Polygon.Contains(ring, new GroundPoint(p.X, p.Z)),
                $"a bay line reaches ({p.X:0.0}, {p.Z:0.0}), which is off the apron");
        }
        Assert.NotEmpty(paint);

        // The short ends carry no rows: the bays stand along the two 40 m edges, five metres deep.
        Assert.All(paint, corner => Assert.InRange(corner.Item1, 0, 40));
        Assert.All(paint, corner => Assert.InRange(corner.Item2, 0, 20));
        Assert.Contains(paint, corner => corner.Item2 < 6f);
        Assert.Contains(paint, corner => corner.Item2 > 14f);
    }

    [Fact]
    public void APrism_IsDrawnTheSameTwice()
    {
        var prism = new WorldObject
        {
            Id = "b", Type = "prism", Scale = new Vec3(1, 11.78f, 1), Seed = 7,
            Points = Polygon.Normalize(Ring((0, 0), (30, 0), (30, 10), (10, 10), (10, 20), (0, 20))),
        };
        Assert.Equal(SceneBuilder.ObjectMesh(prism).Colors, SceneBuilder.ObjectMesh(prism).Colors);
    }

    [Fact]
    public void APrism_IsAcceptedInAWorld()
    {
        var world = DefaultWorld.Create();
        world.Objects.Add(new WorldObject
        {
            Id = "building", Type = "prism", Scale = new Vec3(1, 12, 1), Seed = 1,
            Points = Polygon.Normalize(Ring((0, 0), (40, 0), (40, 20), (0, 20))),
            Openings = [new OpeningModel { Edge = 0, Offset = 18, Width = 1.8f, Height = 2.2f }],
        });
        Editor.WorldValidation.Check(world, 0, 0);
    }

    // Each of these is refused where a world is written: a footprint that is not normalised, one that
    // crosses itself, one too small, a stretched scale, and openings that do not fit.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ABadPrism_IsRefused(int variant)
    {
        var world = DefaultWorld.Create();
        var prism = new WorldObject
        {
            Id = "building", Type = "prism", Scale = new Vec3(1, 12, 1),
            Points = Polygon.Normalize(Ring((0, 0), (40, 0), (40, 20), (0, 20))),
        };
        switch (variant)
        {
            case 0:
                prism.Points!.Reverse();
                break;
            case 1:
                prism.Points = Ring((0, 0), (40, 20), (40, 0), (0, 20));
                break;
            case 2:
                prism.Points = Polygon.Normalize(Ring((0, 0), (0.5f, 0), (0.5f, 0.5f), (0, 0.5f)));
                break;
            case 3:
                prism.Scale = new Vec3(2, 12, 1);
                break;
            case 4:
                prism.Openings = [new OpeningModel { Edge = 0, Offset = 39, Width = 3, Height = 2 }];
                break;
            case 5:
                prism.Openings = [new OpeningModel { Edge = 0, Offset = 10, Width = 2, Height = 20 }];
                break;
        }
        world.Objects.Add(prism);
        Assert.Throws<InvalidWorldException>(() => Editor.WorldValidation.Check(world, 0, 0));
    }
}
