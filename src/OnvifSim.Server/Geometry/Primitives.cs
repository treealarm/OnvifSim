using System.Numerics;
using OnvifSim.World;

namespace OnvifSim.Geometry;

// Low-poly building blocks in local space, flat shaded. Y is up, the base sits on y = 0 unless noted.
public static class Primitives
{
    // Box spanning x in [-w/2, w/2], y in [y0, y0 + h], z in [-d/2, d/2].
    public static void Box(Mesh m, float w, float h, float d, Vector3 color, float y0 = 0)
    {
        float x = w / 2, z = d / 2, y1 = y0 + h;
        Vector3 p000 = new(-x, y0, -z), p100 = new(x, y0, -z), p110 = new(x, y1, -z), p010 = new(-x, y1, -z);
        Vector3 p001 = new(-x, y0, z), p101 = new(x, y0, z), p111 = new(x, y1, z), p011 = new(-x, y1, z);
        m.AddFlatQuad(p001, p101, p111, p011, color); // +Z
        m.AddFlatQuad(p100, p000, p010, p110, color); // -Z
        m.AddFlatQuad(p101, p100, p110, p111, color); // +X
        m.AddFlatQuad(p000, p001, p011, p010, color); // -X
        m.AddFlatQuad(p011, p111, p110, p010, color); // top
        m.AddFlatQuad(p000, p100, p101, p001, color); // bottom
    }

    // A box whose faces carry a little grain: every face is cut into cells a few metres across, each
    // shaded slightly differently, with the bottom of the box a touch darker. A wall or a slab then
    // reads as a surface rather than as one flat grey, and the pattern follows the seed, so a world
    // built twice draws the same thing. Cells are capped so a very long plate stays cheap.
    public static void GrainedBox(Mesh m, float w, float h, float d, Vector3 color, int seed, float y0 = 0,
        float cell = 2.5f, float variation = 0.03f, float bottomShade = 0.05f)
    {
        float x = w / 2, z = d / 2, y1 = y0 + h;
        Face(m, new(-x, y0, z), new(2 * x, 0, 0), new(0, h, 0), color, seed, 0, cell, variation, bottomShade, y0, h); // +Z
        Face(m, new(x, y0, -z), new(-2 * x, 0, 0), new(0, h, 0), color, seed, 1, cell, variation, bottomShade, y0, h); // -Z
        Face(m, new(x, y0, -z), new(0, 0, -2 * z), new(0, h, 0), color, seed, 2, cell, variation, bottomShade, y0, h); // +X
        Face(m, new(-x, y0, z), new(0, 0, 2 * z), new(0, h, 0), color, seed, 3, cell, variation, bottomShade, y0, h); // -X
        Face(m, new(-x, y1, z), new(2 * x, 0, 0), new(0, 0, -2 * z), color, seed, 4, cell, variation, 0, y0, h); // top
        Face(m, new(-x, y0, -z), new(2 * x, 0, 0), new(0, 0, 2 * z), color, seed, 5, cell, variation, 0, y0, h); // bottom
    }

    // One face cut into a grid of grained cells; u and v are the face's full edges, and u x v faces out.
    private static void Face(Mesh m, Vector3 origin, Vector3 u, Vector3 v, Vector3 color, int seed, int face,
        float cell, float variation, float bottomShade, float y0, float h)
    {
        int across = Math.Clamp((int)MathF.Ceiling(u.Length() / cell), 1, 64);
        int down = Math.Clamp((int)MathF.Ceiling(v.Length() / cell), 1, 64);
        var du = u / across;
        var dv = v / down;
        for (int j = 0; j < down; j++)
            for (int i = 0; i < across; i++)
            {
                var a = origin + du * i + dv * j;
                var b = a + du;
                var c = b + dv;
                var d = a + dv;
                // The gradient runs along the face's own vertical edge, whatever its direction in the world.
                float t = h > 0 ? Math.Clamp((j + 0.5f) / down, 0, 1) : 1;
                float shade = 1 + variation * (ColorUtil.Hash(seed * 131 + face, i * 31 + j) - 0.5f) * 2
                    - bottomShade * (1 - t);
                m.AddFlatQuad(a, b, c, d, Vector3.Clamp(color * shade, Vector3.Zero, Vector3.One));
            }
    }

    // A floor slab: a slab in the seam colour with its top cut into tiles. Each tile keeps a couple of
    // centimetres clear of its neighbours, so the seams show, and carries a gentle gradient from the
    // middle of the slab outwards with a slight variation of its own — a hall floor that reads as a
    // floor rather than as a sheet of one grey.
    public static void TiledFloor(Mesh m, float w, float h, float d, Vector3 tile, Vector3 seam, int seed,
        float y0 = 0, float tileSize = 3f, float variation = 0.015f)
    {
        Box(m, w, h, d, seam, y0);
        float x = w / 2, z = d / 2, top = y0 + h + 0.002f, gap = 0.02f;
        int across = Math.Max(1, (int)MathF.Round(w / tileSize));
        int down = Math.Max(1, (int)MathF.Round(d / tileSize));
        float cw = w / across, cd = d / down, radius = MathF.Sqrt(x * x + z * z);
        for (int j = 0; j < down; j++)
            for (int i = 0; i < across; i++)
            {
                float x0 = -x + i * cw + gap, x1 = x0 + cw - 2 * gap;
                float z0 = -z + j * cd + gap, z1 = z0 + cd - 2 * gap;
                float cx = (x0 + x1) / 2, cz = (z0 + z1) / 2;
                float r = radius > 0 ? MathF.Sqrt(cx * cx + cz * cz) / radius : 0;
                float shade = 1 + 0.03f * (0.5f - r) + variation * (ColorUtil.Hash(seed, i * 131 + j) - 0.5f) * 2;
                var color = Vector3.Clamp(tile * shade, Vector3.Zero, Vector3.One);
                // Counter-clockwise seen from above, as the box's own top face.
                m.AddFlatQuad(new(x0, top, z1), new(x1, top, z1), new(x1, top, z0), new(x0, top, z0), color);
            }
    }

    // Gable roof over a w x d footprint starting at y0, ridge along Z.
    public static void GableRoof(Mesh m, float w, float d, float y0, float rise, float overhang, Vector3 color, Vector3 gableColor)
    {
        float x = w / 2 + overhang, z = d / 2 + overhang, yr = y0 + rise;
        Vector3 l0 = new(-x, y0, -z), l1 = new(-x, y0, z), r0 = new(x, y0, -z), r1 = new(x, y0, z);
        Vector3 t0 = new(0, yr, -z), t1 = new(0, yr, z);
        m.AddFlatQuad(l1, t1, t0, l0, color);
        m.AddFlatQuad(r0, t0, t1, r1, color);
        m.AddFlatTriangle(l1, r1, t1, gableColor);
        m.AddFlatTriangle(r0, l0, t0, gableColor);
        m.AddFlatQuad(l0, r0, r1, l1, color * 0.6f); // underside, visible from low cameras
    }

    // Truncated cone (cylinder when r0 == r1) from y0 to y0 + h with its own caps.
    public static void Frustum(Mesh m, float r0, float r1, float h, int segments, Vector3 color, float y0 = 0, bool caps = true)
    {
        float y1 = y0 + h;
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * MathF.Tau / segments, a1 = (i + 1) * MathF.Tau / segments;
            Vector3 d0 = new(MathF.Sin(a0), 0, MathF.Cos(a0)), d1 = new(MathF.Sin(a1), 0, MathF.Cos(a1));
            Vector3 b0 = d0 * r0 + new Vector3(0, y0, 0), b1 = d1 * r0 + new Vector3(0, y0, 0);
            Vector3 t0 = d0 * r1 + new Vector3(0, y1, 0), t1 = d1 * r1 + new Vector3(0, y1, 0);
            if (r1 > 0)
                m.AddFlatQuad(b0, b1, t1, t0, color);
            else
                m.AddFlatTriangle(b0, b1, t0, color);
            if (caps)
            {
                m.AddFlatTriangle(new(0, y0, 0), b1, b0, color);
                if (r1 > 0) m.AddFlatTriangle(new(0, y1, 0), t0, t1, color);
            }
        }
    }

    // Icosahedron-based blob, each vertex pushed in or out by a seeded amount so no two look alike.
    public static void Blob(Mesh m, Vector3 radii, Vector3 center, float jitter, int seed, Vector3 color, Vector3 colorVariation)
    {
        var rng = new Random(seed);
        float t = (1 + MathF.Sqrt(5)) / 2;
        Vector3[] v =
        [
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
            new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
            new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        ];
        int[] f =
        [
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
        ];
        for (int i = 0; i < v.Length; i++)
            v[i] = center + Vector3.Normalize(v[i]) * radii * (1 + jitter * ((float)rng.NextDouble() * 2 - 1));
        for (int i = 0; i < f.Length; i += 3)
        {
            var c = color + colorVariation * ((float)rng.NextDouble() * 2 - 1);
            m.AddFlatTriangle(v[f[i]], v[f[i + 1]], v[f[i + 2]], Vector3.Clamp(c, Vector3.Zero, Vector3.One));
        }
    }

    // The underside of a cap sits this far above the terrain: enough that the ground does not shine
    // through it and a camera inside a building stands on a floor rather than on the grass.
    public const float PrismFloorLift = 0.02f;

    // A building shell from its footprint in the object's own metres: a wall on every segment, a roof
    // and a floor, all drawn from both sides, so a camera inside sees the walls and a camera outside
    // sees the building. An opening cuts its segment into two panels and the band above it (and below,
    // when its sill is raised). Grain on the walls follows the seed, so a world built twice draws the
    // same thing. The footprint must already be normalised (see Polygon).
    public static void Prism(Mesh m, IReadOnlyList<GroundPoint> points, float height, Vector3 wall, Vector3 roof,
        IReadOnlyList<OpeningModel>? openings, int seed, bool textured)
    {
        int n = points.Count;
        if (n < 3 || height <= 0) return;

        for (int i = 0; i < n; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % n];
            float length = MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
            if (length < Polygon.MinPointGapM) continue;

            var cuts = openings?.Where(o => o.Edge == i && o.Width > 0 && o.Height > 0).OrderBy(o => o.Offset).ToList();
            float cursor = 0;
            if (cuts is not null)
                foreach (var opening in cuts)
                {
                    float start = Math.Clamp(opening.Offset, 0, length);
                    float end = Math.Clamp(opening.Offset + opening.Width, 0, length);
                    if (start > cursor) Wall(m, a, b, cursor, start, 0, height);
                    if (opening.Sill > 0) Wall(m, a, b, start, end, 0, opening.Sill);
                    if (opening.Sill + opening.Height < height) Wall(m, a, b, start, end, opening.Sill + opening.Height, height);
                    cursor = Math.Max(cursor, end);
                }
            if (cursor < length) Wall(m, a, b, cursor, length, 0, height);

            // Each wall is drawn twice: from outside and from inside, where culling would drop one face.
            void Wall(Mesh target, GroundPoint from, GroundPoint to, float t0, float t1, float y0, float y1)
            {
                if (t1 - t0 <= Polygon.MinPointGapM || y1 - y0 <= 0) return;
                var p0 = Lerp(from, to, t0 / length);
                var p1 = Lerp(from, to, t1 / length);
                Quad(target, p0, p1, y0, y1, wall, seed * 131 + i, textured);
            }
        }

        var roofColor = roof;
        var floorColor = Vector3.Clamp(roof * 0.8f, Vector3.Zero, Vector3.One);
        var triangles = Polygon.Triangulate(points);
        for (int t = 0; t + 2 < triangles.Count; t += 3)
        {
            var a = At(points[triangles[t]], height);
            var b = At(points[triangles[t + 1]], height);
            var c = At(points[triangles[t + 2]], height);
            m.AddFlatTriangle(a, b, c, roofColor);   // the roof, facing up
            m.AddFlatTriangle(a, c, b, roofColor * 1.02f); // and its underside, the ceiling

            var f0 = At(points[triangles[t]], PrismFloorLift);
            var f1 = At(points[triangles[t + 1]], PrismFloorLift);
            var f2 = At(points[triangles[t + 2]], PrismFloorLift);
            m.AddFlatTriangle(f0, f1, f2, floorColor); // the floor, facing up
            m.AddFlatTriangle(f0, f2, f1, floorColor); // and the underside, which nobody sees
        }
    }

    private static Vector3 At(GroundPoint p, float y) => new(p.X, y, p.Z);

    private static GroundPoint Lerp(GroundPoint a, GroundPoint b, float t) =>
        new(a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t);

    // One panel of wall between two points along its segment, from y0 to y1, cut into a few cells of
    // grain and drawn from both sides.
    private static void Quad(Mesh m, GroundPoint a, GroundPoint b, float y0, float y1, Vector3 color, int seed, bool textured)
    {
        int across = textured ? Math.Clamp((int)MathF.Ceiling(Distance(a, b) / 2.5f), 1, 64) : 1;
        int down = textured ? Math.Clamp((int)MathF.Ceiling((y1 - y0) / 2.5f), 1, 32) : 1;
        for (int j = 0; j < down; j++)
        {
            float t0 = (float)j / down, t1 = (float)(j + 1) / down;
            float bottom = y0 + (y1 - y0) * t0, top = y0 + (y1 - y0) * t1;
            for (int i = 0; i < across; i++)
            {
                var p0 = Lerp(a, b, (float)i / across);
                var p1 = Lerp(a, b, (float)(i + 1) / across);
                float shade = textured
                    ? 1 + 0.03f * (ColorUtil.Hash(seed, i * 31 + j) - 0.5f) * 2 - 0.05f * (1 - (t0 + t1) / 2)
                    : 1;
                var cell = Vector3.Clamp(color * shade, Vector3.Zero, Vector3.One);
                var v00 = new Vector3(p0.X, bottom, p0.Z);
                var v10 = new Vector3(p1.X, bottom, p1.Z);
                var v11 = new Vector3(p1.X, top, p1.Z);
                var v01 = new Vector3(p0.X, top, p0.Z);
                m.AddFlatQuad(v00, v10, v11, v01, cell);         // facing out of the footprint
                m.AddFlatQuad(v00, v01, v11, v10, cell * 0.92f); // and the inner face, a little darker
            }
        }
    }

    private static float Distance(GroundPoint a, GroundPoint b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
