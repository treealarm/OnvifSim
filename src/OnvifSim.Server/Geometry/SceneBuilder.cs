using System.Numerics;
using OnvifSim.World;

namespace OnvifSim.Geometry;

// One renderable piece of the world in world coordinates. Terrain is cut into chunks and every object
// is its own chunk, so cameras can skip whatever lies outside their view.
public sealed class SceneChunk
{
    public required string Name { get; init; }
    public required Mesh Mesh { get; init; }
    public Vector3 Center { get; init; }
    public float Radius { get; init; }
}

public static class SceneBuilder
{
    private const int TerrainChunkCells = 16;

    public static List<SceneChunk> Build(WorldModel world)
    {
        var terrain = new Terrain(world.Terrain);
        var chunks = new List<SceneChunk>();
        for (int cz = 0; cz < terrain.Resolution; cz += TerrainChunkCells)
            for (int cx = 0; cx < terrain.Resolution; cx += TerrainChunkCells)
                chunks.Add(Chunk($"terrain_{cx}_{cz}", TerrainChunk(terrain, cx, cz)));

        foreach (var obj in world.Objects)
        {
            var local = ObjectMesh(obj);
            if (local.VertexCount == 0) continue;
            var transform = Matrix4x4.CreateRotationY(-obj.RotationY * MathF.PI / 180) *
                            Matrix4x4.CreateTranslation(obj.Position.X, terrain.HeightAt(obj.Position.X, obj.Position.Z) + obj.Position.Y, obj.Position.Z);
            var mesh = new Mesh();
            mesh.Append(local, transform);
            chunks.Add(Chunk(obj.Id, mesh));
        }

        foreach (var road in world.Roads)
        {
            var mesh = RoadMesh(road, terrain);
            if (mesh.VertexCount > 0) chunks.Add(Chunk(road.Id, mesh));
        }
        return chunks;
    }

    // A walker at a pose, in world coordinates; drawn anew every frame, as walkers move.
    public static SceneChunk WalkerChunk(WalkerModel walker, WalkerPose pose, Terrain terrain)
    {
        var person = PersonMesh.Build(walker.Height, pose.StridePhase,
            ColorUtil.Parse(walker.Color, ColorUtil.Rgb(59, 110, 168)), ColorUtil.Parse(walker.Color2, ColorUtil.Rgb(58, 58, 68)));
        var mesh = new Mesh();
        mesh.Append(person, Matrix4x4.CreateRotationY(-pose.Heading * MathF.PI / 180) *
                            Matrix4x4.CreateTranslation(pose.X, terrain.HeightAt(pose.X, pose.Z), pose.Z));
        return Chunk(walker.Id, mesh);
    }

    // How far apart the strip of a road takes the ground's height, along it and across it, and how high
    // above the ground it lies so the terrain does not show through.
    private const float RoadSampleSpacing = 2;
    private const float RoadLift = 0.12f;

    private static Mesh RoadMesh(RoadModel road, Terrain terrain)
    {
        var mesh = new Mesh();
        var points = road.Points.Select(p => new Vector2(p.X, p.Z)).ToList();
        if (points.Count < 2) return mesh;
        var color = ColorUtil.Parse(road.Color, ColorUtil.Rgb(85, 87, 90));
        float half = road.Width / 2;
        int across = Math.Max(2, (int)MathF.Ceiling(road.Width / RoadSampleSpacing));
        int previousRow = -1;
        for (int i = 0; i < points.Count - 1; i++)
        {
            var (a, b) = (points[i], points[i + 1]);
            float length = Vector2.Distance(a, b);
            if (length == 0) continue;
            int steps = Math.Max(1, (int)MathF.Ceiling(length / RoadSampleSpacing));
            // The last row of a segment is the first of the next one, turned halfway between the two.
            for (int s = previousRow < 0 ? 0 : 1; s <= steps; s++)
            {
                float t = (float)s / steps;
                var centre = Vector2.Lerp(a, b, t);
                var side = Side(points, i, s == steps ? i + 1 : -1);
                int row = mesh.VertexCount;
                for (int k = 0; k <= across; k++)
                {
                    var at = centre + side * (half * (2f * k / across - 1));
                    mesh.AddVertex(new Vector3(at.X, terrain.HeightAt(at.X, at.Y) + RoadLift, at.Y), Vector3.UnitY, color);
                }
                if (previousRow >= 0)
                    for (int k = 0; k < across; k++)
                    {
                        // Counter-clockwise seen from above whichever way the road runs.
                        int p0 = previousRow + k, p1 = p0 + 1, q0 = row + k, q1 = q0 + 1;
                        AddUpward(mesh, p0, p1, q0);
                        AddUpward(mesh, p1, q1, q0);
                    }
                previousRow = row;
            }
        }
        return mesh;
    }

    // The unit vector pointing across the road (to the right of travel) on segment i, or at a joint the
    // average of the segments meeting there.
    private static Vector2 Side(List<Vector2> points, int segment, int joint)
    {
        static Vector2 Across(Vector2 from, Vector2 to)
        {
            var d = Vector2.Normalize(to - from);
            return new Vector2(-d.Y, d.X);
        }
        var side = Across(points[segment], points[segment + 1]);
        if (joint > 0 && joint < points.Count - 1 && points[joint + 1] != points[joint])
        {
            var averaged = side + Across(points[joint], points[joint + 1]);
            if (averaged.LengthSquared() > 1e-6f) side = Vector2.Normalize(averaged);
        }
        return side;
    }

    private static void AddUpward(Mesh mesh, int a, int b, int c)
    {
        var (pa, pb, pc) = (mesh.Positions[a], mesh.Positions[b], mesh.Positions[c]);
        if (Vector3.Cross(pb - pa, pc - pa).Y >= 0) mesh.AddTriangle(a, b, c);
        else mesh.AddTriangle(a, c, b);
    }

    private static SceneChunk Chunk(string name, Mesh mesh)
    {
        var (c, r) = mesh.BoundingSphere();
        return new SceneChunk { Name = name, Mesh = mesh, Center = c, Radius = r };
    }

    private static readonly Vector3 Grass = ColorUtil.Rgb(92, 128, 60);
    private static readonly Vector3 DryGrass = ColorUtil.Rgb(128, 136, 74);
    private static readonly Vector3 Rock = ColorUtil.Rgb(118, 112, 104);

    private static Mesh TerrainChunk(Terrain t, int cx0, int cz0)
    {
        var m = new Mesh();
        int cx1 = Math.Min(cx0 + TerrainChunkCells, t.Resolution), cz1 = Math.Min(cz0 + TerrainChunkCells, t.Resolution);
        int stride = cx1 - cx0 + 1;
        for (int iz = cz0; iz <= cz1; iz++)
            for (int ix = cx0; ix <= cx1; ix++)
            {
                var n = t.NormalAt(ix, iz);
                // Steep slopes turn to rock; a cheap hash breaks up the grass so distance is readable.
                float noise = Hash(ix, iz);
                var grass = Vector3.Lerp(Grass, DryGrass, noise * 0.6f);
                float rockiness = Math.Clamp((0.92f - n.Y) * 6, 0, 1);
                m.AddVertex(new(t.VertexX(ix), t[ix, iz], t.VertexZ(iz)), n, Vector3.Lerp(grass, Rock, rockiness));
            }
        for (int iz = 0; iz < cz1 - cz0; iz++)
            for (int ix = 0; ix < cx1 - cx0; ix++)
            {
                int a = iz * stride + ix, b = a + 1, c = a + stride, d = c + 1;
                // Counter-clockwise seen from above (+Y): a(-x,-z) c(-x,+z) b(+x,-z).
                m.AddTriangle(a, c, b);
                m.AddTriangle(b, c, d);
            }
        return m;
    }

    private static float Hash(int x, int z)
    {
        uint h = (uint)(x * 374761393 + z * 668265263);
        h = (h ^ (h >> 13)) * 1274126177;
        return ((h ^ (h >> 16)) & 0xffff) / 65535f;
    }

    // The object types ObjectMesh knows.
    public static readonly string[] ObjectTypes = ["tree.pine", "tree.oak", "house", "rock", "box"];

    public static Mesh ObjectMesh(WorldObject o)
    {
        var m = new Mesh();
        var s = o.Scale;
        var rng = new Random(o.Seed);
        float Vary(float amount) => 1 + amount * ((float)rng.NextDouble() * 2 - 1);
        switch (o.Type)
        {
            case "tree.pine":
            {
                var trunk = ColorUtil.Parse(o.Color2, ColorUtil.Rgb(92, 64, 44));
                var leaves = ColorUtil.Parse(o.Color, ColorUtil.Rgb(38, 82, 48)) * Vary(0.12f);
                float h = s.Y, r = s.X / 2;
                Primitives.Frustum(m, r * 0.16f, r * 0.12f, h * 0.3f, 6, trunk);
                Primitives.Frustum(m, r, 0, h * 0.45f, 8, leaves, h * 0.18f);
                Primitives.Frustum(m, r * 0.78f, 0, h * 0.4f, 8, leaves * 1.08f, h * 0.4f);
                Primitives.Frustum(m, r * 0.52f, 0, h * 0.35f, 8, leaves * 1.16f, h * 0.65f);
                break;
            }
            case "tree.oak":
            {
                var trunk = ColorUtil.Parse(o.Color2, ColorUtil.Rgb(98, 72, 50));
                var leaves = ColorUtil.Parse(o.Color, ColorUtil.Rgb(70, 110, 46)) * Vary(0.12f);
                float h = s.Y;
                Primitives.Frustum(m, s.X * 0.08f, s.X * 0.06f, h * 0.5f, 6, trunk);
                Primitives.Blob(m, new(s.X / 2, h * 0.32f, s.Z / 2), new(0, h * 0.66f, 0), 0.18f, o.Seed, leaves, new(0.04f, 0.05f, 0.03f));
                break;
            }
            case "house":
            {
                var wall = ColorUtil.Parse(o.Color, ColorUtil.Rgb(216, 201, 168));
                var roof = ColorUtil.Parse(o.Color2, ColorUtil.Rgb(138, 59, 46));
                var dark = ColorUtil.Rgb(60, 52, 48);
                var glass = ColorUtil.Rgb(58, 72, 88);
                // Walls reach 2 m below the base so a house on a slope never floats.
                Primitives.Box(m, s.X, s.Y + 2, s.Z, wall, -2);
                Primitives.GableRoof(m, s.X, s.Z, s.Y, s.X * 0.38f, 0.5f, roof, wall);
                var door = new Mesh();
                Primitives.Box(door, 1.1f, 2.1f, 0.12f, dark);
                m.Append(door, Matrix4x4.CreateTranslation(0, 0, s.Z / 2));
                for (int i = 0; i < 2; i++)
                {
                    float z = (i == 0 ? -1 : 1) * s.Z / 4;
                    var win = new Mesh();
                    Primitives.Box(win, 0.12f, 1.2f, 1.4f, glass);
                    m.Append(win, Matrix4x4.CreateTranslation(s.X / 2, s.Y * 0.45f, z));
                    m.Append(win, Matrix4x4.CreateTranslation(-s.X / 2, s.Y * 0.45f, z));
                }
                break;
            }
            case "rock":
            {
                var c = ColorUtil.Parse(o.Color, ColorUtil.Rgb(128, 124, 118));
                Primitives.Blob(m, new(s.X / 2, s.Y / 2, s.Z / 2), new(0, s.Y * 0.25f, 0), 0.25f, o.Seed, c, new(0.04f, 0.04f, 0.04f));
                break;
            }
            case "box":
                Primitives.Box(m, s.X, s.Y, s.Z, ColorUtil.Parse(o.Color, ColorUtil.Rgb(200, 200, 200)));
                break;
        }
        return m;
    }
}
