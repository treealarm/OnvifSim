using System.Net;
using System.Text.Json;
using OnvifSim.World;

namespace OnvifSim.Tests;

// The ground map cache: what it covers, how it is fetched, and what a world gets when the map does
// not fit or cannot be had. The tile provider is a stub — no test talks to the internet.
public class GroundMapTests
{
    // Serves a 256×256 tile of one colour that depends on the tile's place, so a picture made of
    // several tiles cannot be mistaken for one flat colour.
    private sealed class StubTiles : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var parts = request.RequestUri!.AbsolutePath.TrimStart('/').Split('/');
            int x = int.Parse(parts[^2]), y = int.Parse(parts[^1].Split('.')[0]);
            Requests++;
            var pixels = new byte[256 * 256 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = (byte)(x % 2 == 0 ? 200 : 20);
                pixels[i + 1] = (byte)(y % 2 == 0 ? 200 : 20);
                pixels[i + 2] = 120;
                pixels[i + 3] = 255;
            }
            using var png = new MemoryStream();
            new StbImageWriteSharp.ImageWriter().WritePng(pixels, 256, 256, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, png);
            var content = new ByteArrayContent(png.ToArray());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static WorldModel World(string name = "test-ground")
    {
        var world = new WorldModel
        {
            Geo = new GeoModel { Latitude = 38.9923, Longitude = -76.9378 },
            Terrain = new TerrainModel { Size = 400, Resolution = 16, Map = name, MapAttribution = "© test" },
        };
        world.Terrain.Heights = new float[17 * 17];
        var bounds = TerrainMaps.BoundsOf(world.Geo, world.Terrain.Size);
        world.Terrain.MapBounds = new MapBoundsModel { South = bounds.South, West = bounds.West, North = bounds.North, East = bounds.East };
        return world;
    }

    private static TerrainMaps Maps(string directory, StubTiles tiles, string url = "https://tiles.test/{z}/{x}/{y}.png") =>
        new(directory, url, new HttpClient(tiles));

    [Fact]
    public void TheTerrainOfAWorld_CoversItsOwnCorners()
    {
        var bounds = TerrainMaps.BoundsOf(new GeoModel { Latitude = 38.9923, Longitude = -76.9378 }, 400);
        // 400 m across at this latitude: about 0.0036° of latitude and 0.0046° of longitude.
        Assert.Equal(38.9941, bounds.North, 4);
        Assert.Equal(38.9905, bounds.South, 4);
        Assert.Equal(-76.9401, bounds.West, 4);
        Assert.Equal(-76.9355, bounds.East, 4);
    }

    [Fact]
    public async Task Baking_WritesThePictureAndWhatItCovers()
    {
        var directory = Directory.CreateTempSubdirectory("onvifsim-maps-").FullName;
        var tiles = new StubTiles();
        var maps = Maps(directory, tiles);
        var world = World();

        var meta = await maps.BakeAsync(world, "test-ground");

        Assert.InRange(meta.Zoom, 1, 19);
        Assert.Equal(world.Terrain.MapBounds!.North, meta.North, 6);
        Assert.Equal("© test", meta.Attribution);
        var picture = Path.Combine(directory, "maps", "test-ground.png");
        Assert.True(File.Exists(picture));
        var image = StbImageSharp.ImageResult.FromMemory(File.ReadAllBytes(picture), StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        Assert.Equal(2048, image.Width);
        Assert.Equal(2048, image.Height);
        // Many tiles, not one colour: the picture is made of the provider's pieces.
        Assert.True(tiles.Requests > 1);
        Assert.True(image.Data.Where((_, i) => i % 4 == 0).Distinct().Count() > 1);
    }

    [Fact]
    public void WithoutABakedMap_TheWorldIsDrawnWithoutOne()
    {
        var directory = Directory.CreateTempSubdirectory("onvifsim-maps-").FullName;
        var tiles = new StubTiles();
        var maps = Maps(directory, tiles);
        // The first ask starts the fetch and answers "not yet"; the world renders as it always did.
        Assert.Null(maps.Get(World()));
    }

    // A map baked for another anchor or size is not drawn at all: half a map in the wrong place is
    // worse than the terrain's own colour.
    [Fact]
    public async Task AMapBakedForAnotherWorld_IsNotDrawn()
    {
        var directory = Directory.CreateTempSubdirectory("onvifsim-maps-").FullName;
        var maps = Maps(directory, new StubTiles());
        await maps.BakeAsync(World(), "test-ground");

        var moved = World();
        moved.Geo.Latitude += 0.01;
        var bounds = TerrainMaps.BoundsOf(moved.Geo, moved.Terrain.Size);
        moved.Terrain.MapBounds = new MapBoundsModel { South = bounds.South, West = bounds.West, North = bounds.North, East = bounds.East };
        Assert.Null(maps.Get(moved));
    }

    [Fact]
    public void AMapNameReachesTheFileSystem_SoItIsChecked()
    {
        Assert.True(TerrainMaps.IsSafeName("maryland-ground"));
        Assert.False(TerrainMaps.IsSafeName("../secret"));
        Assert.False(TerrainMaps.IsSafeName("a/b"));
        Assert.False(TerrainMaps.IsSafeName(""));
    }

    [Fact]
    public void TheCacheNeverLeavesItsDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("onvifsim-maps-").FullName;
        var maps = Maps(directory, new StubTiles());
        Assert.Null(maps.PictureFile("../../etc/passwd"));
        Assert.Null(maps.ReadMeta("../x"));
    }

    [Fact]
    public void NoProvider_MeansNoFetchAndNoMap()
    {
        var directory = Directory.CreateTempSubdirectory("onvifsim-maps-").FullName;
        var tiles = new StubTiles();
        var maps = new TerrainMaps(directory, null, new HttpClient(tiles));
        Assert.Null(maps.Get(World()));
        Assert.Equal(0, tiles.Requests);
    }
}
