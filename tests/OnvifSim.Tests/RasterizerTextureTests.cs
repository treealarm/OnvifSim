using System.Numerics;
using OnvifSim.Geometry;
using OnvifSim.Rendering;
using OnvifSim.World;

namespace OnvifSim.Tests;

// Drawing a picture on geometry: sampling it, and the whole path from a mesh's UVs through the
// rasterizer to a pixel — the ground map under the world depends on both.
public class RasterizerTextureTests
{
    // A picture split down the middle: red on the left, blue on the right, as bands four pixels wide.
    private static Texture LeftRedRightBlue()
    {
        const int size = 8;
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int o = (y * size + x) * 4;
                bool left = x < size / 2;
                px[o] = left ? (byte)255 : (byte)0;
                px[o + 1] = 0;
                px[o + 2] = left ? (byte)0 : (byte)255;
                px[o + 3] = 255;
            }
        return new Texture { Width = size, Height = size, Rgba = px };
    }

    [Fact]
    public void SamplingTheMiddle_IsTheBoundaryBetweenTheBands()
    {
        var tex = LeftRedRightBlue();
        tex.Sample(0.25f, 0.5f, out var r, out var g, out var b);
        Assert.Equal(1f, r, 2);
        Assert.Equal(0f, g, 2);
        Assert.Equal(0f, b, 2);
        tex.Sample(0.75f, 0.5f, out r, out g, out b);
        Assert.Equal(0f, r, 2);
        Assert.Equal(1f, b, 2);
        // Bilinear at the seam: half of each.
        tex.Sample(0.5f, 0.5f, out r, out g, out b);
        Assert.Equal(0.5f, r, 1);
        Assert.Equal(0.5f, b, 1);
    }

    // A ground quad drawn from above shows the part of the picture under the camera: the same mesh and
    // picture, two places, two colours.
    [Theory]
    [InlineData(-50f, true)]
    [InlineData(50f, false)]
    public void TheGroundShowsThePictureUnderTheCamera(float cameraX, bool expectRed)
    {
        var world = new WorldModel { Terrain = new TerrainModel { Size = 200, Resolution = 16 } };
        world.Terrain.Heights = new float[17 * 17];

        var mesh = new Mesh();
        // A quad of ±100 m, its corners placed at the picture's corners: u east, v south.
        mesh.AddVertex(new Vector3(-100, 0.05f, -100), Vector3.UnitY, Vector3.One, new Vector2(0, 0));
        mesh.AddVertex(new Vector3(100, 0.05f, -100), Vector3.UnitY, Vector3.One, new Vector2(1, 0));
        mesh.AddVertex(new Vector3(-100, 0.05f, 100), Vector3.UnitY, Vector3.One, new Vector2(0, 1));
        mesh.AddVertex(new Vector3(100, 0.05f, 100), Vector3.UnitY, Vector3.One, new Vector2(1, 1));
        mesh.AddTriangle(0, 2, 1);
        mesh.AddTriangle(1, 2, 3);
        var chunk = new SceneChunk { Name = "ground", Mesh = mesh, Center = Vector3.Zero, Radius = 150, Texture = LeftRedRightBlue() };

        var scene = RenderScene.Build(world, [chunk]);
        var frame = new Frame(160, 90);
        var pose = new CameraPose(new Vector3(cameraX, 20, 0), 0, -90, 0, 60);
        new SoftwareRasterizer().Render(scene, [], pose, frame);

        int o = (45 * frame.Width + 80) * 4;
        var (r, g, b) = (frame.Rgba[o], frame.Rgba[o + 1], frame.Rgba[o + 2]);
        if (expectRed)
            Assert.True(r > 200 && b < 40, $"expected red, got ({r},{g},{b})");
        else
            Assert.True(b > 200 && r < 40, $"expected blue, got ({r},{g},{b})");
    }

    // The same picture painted over the whole terrain across the seam of its chunks: the colours must
    // not change from one chunk to the next, because the place comes from the world, not from the chunk.
    [Fact]
    public void TerrainChunks_CarryTheSamePlacementAcrossTheirSeam()
    {
        var world = new WorldModel { Terrain = new TerrainModel { Size = 400, Resolution = 32 } };
        world.Terrain.Heights = new float[33 * 33];
        var chunks = SceneBuilder.Build(world, LeftRedRightBlue());
        var west = chunks.Single(c => c.Name == "terrain_0_0").Mesh;
        var east = chunks.Single(c => c.Name == "terrain_16_0").Mesh;
        // The seam is x = 0: the west chunk's eastern vertices and the east chunk's western ones stand
        // on the same ground and carry the same place in the picture.
        int w = west.Positions.FindIndex(p => p.X == 0f && p.Z == west.Positions[0].Z);
        int e = east.Positions.FindIndex(p => p.X == 0f && p.Z == east.Positions[0].Z);
        Assert.True(w >= 0 && e >= 0);
        Assert.Equal(west.Uvs[w], east.Uvs[e]);
        Assert.Equal(0.5f, west.Uvs[w].X, 3);
    }
}
