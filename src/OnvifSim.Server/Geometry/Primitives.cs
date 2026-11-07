using System.Numerics;

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
}
