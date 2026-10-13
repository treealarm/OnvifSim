using System.Numerics;
using System.Runtime.CompilerServices;

namespace OnvifSim.Rendering;

// CPU renderer for the low-poly world: sky gradient, z-buffer, Gouraud colours (lighting is baked into
// the scene), per-vertex distance fog, perspective-correct interpolation. No native dependencies, and
// the output is bit-for-bit deterministic for a given scene and pose.
public sealed class SoftwareRasterizer : ICameraRenderer
{
    private const float Near = ViewFrustum.Near;

    private float[] _depth = [];
    private Vector4[] _clip = [];
    private Vector3[] _color = [];

    // Clipped polygon scratch (a triangle cut by the near plane has at most 4 corners).
    private readonly Vector4[] _polyPos = new Vector4[4];
    private readonly Vector3[] _polyCol = new Vector3[4];

    public void Render(RenderScene scene, IReadOnlyList<RenderScene.Chunk> moving, in CameraPose pose, Frame frame)
    {
        int w = frame.Width, h = frame.Height;
        if (_depth.Length != w * h) _depth = new float[w * h];
        Array.Fill(_depth, 1f);
        int maxVertices = Math.Max(scene.MaxChunkVertices, moving.Count == 0 ? 0 : moving.Max(c => c.Positions.Length));
        if (_clip.Length < maxVertices)
        {
            _clip = new Vector4[maxVertices];
            _color = new Vector3[maxVertices];
        }

        var (fwd, right, up) = pose.Basis();
        float aspect = (float)w / h;
        float tanH = MathF.Tan(Math.Clamp(pose.HFov, 0.5f, 170f) * MathF.PI / 360);
        float tanV = tanH / aspect;
        float far = scene.FogEnd;

        DrawSky(scene, frame, fwd, right, up, tanH, tanV);

        var eye = pose.Position;
        var view = new Matrix4x4(
            right.X, up.X, -fwd.X, 0,
            right.Y, up.Y, -fwd.Y, 0,
            right.Z, up.Z, -fwd.Z, 0,
            -Vector3.Dot(right, eye), -Vector3.Dot(up, eye), Vector3.Dot(fwd, eye), 1);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(2 * MathF.Atan(tanV), aspect, Near, far);
        var viewProj = view * proj;

        var frustum = new ViewFrustum(pose, aspect, far);

        var fog = scene.SkyHorizon;
        float fogStart = scene.FogStart, fogInv = 1 / (scene.FogEnd - scene.FogStart);

        foreach (var chunk in scene.Chunks.Concat(moving))
        {
            if (!frustum.Sees(chunk.Center, chunk.Radius)) continue;

            var pos = chunk.Positions;
            var col = chunk.Colors;
            for (int i = 0; i < pos.Length; i++)
            {
                var p = Vector4.Transform(new Vector4(pos[i], 1), viewProj);
                _clip[i] = p;
                float f = Math.Clamp((p.W - fogStart) * fogInv, 0, 1);
                _color[i] = Vector3.Lerp(col[i], fog, f);
            }

            var idx = chunk.Indices;
            for (int t = 0; t < idx.Length; t += 3)
                DrawTriangle(frame, idx[t], idx[t + 1], idx[t + 2]);
        }
    }

    private static void DrawSky(RenderScene scene, Frame frame, Vector3 fwd, Vector3 right, Vector3 up, float tanH, float tanV)
    {
        int w = frame.Width, h = frame.Height;
        var px = frame.Rgba;
        var zenith = scene.SkyZenith;
        var horizon = scene.SkyHorizon;
        // Below the horizon (only visible past the terrain edge) the sky fades to a hazy ground colour.
        var below = horizon * 0.85f;
        for (int y = 0; y < h; y++)
        {
            float ny = 1 - (y + 0.5f) / h * 2;
            var rowDir = fwd + up * (ny * tanV);
            var dx = right * tanH;
            int o = y * w * 4;
            for (int x = 0; x < w; x++, o += 4)
            {
                float nx = (x + 0.5f) / w * 2 - 1;
                var d = rowDir + dx * nx;
                float s = d.Y / d.Length();
                var c = s >= 0
                    ? Vector3.Lerp(horizon, zenith, MathF.Sqrt(s))
                    : Vector3.Lerp(horizon, below, MathF.Min(1, -s * 8));
                px[o] = (byte)(c.X * 255);
                px[o + 1] = (byte)(c.Y * 255);
                px[o + 2] = (byte)(c.Z * 255);
                px[o + 3] = 255;
            }
        }
    }

    private void DrawTriangle(Frame frame, int i0, int i1, int i2)
    {
        var a = _clip[i0];
        var b = _clip[i1];
        var c = _clip[i2];
        // Trivially outside one clip plane: skip.
        if ((a.X > a.W && b.X > b.W && c.X > c.W) || (a.X < -a.W && b.X < -b.W && c.X < -c.W) ||
            (a.Y > a.W && b.Y > b.W && c.Y > c.W) || (a.Y < -a.W && b.Y < -b.W && c.Y < -c.W) ||
            (a.Z < 0 && b.Z < 0 && c.Z < 0) || (a.Z > a.W && b.Z > b.W && c.Z > c.W))
            return;

        if (a.Z >= 0 && b.Z >= 0 && c.Z >= 0)
        {
            Rasterize(frame, a, _color[i0], b, _color[i1], c, _color[i2]);
            return;
        }

        // Crosses the near plane (clip z = 0): cut it and draw the remaining polygon as a fan.
        int n = 0;
        Span<int> ids = [i0, i1, i2];
        for (int k = 0; k < 3; k++)
        {
            var p = _clip[ids[k]];
            var q = _clip[ids[(k + 1) % 3]];
            if (p.Z >= 0)
            {
                _polyPos[n] = p;
                _polyCol[n++] = _color[ids[k]];
            }
            if ((p.Z >= 0) != (q.Z >= 0))
            {
                float t = p.Z / (p.Z - q.Z);
                _polyPos[n] = Vector4.Lerp(p, q, t);
                _polyCol[n++] = Vector3.Lerp(_color[ids[k]], _color[ids[(k + 1) % 3]], t);
            }
        }
        for (int k = 1; k + 1 < n; k++)
            Rasterize(frame, _polyPos[0], _polyCol[0], _polyPos[k], _polyCol[k], _polyPos[k + 1], _polyCol[k + 1]);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Rasterize(Frame frame, Vector4 a, Vector3 ca, Vector4 b, Vector3 cb, Vector4 c, Vector3 cc)
    {
        int w = frame.Width, h = frame.Height;
        // To screen space (y down) with 1/w kept for perspective-correct colour.
        float wa = 1 / a.W, wb = 1 / b.W, wc = 1 / c.W;
        float ax = (a.X * wa + 1) * 0.5f * w, ay = (1 - a.Y * wa) * 0.5f * h, az = a.Z * wa;
        float bx = (b.X * wb + 1) * 0.5f * w, by = (1 - b.Y * wb) * 0.5f * h, bz = b.Z * wb;
        float cx = (c.X * wc + 1) * 0.5f * w, cy = (1 - c.Y * wc) * 0.5f * h, cz = c.Z * wc;

        // Front faces are counter-clockwise in NDC, which is clockwise (negative area) with y down.
        float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        if (area >= 0) return;
        // Each edge function evaluated at the opposite vertex equals -area, so dividing by -area makes
        // them the barycentric weights: 1 at their vertex, 0 on their edge, all positive inside.
        float invArea = -1 / area;

        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))));
        int maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))));
        int maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
        if (minX > maxX || minY > maxY) return;

        // Colour over w, interpolated linearly in screen space, then divided by interpolated 1/w.
        var pa = ca * wa;
        var pb = cb * wb;
        var pc = cc * wc;

        // Edge functions, normalised so each is the barycentric weight of the opposite vertex.
        // e0 belongs to a (edge b->c), e1 to b (edge c->a), e2 to c (edge a->b).
        float e0dx = (cy - by) * invArea, e0dy = -(cx - bx) * invArea;
        float e1dx = (ay - cy) * invArea, e1dy = -(ax - cx) * invArea;
        float e2dx = (by - ay) * invArea, e2dy = -(bx - ax) * invArea;
        float sx = minX + 0.5f, sy = minY + 0.5f;
        float e0row = ((sx - bx) * (cy - by) - (sy - by) * (cx - bx)) * invArea;
        float e1row = ((sx - cx) * (ay - cy) - (sy - cy) * (ax - cx)) * invArea;
        float e2row = ((sx - ax) * (by - ay) - (sy - ay) * (bx - ax)) * invArea;

        var depth = _depth;
        var px = frame.Rgba;
        for (int y = minY; y <= maxY; y++)
        {
            float e0 = e0row, e1 = e1row, e2 = e2row;
            int row = y * w;
            for (int x = minX; x <= maxX; x++)
            {
                if (e0 >= 0 && e1 >= 0 && e2 >= 0)
                {
                    float z = e0 * az + e1 * bz + e2 * cz;
                    int i = row + x;
                    if (z < depth[i] && z >= 0)
                    {
                        depth[i] = z;
                        float iw = 1 / (e0 * wa + e1 * wb + e2 * wc);
                        var col = (pa * e0 + pb * e1 + pc * e2) * (iw * 255);
                        int o = i * 4;
                        px[o] = (byte)Math.Min(255f, col.X);
                        px[o + 1] = (byte)Math.Min(255f, col.Y);
                        px[o + 2] = (byte)Math.Min(255f, col.Z);
                    }
                }
                e0 += e0dx;
                e1 += e1dx;
                e2 += e2dx;
            }
            e0row += e0dy;
            e1row += e1dy;
            e2row += e2dy;
        }
    }
}
