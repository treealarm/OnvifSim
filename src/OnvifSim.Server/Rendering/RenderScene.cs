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

    public static RenderScene Build(WorldModel world) => Build(world, SceneBuilder.Build(world));

    public static RenderScene Build(WorldModel world, IReadOnlyList<SceneChunk> chunks)
    {
        var env = world.Environment;
        float az = env.SunAzimuth * MathF.PI / 180, el = env.SunElevation * MathF.PI / 180;
        // Direction towards the sun: azimuth clockwise from north (-Z).
        var toSun = Vector3.Normalize(new(MathF.Sin(az) * MathF.Cos(el), MathF.Sin(el), -MathF.Cos(az) * MathF.Cos(el)));
        var lit = chunks.Select(c => Light(c, toSun)).ToList();
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
        };
    }

    // A chunk lit like the rest of the scene: for what moves, built for every frame.
    public Chunk Light(SceneChunk chunk) => Light(chunk, ToSun);

    private static Chunk Light(SceneChunk chunk, Vector3 toSun)
    {
        var skyLight = new Vector3(0.62f, 0.68f, 0.78f);
        var groundLight = new Vector3(0.42f, 0.40f, 0.36f);
        var sunLight = new Vector3(1.0f, 0.95f, 0.85f) * 0.75f;
        var m = chunk.Mesh;
        var colors = new Vector3[m.VertexCount];
        for (int i = 0; i < colors.Length; i++)
        {
            var n = m.Normals[i];
            // Hemisphere ambient plus Lambert sun.
            var ambient = Vector3.Lerp(groundLight, skyLight, n.Y * 0.5f + 0.5f) * 0.55f;
            var light = ambient + sunLight * MathF.Max(0, Vector3.Dot(n, toSun));
            colors[i] = Vector3.Min(m.Colors[i] * light * 1.25f, Vector3.One);
        }
        return new Chunk
        {
            Positions = [.. m.Positions], Colors = colors, Indices = [.. m.Indices],
            Center = chunk.Center, Radius = chunk.Radius,
        };
    }
}
