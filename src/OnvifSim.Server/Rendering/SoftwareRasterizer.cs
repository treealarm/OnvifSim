using System.Numerics;
using System.Runtime.CompilerServices;

namespace OnvifSim.Rendering;

// CPU renderer for the low-poly world: sky gradient, z-buffer, Gouraud colours (lighting is baked into
// the scene), per-vertex distance fog, perspective-correct interpolation, a picture where a chunk
// carries one, and lines drawn after all triangles where nothing stands in front of them. No native
// dependencies, and the output is bit-for-bit deterministic for a given scene and pose.
public sealed class SoftwareRasterizer : ICameraRenderer
{
    // One vertex after the view-projection: where it lands, the colour fog has been mixed into, its
    // place in the chunk's picture, and how far fog has taken it (for a textured chunk, which cannot
    // bake fog into a colour nobody reads).
    private struct Vertex
    {
        public Vector4 Clip;
        public Vector3 Color;
        public Vector2 Uv;
        public float Fog;
    }

    private float[] _depth = [];
    private Vertex[] _vertices = [];
    // The colour fog takes a pixel to: the sky at the horizon.
    private Vector3 _fogColor;

    // Clipped polygon scratch (a triangle cut by the near plane has at most 4 corners).
    private readonly Vertex[] _polygon = new Vertex[4];

    public void Render(RenderScene scene, IReadOnlyList<RenderScene.Chunk> moving, in CameraPose pose, Frame frame)
    {
        int w = frame.Width, h = frame.Height;
        if (_depth.Length != w * h) _depth = new float[w * h];
        Array.Fill(_depth, 1f);
        int maxVertices = Math.Max(scene.MaxChunkVertices, moving.Count == 0 ? 0 : moving.Max(c => c.Positions.Length));
        if (_vertices.Length < maxVertices) _vertices = new Vertex[maxVertices];

        var (fwd, right, up) = pose.Basis();
        float aspect = (float)w / h;
        float tanH = pose.HalfTangent;
        float tanV = tanH / aspect;
        float far = scene.FogEnd;

        DrawSky(scene, frame, fwd, right, up, tanH, tanV);

        var viewProj = pose.ViewProjection(aspect, far);

        var frustum = new ViewFrustum(pose, aspect, far);

        var fog = scene.SkyHorizon;
        _fogColor = fog;
        float fogStart = scene.FogStart, fogInv = 1 / (scene.FogEnd - scene.FogStart);

        void Transform(RenderScene.Chunk chunk)
        {
            var pos = chunk.Positions;
            var col = chunk.Colors;
            var uv = chunk.Uvs;
            for (int i = 0; i < pos.Length; i++)
            {
                var p = Vector4.Transform(new Vector4(pos[i], 1), viewProj);
                float f = Math.Clamp((p.W - fogStart) * fogInv, 0, 1);
                _vertices[i] = new Vertex
                {
                    Clip = p,
                    Color = Vector3.Lerp(col[i], fog, f),
                    Uv = uv is null ? default : uv[i],
                    Fog = f,
                };
            }
        }

        var chunks = scene.Chunks.Concat(moving);
        foreach (var chunk in chunks)
        {
            if (chunk.Indices.Length == 0 || !frustum.Sees(chunk.Center, chunk.Radius)) continue;
            Transform(chunk);
            var idx = chunk.Indices;
            for (int t = 0; t < idx.Length; t += 3)
                DrawTriangle(frame, idx[t], idx[t + 1], idx[t + 2], chunk.Texture);
        }
        // Lines last, so a triangle drawn after one cannot cover it from behind.
        foreach (var chunk in chunks)
        {
            if (chunk.Lines.Length == 0 || !frustum.Sees(chunk.Center, chunk.Radius)) continue;
            Transform(chunk);
            var lines = chunk.Lines;
            for (int l = 0; l + 1 < lines.Length; l += 2)
                DrawLine(frame, lines[l], lines[l + 1], chunk.LineAlpha);
        }
    }

    // A one-pixel line between two transformed vertices, cut at the near plane, depth-tested against
    // what is drawn and hiding nothing itself.
    private void DrawLine(Frame frame, int i0, int i1, float alpha)
    {
        var a = _vertices[i0].Clip;
        var b = _vertices[i1].Clip;
        var ca = _vertices[i0].Color;
        var cb = _vertices[i1].Color;
        if (a.Z < 0 && b.Z < 0) return;
        if ((a.X > a.W && b.X > b.W) || (a.X < -a.W && b.X < -b.W) || (a.Y > a.W && b.Y > b.W) || (a.Y < -a.W && b.Y < -b.W))
            return;
        if (a.Z < 0 || b.Z < 0)
        {
            float t = a.Z / (a.Z - b.Z);
            var cut = Vector4.Lerp(a, b, t);
            var cutColor = Vector3.Lerp(ca, cb, t);
            if (a.Z < 0) (a, ca) = (cut, cutColor);
            else (b, cb) = (cut, cutColor);
        }
        int w = frame.Width, h = frame.Height;
        float ax = (a.X / a.W + 1) * 0.5f * w, ay = (1 - a.Y / a.W) * 0.5f * h, az = a.Z / a.W;
        float bx = (b.X / b.W + 1) * 0.5f * w, by = (1 - b.Y / b.W) * 0.5f * h, bz = b.Z / b.W;
        // Only the part of the segment over the picture is walked (Liang-Barsky against its edges).
        float t0 = 0, t1 = 1, dx = bx - ax, dy = by - ay;
        if (!Clip(-dx, ax, ref t0, ref t1) || !Clip(dx, w - ax, ref t0, ref t1) ||
            !Clip(-dy, ay, ref t0, ref t1) || !Clip(dy, h - ay, ref t0, ref t1))
            return;
        int steps = Math.Max(1, (int)MathF.Ceiling(Math.Max(MathF.Abs(dx), MathF.Abs(dy)) * (t1 - t0)));
        var px = frame.Rgba;
        for (int k = 0; k <= steps; k++)
        {
            float t = t0 + (t1 - t0) * k / steps;
            int x = (int)(ax + dx * t), y = (int)(ay + dy * t);
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
            int i = y * w + x;
            float z = az + (bz - az) * t;
            if (z < 0 || z >= _depth[i]) continue;
            var col = Vector3.Lerp(ca, cb, t) * 255;
            int o = i * 4;
            px[o] = Blend(px[o], col.X, alpha);
            px[o + 1] = Blend(px[o + 1], col.Y, alpha);
            px[o + 2] = Blend(px[o + 2], col.Z, alpha);
        }
    }

    private static bool Clip(float p, float q, ref float t0, ref float t1)
    {
        if (p == 0) return q >= 0;
        float r = q / p;
        if (p < 0)
        {
            if (r > t1) return false;
            if (r > t0) t0 = r;
        }
        else
        {
            if (r < t0) return false;
            if (r < t1) t1 = r;
        }
        return true;
    }

    private static byte Blend(byte under, float over, float alpha) =>
        (byte)Math.Clamp(under + (over - under) * alpha, 0, 255);

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

    private void DrawTriangle(Frame frame, int i0, int i1, int i2, Texture? texture)
    {
        var a = _vertices[i0].Clip;
        var b = _vertices[i1].Clip;
        var c = _vertices[i2].Clip;
        // Trivially outside one clip plane: skip.
        if ((a.X > a.W && b.X > b.W && c.X > c.W) || (a.X < -a.W && b.X < -b.W && c.X < -c.W) ||
            (a.Y > a.W && b.Y > b.W && c.Y > c.W) || (a.Y < -a.W && b.Y < -b.W && c.Y < -c.W) ||
            (a.Z < 0 && b.Z < 0 && c.Z < 0) || (a.Z > a.W && b.Z > b.W && c.Z > c.W))
            return;

        if (a.Z >= 0 && b.Z >= 0 && c.Z >= 0)
        {
            Rasterize(frame, _vertices[i0], _vertices[i1], _vertices[i2], texture);
            return;
        }

        // Crosses the near plane (clip z = 0): cut it and draw the remaining polygon as a fan. Every
        // field is interpolated here, so a textured triangle keeps its place in the picture.
        int n = 0;
        Span<int> ids = [i0, i1, i2];
        for (int k = 0; k < 3; k++)
        {
            var p = _vertices[ids[k]];
            var q = _vertices[ids[(k + 1) % 3]];
            if (p.Clip.Z >= 0) _polygon[n++] = p;
            if ((p.Clip.Z >= 0) != (q.Clip.Z >= 0))
            {
                float t = p.Clip.Z / (p.Clip.Z - q.Clip.Z);
                _polygon[n++] = new Vertex
                {
                    Clip = Vector4.Lerp(p.Clip, q.Clip, t),
                    Color = Vector3.Lerp(p.Color, q.Color, t),
                    Uv = Vector2.Lerp(p.Uv, q.Uv, t),
                    Fog = p.Fog + (q.Fog - p.Fog) * t,
                };
            }
        }
        for (int k = 1; k + 1 < n; k++)
            Rasterize(frame, _polygon[0], _polygon[k], _polygon[k + 1], texture);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Rasterize(Frame frame, in Vertex va, in Vertex vb, in Vertex vc, Texture? texture)
    {
        int w = frame.Width, h = frame.Height;
        // To screen space (y down) with 1/w kept for perspective-correct interpolation.
        float wa = 1 / va.Clip.W, wb = 1 / vb.Clip.W, wc = 1 / vc.Clip.W;
        float ax = (va.Clip.X * wa + 1) * 0.5f * w, ay = (1 - va.Clip.Y * wa) * 0.5f * h, az = va.Clip.Z * wa;
        float bx = (vb.Clip.X * wb + 1) * 0.5f * w, by = (1 - vb.Clip.Y * wb) * 0.5f * h, bz = vb.Clip.Z * wb;
        float cx = (vc.Clip.X * wc + 1) * 0.5f * w, cy = (1 - vc.Clip.Y * wc) * 0.5f * h, cz = vc.Clip.Z * wc;

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
        var pa = va.Color * wa;
        var pb = vb.Color * wb;
        var pc = vc.Color * wc;
        // A picture is sampled the same way: u/w and v/w, and the fog factor, which a textured chunk
        // cannot bake into a colour.
        var uwa = va.Uv * wa;
        var uwb = vb.Uv * wb;
        var uwc = vc.Uv * wc;
        float fwa = va.Fog * wa, fwb = vb.Fog * wb, fwc = vc.Fog * wc;

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
        if (texture is null)
        {
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
            return;
        }

        var fogColor = _fogColor;
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
                        var uv = (uwa * e0 + uwb * e1 + uwc * e2) * iw;
                        texture.Sample(uv.X, uv.Y, out float r, out float g, out float b);
                        float f = (fwa * e0 + fwb * e1 + fwc * e2) * iw;
                        int o = i * 4;
                        px[o] = (byte)Math.Clamp((r + (fogColor.X - r) * f) * 255, 0, 255);
                        px[o + 1] = (byte)Math.Clamp((g + (fogColor.Y - g) * f) * 255, 0, 255);
                        px[o + 2] = (byte)Math.Clamp((b + (fogColor.Z - b) * f) * 255, 0, 255);
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
