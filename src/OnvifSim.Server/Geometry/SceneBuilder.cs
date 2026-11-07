using System.Numerics;
using OnvifSim.Rendering;
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
    // A picture the mesh's vertices are placed in (the ground map under the terrain), drawn as it is:
    // lighting a map would only make it harder to read.
    public Texture? Texture { get; init; }
}

public static class SceneBuilder
{
    private const int TerrainChunkCells = 16;

    // groundMap, when given, is drawn under the terrain: every terrain vertex carries its place in the
    // picture, and arcs from the world's extent to the picture's edges.
    public static List<SceneChunk> Build(WorldModel world, Texture? groundMap = null)
    {
        var terrain = new Terrain(world.Terrain);
        var chunks = new List<SceneChunk>();
        for (int cz = 0; cz < terrain.Resolution; cz += TerrainChunkCells)
            for (int cx = 0; cx < terrain.Resolution; cx += TerrainChunkCells)
                chunks.Add(Chunk($"terrain_{cx}_{cz}", TerrainChunk(terrain, world.Terrain.Color, world.Terrain.Texture, cx, cz, groundMap),
                    groundMap));

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

    // A moving car at a pose, in world coordinates; drawn anew every frame, as it drives. The car faces
    // -Z at heading 0, so the rotation turns it towards the way its route goes.
    public static SceneChunk VehicleChunk(VehicleModel vehicle, RoutePose pose, Terrain terrain)
    {
        var car = CarMesh.Build(vehicle.Width, vehicle.Height, vehicle.Length,
            ColorUtil.Parse(vehicle.Color, ColorUtil.Rgb(58, 110, 165)),
            ColorUtil.Parse(vehicle.Color2, ColorUtil.Rgb(240, 240, 234)),
            vehicle.Plate);
        var mesh = new Mesh();
        mesh.Append(car, Matrix4x4.CreateRotationY(-pose.Heading * MathF.PI / 180) *
                            Matrix4x4.CreateTranslation(pose.X, terrain.HeightAt(pose.X, pose.Z), pose.Z));
        return Chunk(vehicle.Id, mesh);
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
        // A road told to be smooth is one flat grey; otherwise its edges are darker than its crown
        // and the surface shifts a little from metre to metre, so it reads as asphalt.
        bool textured = !Smooth(road.Texture);
        float half = road.Width / 2;
        int across = Math.Max(2, (int)MathF.Ceiling(road.Width / RoadSampleSpacing));
        int segments = road.Closed ? points.Count : points.Count - 1;
        int previousRow = -1;
        for (int i = 0; i < segments; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            float length = Vector2.Distance(a, b);
            if (length == 0) continue;
            int steps = Math.Max(1, (int)MathF.Ceiling(length / RoadSampleSpacing));
            // The last row of a segment is the first of the next one, turned halfway between the two;
            // a closed road's last row meets the first one at the starting point.
            for (int s = previousRow < 0 ? 0 : 1; s <= steps; s++)
            {
                float t = (float)s / steps;
                var centre = Vector2.Lerp(a, b, t);
                var side = s == 0 && i == 0
                    ? JointSide(points, 0, road.Closed)
                    : s == steps
                        ? JointSide(points, (i + 1) % points.Count, road.Closed)
                        : Across(a, b);
                int row = mesh.VertexCount;
                for (int k = 0; k <= across; k++)
                {
                    var at = centre + side * (half * (2f * k / across - 1));
                    float edge = MathF.Abs(2f * k / across - 1);
                    float grain = 1 + 0.015f * (ColorUtil.Hash(i * 1009 + s, k * 7) - 0.5f) * 2;
                    var shade = textured
                        ? Vector3.Clamp(color * ((1 - 0.08f * edge * edge) * grain), Vector3.Zero, Vector3.One)
                        : color;
                    mesh.AddVertex(new Vector3(at.X, terrain.HeightAt(at.X, at.Y) + RoadLift, at.Y), Vector3.UnitY, shade);
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

    private static Vector2 Across(Vector2 from, Vector2 to)
    {
        var d = Vector2.Normalize(to - from);
        return new Vector2(-d.Y, d.X);
    }

    // The direction across the road at a joint: the average of the segments arriving at it and leaving
    // it, so the two rows that meet there share one edge. On a closed road the first and last segments
    // meet at joint 0 too.
    private static Vector2 JointSide(List<Vector2> points, int joint, bool closed)
    {
        int n = points.Count;
        int incoming = (joint - 1 + n) % n;
        int outgoing = (joint + 1) % n;
        var side = Vector2.Zero;
        if ((closed || joint > 0) && points[joint] != points[incoming])
            side += Across(points[incoming], points[joint]);
        if ((closed || joint < n - 1) && points[outgoing] != points[joint])
            side += Across(points[joint], points[outgoing]);
        if (side.LengthSquared() > 1e-6f) return Vector2.Normalize(side);
        // A joint surrounded by zero-length segments has no direction; take any segment that has one.
        for (int i = 0; i < (closed ? n : n - 1); i++)
            if (points[i] != points[(i + 1) % n]) return Across(points[i], points[(i + 1) % n]);
        return new Vector2(1, 0);
    }

    private static void AddUpward(Mesh mesh, int a, int b, int c)
    {
        var (pa, pb, pc) = (mesh.Positions[a], mesh.Positions[b], mesh.Positions[c]);
        if (Vector3.Cross(pb - pa, pc - pa).Y >= 0) mesh.AddTriangle(a, b, c);
        else mesh.AddTriangle(a, c, b);
    }

    private static SceneChunk Chunk(string name, Mesh mesh, Texture? texture = null)
    {
        var (c, r) = mesh.BoundingSphere();
        return new SceneChunk { Name = name, Mesh = mesh, Center = c, Radius = r, Texture = texture };
    }

    private static readonly Vector3 Grass = ColorUtil.Rgb(92, 128, 60);
    private static readonly Vector3 Rock = ColorUtil.Rgb(118, 112, 104);

    private static Mesh TerrainChunk(Terrain t, string? color, string? texture, int cx0, int cz0, Texture? groundMap = null)
    {
        var m = new Mesh();
        var grass = ColorUtil.Parse(color, Grass);
        bool textured = !Smooth(texture);
        // The dry patches are the chosen colour shifted towards straw, so any grass colour keeps its
        // variation; at the default colour this is the old (128, 136, 74).
        var dryGrass = Vector3.Min(grass * new Vector3(1.39f, 1.06f, 1.23f), Vector3.One);
        int cx1 = Math.Min(cx0 + TerrainChunkCells, t.Resolution), cz1 = Math.Min(cz0 + TerrainChunkCells, t.Resolution);
        int stride = cx1 - cx0 + 1;
        // A ground map is placed in the world's own extent, edge to edge: U runs west to east and V
        // north to south, so the picture's top row is the world's north.
        float size = t.Size, half = size / 2;
        Vector2 UvAt(int ix, int iz) =>
            new((t.VertexX(ix) + half) / size, (t.VertexZ(iz) + half) / size);
        for (int iz = cz0; iz <= cz1; iz++)
            for (int ix = cx0; ix <= cx1; ix++)
            {
                var n = t.NormalAt(ix, iz);
                var position = new Vector3(t.VertexX(ix), t[ix, iz], t.VertexZ(iz));
                if (groundMap is not null)
                {
                    // The picture is the ground's colour; the vertex colour stays the fallback under it.
                    m.AddVertex(position, n, grass, UvAt(ix, iz));
                    continue;
                }
                // Steep slopes turn to rock; a cheap hash breaks up the grass so distance is readable.
                // A terrain told to be smooth is one flat colour, lit by its own shape.
                if (!textured)
                {
                    m.AddVertex(position, n, grass);
                    continue;
                }
                float noise = ColorUtil.Hash(ix, iz);
                var patch = Vector3.Lerp(grass, dryGrass, noise * 0.6f);
                float rockiness = Math.Clamp((0.92f - n.Y) * 6, 0, 1);
                m.AddVertex(position, n, Vector3.Lerp(patch, Rock, rockiness));
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

    // A slab or a wall with the pattern it is asked for: tiles, grain, or nothing at all.
    private static void Surface(Mesh m, float w, float h, float d, Vector3 color, Vector3 seam, string texture,
        int seed, float y0 = 0)
    {
        switch (texture)
        {
            case "tiles":
                Primitives.TiledFloor(m, w, h, d, color, seam, seed, y0);
                break;
            case "grain":
                Primitives.GrainedBox(m, w, h, d, color, seed, y0);
                break;
            default:
                Primitives.Box(m, w, h, d, color, y0);
                break;
        }
    }

    // A surface told to be smooth carries no pattern: one flat colour, lit by its own shape.
    private static bool Smooth(string? texture) =>
        string.Equals(texture, "none", StringComparison.OrdinalIgnoreCase);

    // The texture an object is drawn with: the one it names, or the default its type has. A floor is
    // tiled, a box and a building's walls are grained, and a tree, a rock or a car is not textured at
    // all — its own shape tells it apart.
    private static string TextureOf(WorldObject o) =>
        o.Texture ?? (o.Type switch
        {
            "floor" => "tiles",
            "box" or "house" or "office" or "prism" => "grain",
            _ => "none",
        });

    // The object types ObjectMesh knows. "floor" is a slab whose top is tiled: a floor is drawn as a
    // grid of tiles with seams, not as a flat box, so a hall reads as a hall. "prism" is any footprint
    // extruded to a height — a building of any shape, a parking apron, a pond.
    public static readonly string[] ObjectTypes = ["tree.pine", "tree.oak", "house", "office", "rock", "box", "floor", "car", "prism"];

    public static Mesh ObjectMesh(WorldObject o)
    {
        var m = new Mesh();
        var s = o.Scale;
        var rng = new Random(o.Seed);
        float Vary(float amount) => 1 + amount * ((float)rng.NextDouble() * 2 - 1);
        var texture = TextureOf(o);
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
                Surface(m, s.X, s.Y + 2, s.Z, wall, wall * 0.8f, texture, o.Seed, -2);
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
            case "office":
            {
                var wall = ColorUtil.Parse(o.Color, ColorUtil.Rgb(204, 208, 214));
                var roof = ColorUtil.Parse(o.Color2, ColorUtil.Rgb(88, 92, 98));
                var glass = ColorUtil.Rgb(54, 70, 92);
                // Walls reach 2 m below the base so a building on a slope never floats.
                Surface(m, s.X, s.Y + 2, s.Z, wall, wall * 0.8f, texture, o.Seed, -2);
                // A window band standing a few centimetres proud of the walls, so it shows on every side.
                Primitives.Box(m, s.X + 0.06f, MathF.Min(1.2f, s.Y * 0.4f), s.Z + 0.06f, glass, s.Y * 0.5f);
                // A flat roof: a slab with a parapet round it.
                Primitives.Box(m, s.X + 0.5f, 0.25f, s.Z + 0.5f, roof, s.Y);
                float top = s.Y + 0.25f;
                foreach (var (x, z) in new[]
                         {
                             (0f, s.Z / 2 + 0.15f), (0f, -s.Z / 2 - 0.15f),
                             (s.X / 2 + 0.15f, 0f), (-s.X / 2 - 0.15f, 0f),
                         })
                {
                    var rail = new Mesh();
                    bool alongX = z != 0;
                    Primitives.Box(rail, alongX ? s.X + 0.5f : 0.2f, 0.5f, alongX ? 0.2f : s.Z + 0.5f, roof);
                    m.Append(rail, Matrix4x4.CreateTranslation(x, top, z));
                }
                // The way in, on the side a house's door faces too.
                var door = new Mesh();
                Primitives.Box(door, 1.8f, 2.4f, 0.16f, ColorUtil.Rgb(48, 50, 54));
                m.Append(door, Matrix4x4.CreateTranslation(0, 0, s.Z / 2));
                break;
            }
            case "rock":
            {
                var c = ColorUtil.Parse(o.Color, ColorUtil.Rgb(128, 124, 118));
                Primitives.Blob(m, new(s.X / 2, s.Y / 2, s.Z / 2), new(0, s.Y * 0.25f, 0), 0.25f, o.Seed, c, new(0.04f, 0.04f, 0.04f));
                break;
            }
            case "box":
            case "floor":
            {
                // Color is the surface, Color2 the seam between tiles where there are any. A box is
                // grained and a floor tiled unless the object says otherwise.
                var tile = ColorUtil.Parse(o.Color, ColorUtil.Rgb(180, 184, 190));
                var seam = ColorUtil.Parse(o.Color2, tile * 0.8f);
                Surface(m, s.X, s.Y, s.Z, tile, seam, texture, o.Seed);
                break;
            }
            case "car":
                return CarMesh.Build(s.X, s.Y, s.Z,
                    ColorUtil.Parse(o.Color, ColorUtil.Rgb(58, 110, 165)),
                    ColorUtil.Parse(o.Color2, ColorUtil.Rgb(240, 240, 234)),
                    o.Plate);
            case "prism":
            {
                // A footprint of its own, of any shape: Color is the wall, Color2 the roof and the
                // floor, Scale.Y the height. The size across comes from the outline, not from Scale.
                var wall = ColorUtil.Parse(o.Color, ColorUtil.Rgb(200, 204, 210));
                var roof = ColorUtil.Parse(o.Color2, ColorUtil.Rgb(112, 116, 122));
                Primitives.Prism(m, o.Points ?? [], s.Y, wall, roof, o.Openings, o.Seed, !Smooth(texture));
                break;
            }
        }
        return m;
    }
}
