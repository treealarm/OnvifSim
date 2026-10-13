using System.Numerics;

namespace OnvifSim.Rendering;

// Whether anything the camera draws stands between two points: the same triangles the rasterizer fills,
// so what a camera "sees" is what its picture shows. Chunks the segment cannot reach are skipped by
// their bounding spheres.
public static class LineOfSight
{
    // Hits this close to either end do not count: the ground under a person's feet, or a surface the
    // eye itself sits on.
    private const float EndMargin = 0.01f;

    public static bool IsClear(RenderScene scene, Vector3 from, Vector3 to)
    {
        var direction = to - from;
        foreach (var chunk in scene.Chunks)
        {
            if (!SegmentNearSphere(from, direction, chunk.Center, chunk.Radius)) continue;
            var positions = chunk.Positions;
            var indices = chunk.Indices;
            for (int t = 0; t < indices.Length; t += 3)
                if (Hits(from, direction, positions[indices[t]], positions[indices[t + 1]], positions[indices[t + 2]]))
                    return false;
        }
        return true;
    }

    private static bool SegmentNearSphere(Vector3 from, Vector3 direction, Vector3 center, float radius)
    {
        float along = Math.Clamp(Vector3.Dot(center - from, direction) / direction.LengthSquared(), 0, 1);
        return Vector3.DistanceSquared(from + direction * along, center) <= radius * radius;
    }

    // Möller–Trumbore, both faces, for the segment from + s * direction with s inside the margins.
    private static bool Hits(Vector3 from, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var p = Vector3.Cross(direction, ac);
        float det = Vector3.Dot(ab, p);
        if (MathF.Abs(det) < 1e-9f) return false;
        float inv = 1 / det;
        var s = from - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u is < 0 or > 1) return false;
        var q = Vector3.Cross(s, ab);
        float v = Vector3.Dot(direction, q) * inv;
        if (v < 0 || u + v > 1) return false;
        float along = Vector3.Dot(ac, q) * inv;
        return along > EndMargin && along < 1 - EndMargin;
    }
}
