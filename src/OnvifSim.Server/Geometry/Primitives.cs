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
