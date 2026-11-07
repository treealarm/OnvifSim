using System.Numerics;
using OnvifSim.Geometry;
using OnvifSim.World;

namespace OnvifSim.Rendering;

// The world prepared for drawing: geometry with sunlight baked into vertex colours (light never moves,
// so per-frame lighting would be wasted work), plus sky and fog settings. Immutable, so render threads
// share one instance and a world edit simply swaps in a new one.
public sealed class RenderScene
{
    public sealed class Chunk
    {
        public required Vector3[] Positions { get; init; }
        public required Vector3[] Colors { get; init; }
        public required int[] Indices { get; init; }
        // Where each vertex lies in the picture drawn on the chunk, when it is drawn with one.
        public Vector2[]? Uvs { get; init; }
        // The picture itself: a map under the terrain, not a pattern made of colours.
        public Texture? Texture { get; init; }
        // Geometry already coloured as it must be drawn is not lit; light would only make a map harder
        // to read.
        public bool Unlit { get; init; }
        // Pairs of vertices joined by a one-pixel line, drawn after every triangle where nothing hides it,
        // blended in by LineAlpha, and hiding nothing itself.
        public int[] Lines { get; init; } = [];
        public float LineAlpha { get; init; } = 1;
        public Vector3 Center { get; init; }
        public float Radius { get; init; }
    }

    public required IReadOnlyList<Chunk> Chunks { get; init; }
    public required Terrain Terrain { get; init; }
    public Vector3 ToSun { get; init; }
    public Vector3 SkyZenith { get; init; }
    public Vector3 SkyHorizon { get; init; }
    public float FogStart { get; init; }
    public float FogEnd { get; init; }
    public int MaxChunkVertices { get; init; }
    public bool ShowCameras { get; init; }
    public bool ShowFrustums { get; init; }
    public float FrustumLength { get; init; }

    public static RenderScene Build(WorldModel world, Texture? groundMap = null) => Build(world, SceneBuilder.Build(world, groundMap));

    public static RenderScene Build(WorldModel world, IReadOnlyList<SceneChunk> chunks)
    {
        var env = world.Environment;
        float az = env.SunAzimuth * MathF.PI / 180, el = env.SunElevation * MathF.PI / 180;
        // Direction towards the sun: azimuth clockwise from north (-Z).
        var toSun = Vector3.Normalize(new(MathF.Sin(az) * MathF.Cos(el), MathF.Sin(el), -MathF.Cos(az) * MathF.Cos(el)));
        var lit = chunks.Concat(DeviceScene.Static(world)).Select(c => Light(c, toSun)).ToList();
        return new RenderScene
        {
            Chunks = lit,
            Terrain = new Terrain(world.Terrain),
            ToSun = toSun,
            SkyZenith = ColorUtil.Parse(env.SkyZenith, ColorUtil.Rgb(74, 127, 193)),
            SkyHorizon = ColorUtil.Parse(env.SkyHorizon, ColorUtil.Rgb(201, 220, 239)),
            FogStart = env.FogStart,
            FogEnd = Math.Max(env.FogEnd, env.FogStart + 1),
            MaxChunkVertices = lit.Count == 0 ? 0 : lit.Max(c => c.Positions.Length),
            ShowCameras = env.ShowCameras,
            ShowFrustums = env.ShowFrustums,
            FrustumLength = env.FrustumLength,
        };
    }

    // A chunk lit like the rest of the scene: for what moves, built for every frame.
    public Chunk Light(SceneChunk chunk) => Light(chunk, ToSun);

    // A chunk in its own colours, unlit, with lines between pairs of its vertices: for marks drawn into
    // the world rather than things standing in it.
    public static Chunk Unlit(Mesh mesh, float lineAlpha, int[] lines)
    {
        var (center, radius) = mesh.BoundingSphere();
        return new Chunk
        {
            Positions = [.. mesh.Positions], Colors = [.. mesh.Colors], Indices = [.. mesh.Indices], Lines = lines,
            LineAlpha = lineAlpha, Center = center, Radius = radius,
        };
    }

    private static Chunk Light(SceneChunk chunk, Vector3 toSun)
    {
        var skyLight = new Vector3(0.62f, 0.68f, 0.78f);
        var groundLight = new Vector3(0.42f, 0.40f, 0.36f);
        var sunLight = new Vector3(1.0f, 0.95f, 0.85f) * 0.75f;
        var m = chunk.Mesh;
        var colors = new Vector3[m.VertexCount];
        bool unlit = chunk.Texture is not null;
        for (int i = 0; i < colors.Length; i++)
        {
            if (unlit)
            {
                colors[i] = m.Colors[i];
                continue;
            }
            var n = m.Normals[i];
            // Hemisphere ambient plus Lambert sun.
            var ambient = Vector3.Lerp(groundLight, skyLight, n.Y * 0.5f + 0.5f) * 0.55f;
            var light = ambient + sunLight * MathF.Max(0, Vector3.Dot(n, toSun));
            colors[i] = Vector3.Min(m.Colors[i] * light * 1.25f, Vector3.One);
        }
        return new Chunk
        {
            Positions = [.. m.Positions], Colors = colors, Indices = [.. m.Indices],
            Uvs = m.HasUvs ? [.. m.Uvs] : null, Texture = chunk.Texture, Unlit = unlit,
            Center = chunk.Center, Radius = chunk.Radius,
        };
    }
}
