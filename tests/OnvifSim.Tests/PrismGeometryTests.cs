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

    // A footprint with a courtyard is a footprint like any other: the walls of the hole are drawn, the
    // cap stops at it, and the outline minus the hole is what the building covers.
    [Fact]
    public void ACourtyard_IsLeftOpenInTheCap()
    {
        var (outline, hole) = CourtyardFootprint();
        var triangles = Polygon.Triangulate(outline, [hole]);
        Assert.Equal(0, triangles.Count % 3);
        Assert.NotEmpty(triangles);

        float area = 0;
        for (int i = 0; i + 2 < triangles.Count; i += 3)
        {
            area += MathF.Abs(Polygon.Cross(triangles[i], triangles[i + 1], triangles[i + 2])) / 2;
            var triangle = new[] { triangles[i], triangles[i + 1], triangles[i + 2] };
            Assert.False(Polygon.Contains(triangle, new GroundPoint(15, 15)), "a triangle covers the courtyard");
        }
        // 60 x 60 less the 10 x 10 court, whatever the shape of the triangles.
        Assert.Equal(60 * 60 - 10 * 10, area, 1);
    }

    // Two courtyards joined one after the other: the second bridge runs to a ring that already carries
    // the first hole, and neither court is covered.
    [Fact]
    public void TwoCourtyards_AreBothLeftOpen()
    {
        var outline = Polygon.Normalize(Ring((-30, -30), (-30, 30), (30, 30), (30, -30)));
        var first = Polygon.NormalizeHole(Ring((-25, -25), (-15, -25), (-15, -15), (-25, -15)));
        var second = Polygon.NormalizeHole(Ring((15, 15), (25, 15), (25, 25), (15, 25)));
        var triangles = Polygon.Triangulate(outline, [first, second]);

        float area = 0;
        for (int i = 0; i + 2 < triangles.Count; i += 3)
        {
            area += MathF.Abs(Polygon.Cross(triangles[i], triangles[i + 1], triangles[i + 2])) / 2;
            var triangle = new[] { triangles[i], triangles[i + 1], triangles[i + 2] };
            Assert.False(Polygon.Contains(triangle, new GroundPoint(-20, -20)), "a triangle covers the first courtyard");
            Assert.False(Polygon.Contains(triangle, new GroundPoint(20, 20)), "a triangle covers the second courtyard");
        }
        Assert.Equal(60 * 60 - 2 * 10 * 10, area, 1);
    }

    // The lighter face of a courtyard wall is the one looking into the court, as the lighter face of an
    // outer wall looks out of the building. The hole is stored the other way round for exactly this.
    [Fact]
    public void ACourtyard_WallsFaceIntoIt()
    {
        var (outline, hole) = CourtyardFootprint();
        var prism = new WorldObject
        {
            Id = "b", Type = "prism", Scale = new Vec3(1, 6, 1), Texture = "none", Color = "#ffffff",
            Points = outline, Holes = [hole],
        };
        var mesh = SceneBuilder.ObjectMesh(prism);
        var bright = new List<(System.Numerics.Vector3 Position, System.Numerics.Vector3 Normal)>();
        for (int i = 0; i < mesh.Positions.Count; i++)
        {
            if (mesh.Colors[i].X + mesh.Colors[i].Y + mesh.Colors[i].Z < 2.9f) continue; // the wall's own colour
            if (MathF.Abs(mesh.Positions[i].Y - 6) > 1e-3f) continue;
            bright.Add((mesh.Positions[i], mesh.Normals[i]));
        }
        Assert.NotEmpty(bright);

        // z grows south: the court at 10..20 has its north wall looking towards +Z, its south wall
        // towards -Z, and its west and east walls towards +X and -X.
        static bool OnNorth(System.Numerics.Vector3 p) => MathF.Abs(p.Z - 10) <= 1e-3f && p.X is >= 10 - 1e-3f and <= 20 + 1e-3f;
        static bool OnSouth(System.Numerics.Vector3 p) => MathF.Abs(p.Z - 20) <= 1e-3f && p.X is >= 10 - 1e-3f and <= 20 + 1e-3f;
        static bool OnWest(System.Numerics.Vector3 p) => MathF.Abs(p.X - 10) <= 1e-3f && p.Z is >= 10 - 1e-3f and <= 20 + 1e-3f;
        static bool OnEast(System.Numerics.Vector3 p) => MathF.Abs(p.X - 20) <= 1e-3f && p.Z is >= 10 - 1e-3f and <= 20 + 1e-3f;
        bool Faces(Func<System.Numerics.Vector3, bool> onWall, System.Numerics.Vector3 into) =>
            bright.Any(f => onWall(f.Position) && System.Numerics.Vector3.Dot(f.Normal, into) > 0.9f);
        Assert.True(Faces(OnNorth, new(0, 0, 1)), "the court's north wall does not face into it");
        Assert.True(Faces(OnSouth, new(0, 0, -1)), "the court's south wall does not face into it");
        Assert.True(Faces(OnWest, new(1, 0, 0)), "the court's west wall does not face into it");
        Assert.True(Faces(OnEast, new(-1, 0, 0)), "the court's east wall does not face into it");
    }

    [Fact]
    public void APrismWithACourtyard_IsAcceptedInAWorld()
    {
        var world = DefaultWorld.Create();
        var (outline, hole) = CourtyardFootprint();
        world.Objects.Add(new WorldObject
        {
            Id = "courtyard", Type = "prism", Scale = new Vec3(1, 12, 1), Seed = 1,
            Points = outline, Holes = [hole],
        });
        Editor.WorldValidation.Check(world, 0, 0);
    }

    // A 60 x 60 outline with a 10 x 10 court inside it: the court is 10..20 in both x and z, so its
    // north wall is the edge at z = 10 and its south wall the edge at z = 20.
    private static (List<GroundPoint> Outline, List<GroundPoint> Hole) CourtyardFootprint()
    {
        var outline = Polygon.Normalize(Ring((-30, -30), (-30, 30), (30, 30), (30, -30)));
        var hole = Polygon.NormalizeHole(Ring((10, 10), (20, 10), (20, 20), (10, 20)));
        return (outline, hole);
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
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
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
            case 6:
                // A hole stored the same way round as the outline: its walls would face the material.
                prism.Holes = [Polygon.Normalize(Ring((10, 5), (20, 5), (20, 15), (10, 15)))];
                break;
            case 7:
                // A hole outside the outline.
                prism.Holes = [Polygon.NormalizeHole(Ring((50, 5), (60, 5), (60, 15), (50, 15)))];
                break;
            case 8:
                // A hole touching the outline's wall: no wall left between them.
                prism.Holes = [Polygon.NormalizeHole(Ring((0, 5), (10, 5), (10, 15), (0, 15)))];
                break;
            case 9:
                // Two holes crossing.
                prism.Holes =
                [
                    Polygon.NormalizeHole(Ring((10, 5), (25, 5), (25, 15), (10, 15))),
                    Polygon.NormalizeHole(Ring((20, 5), (35, 5), (35, 15), (20, 15))),
                ];
                break;
            case 10:
                // A hole inside another hole: an island of material with no wall of its own.
                prism.Holes =
                [
                    Polygon.NormalizeHole(Ring((5, 2), (35, 2), (35, 18), (5, 18))),
                    Polygon.NormalizeHole(Ring((15, 7), (25, 7), (25, 13), (15, 13))),
                ];
                break;
            case 11:
                // A hole so nearly the whole footprint that almost no material is left.
                prism.Holes = [Polygon.NormalizeHole(Ring((0.005f, 0.005f), (39.995f, 0.005f), (39.995f, 19.995f), (0.005f, 19.995f)))];
                break;
            case 12:
                // A hole with a collinear point: not the normalised ring the renderer expects.
                prism.Holes = [Ring((10, 5), (15, 5), (20, 5), (20, 15), (10, 15))];
                break;
        }
        world.Objects.Add(prism);
        Assert.Throws<InvalidWorldException>(() => Editor.WorldValidation.Check(world, 0, 0));
    }
}
