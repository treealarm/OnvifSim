using System.Numerics;
using OnvifSim.Rendering;
using OnvifSim.World;

namespace OnvifSim.Geometry;

// Where a camera's field of view meets the ground: a grid of rays through its picture, each followed
// from the eye until it reaches the terrain. A ray that meets no ground within reach (above the
// horizon, past the terrain's edge or beyond the fog) stops at a fixed length instead, so the field of
// view still has a far end to draw. The editor computes the same grid (viewport/footprint.ts).
public static class ViewFootprint
{
    // Rays across and down the picture, corners included: few, as the grid is a sketch.
    public const int Columns = 5, Rows = 4;

    public sealed record Grid(Vector3 Eye, Vector3[,] Points, bool[,] OnGround);

    // pose: the camera's eye and picture axes; aspect: its picture's width over height; reach: how far
    // a ray looks for the ground; length: where one that finds none stops.
    public static Grid Cast(in CameraPose pose, float aspect, Terrain terrain, float reach, float length)
    {
        float tanH = pose.HalfTangent, tanV = tanH / aspect;
        var points = new Vector3[Columns, Rows];
        var onGround = new bool[Columns, Rows];
        for (int i = 0; i < Columns; i++)
            for (int j = 0; j < Rows; j++)
            {
                float u = 2f * i / (Columns - 1) - 1, v = 1 - 2f * j / (Rows - 1);
                var direction = Vector3.Normalize(pose.Forward + pose.Right * (u * tanH) + pose.Up * (v * tanV));
                (points[i, j], onGround[i, j]) = Ray(terrain, pose.Position, direction, reach, length);
            }
        return new Grid(pose.Position, points, onGround);
    }

    // Steps grow with the distance (2% of it, a quarter metre at least), then the crossing is halved
    // down to millimetres.
    private static (Vector3 Point, bool OnGround) Ray(Terrain terrain, Vector3 eye, Vector3 direction, float reach, float length)
    {
        float half = terrain.Size / 2;
        float Above(float s)
        {
            var p = eye + direction * s;
            return p.Y - terrain.HeightAt(p.X, p.Z);
        }
        bool Inside(float s)
        {
            var p = eye + direction * s;
            return MathF.Abs(p.X) <= half && MathF.Abs(p.Z) <= half;
        }
        if (Above(0) > 0)
        {
            float previous = 0;
            for (float s = 0.25f; s <= reach && Inside(s); s += MathF.Max(0.25f, s * 0.02f))
            {
                if (Above(s) > 0)
                {
                    previous = s;
                    continue;
                }
                float low = previous, high = s;
                for (int k = 0; k < 16; k++)
                {
                    float mid = (low + high) / 2;
                    if (Above(mid) > 0) low = mid;
                    else high = mid;
                }
                var hit = eye + direction * high;
                return (hit with { Y = terrain.HeightAt(hit.X, hit.Z) }, true);
            }
        }
        return (eye + direction * length, false);
    }
}
