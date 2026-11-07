using System.Net;
using System.Text;
using OnvifSim.World;

namespace OnvifSim.Tests;

// Reading the real world for the editor: one request reads everything inside a world's extent, and
// every click after that is a walk over what was kept. The answer is canned here — no test talks to
// the internet.
public class OsmServiceTests
{
    // The Overpass answer for a small campus block: a building way, a building drawn as a
    // multipolygon with a courtyard, a footway and a tree.
    private const string Answer = """
    {"elements":[
      {"type":"way","id":11,"tags":{"building":"yes","name":"Hall","height":"11.78"},"geometry":[
        {"lat":38.99240,"lon":-76.93790},{"lat":38.99240,"lon":-76.93770},
        {"lat":38.99220,"lon":-76.93770},{"lat":38.99220,"lon":-76.93790},
        {"lat":38.99240,"lon":-76.93790}]},
      {"type":"relation","id":12,"tags":{"building":"yes","name":"Court"},"members":[
        {"type":"way","ref":1,"role":"outer","geometry":[
          {"lat":38.99235,"lon":-76.93785},{"lat":38.99235,"lon":-76.93775},
          {"lat":38.99225,"lon":-76.93775},{"lat":38.99225,"lon":-76.93785},
          {"lat":38.99235,"lon":-76.93785}]},
        {"type":"way","ref":2,"role":"inner","geometry":[
          {"lat":38.99232,"lon":-76.93782},{"lat":38.99232,"lon":-76.93778},
          {"lat":38.99228,"lon":-76.93778},{"lat":38.99228,"lon":-76.93782},
          {"lat":38.99232,"lon":-76.93782}]}]},
      {"type":"way","id":13,"tags":{"highway":"footway","name":"Path"},"geometry":[
        {"lat":38.99230,"lon":-76.93788},{"lat":38.99230,"lon":-76.93784}]},
      {"type":"node","id":14,"tags":{"natural":"tree"},"lat":38.99231,"lon":-76.93781}
    ]}
    """;

    private sealed class StubOverpass : HttpMessageHandler
    {
        public string? LastQuery { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            LastQuery = Uri.UnescapeDataString(body.Replace('+', ' '));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Answer, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static WorldModel World()
    {
        var world = new WorldModel
        {
            Geo = new GeoModel { Latitude = 38.9923, Longitude = -76.9378 },
            Terrain = new TerrainModel { Size = 400, Resolution = 16 },
        };
        world.Terrain.Heights = new float[17 * 17];
        return world;
    }

    private static string Scratch() => Path.Combine(Path.GetTempPath(), "onvifsim-osm-" + Guid.NewGuid().ToString("N")[..8]);

    // One world, one download: the answer is kept beside the ground map's cache, under the world's
    // own name, and never reaches the world file.
    private static async Task<(OsmService Service, OsmService.Area Area, StubOverpass Stub, string Data)> Download()
    {
        var data = Scratch();
        var stub = new StubOverpass();
        var service = new OsmService("https://osm.test/api/interpreter", data, new HttpClient(stub));
        var area = await service.DownloadAsync(World(), "maryland");
        return (service, area, stub, data);
    }

    [Fact]
    public async Task AWorld_IsReadInOneQueryForItsWholeExtent()
    {
        var (service, area, stub, data) = await Download();
        try
        {
            // The whole extent, not a radius around a click: south, west, north, east.
            Assert.Contains("nwr(38.9905", stub.LastQuery);
            Assert.Contains(")[building]", stub.LastQuery);
            Assert.Contains("node(", stub.LastQuery);
            Assert.Contains("out tags geom;", stub.LastQuery);
            Assert.True(area.Objects.Count >= 4);
            Assert.True(File.Exists(service.AreaFile("maryland")), "the objects are not kept on this machine");
            Assert.Contains(Path.Combine("osm", "maryland.json"), service.AreaFile("maryland"));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public async Task ABuilding_IsOfferedWithItsHeightAndName()
    {
        var (service, area, _, data) = await Download();
        try
        {
            var within = OsmService.Pick(area, -5f, -1f);
            var building = within.Single(c => c.OsmId == 11);
            Assert.Equal("building", building.Kind);
            Assert.Equal("way", building.OsmType);
            Assert.Equal("Hall", building.Name);
            Assert.Equal(11.78f, building.Height, 2);
            Assert.Equal(4, building.Points.Count);
            Assert.False(building.HasHoles);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    // A courtyard is a hole: the prism cannot draw one, so the candidate says so and the editor
    // refuses it with a message instead of building a lid over the courtyard.
    [Fact]
    public async Task ABuildingWithACourtyard_SaysItHasHoles()
    {
        var (_, area, _, data) = await Download();
        try
        {
            var within = OsmService.Pick(area, -5f, -1f);
            var court = within.Single(c => c.OsmId == 12);
            Assert.Equal("relation", court.OsmType);
            Assert.True(court.HasHoles);
            Assert.Equal(4, court.Points.Count); // the outer ring only
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public async Task APath_IsOfferedAsARoadOfItsOwnWidth()
    {
        var (_, area, _, data) = await Download();
        try
        {
            var within = OsmService.Pick(area, -5f, -1f);
            var path = within.Single(c => c.OsmId == 13);
            Assert.Equal("highway", path.Kind);
            Assert.Equal(2.5f, path.Width, 2);
            Assert.Equal(2, path.Points.Count);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public async Task ATree_IsOfferedAsAPoint()
    {
        var (_, area, _, data) = await Download();
        try
        {
            var within = OsmService.Pick(area, -5f, -1f);
            var tree = within.Single(c => c.OsmId == 14);
            Assert.Equal("tree", tree.Kind);
            Assert.Single(tree.Points);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    // A click on a footprint is about that footprint, whatever else lies near it: the path passes
    // through the building in this answer, and the building still comes first.
    [Fact]
    public async Task TheFootprintThatHoldsTheClick_ComesBeforeWhatPassesBesideIt()
    {
        var (_, area, _, data) = await Download();
        try
        {
            // Inside the Hall, on the line of the footway.
            var within = OsmService.Pick(area, -4.4f, 0.0f);
            Assert.Equal(11, within[0].OsmId);
            Assert.Contains(within, c => c.OsmId == 13);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    // An object away from the click is not what the click was about.
    [Fact]
    public async Task AnObjectAwayFromTheClick_IsNotOffered()
    {
        var (_, area, _, data) = await Download();
        try
        {
            Assert.Empty(OsmService.Pick(area, -30, -35));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    // The kept reading belongs to the world's extent: move the anchor and it is not this world's
    // objects any more, so a click asks for a fresh reading instead of answering from the old one.
    [Fact]
    public async Task AKeptReading_IsUsedUntilTheWorldsExtentMoves()
    {
        var (service, _, _, data) = await Download();
        try
        {
            Assert.NotNull(service.Cached(World(), "maryland"));

            var moved = World();
            moved.Geo.Latitude += 0.01;
            Assert.Null(service.Cached(moved, "maryland"));

            var grown = World();
            grown.Terrain.Size = 800;
            Assert.Null(service.Cached(grown, "maryland"));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public void ANameThatWouldEscapeTheFolder_IsNotRead()
    {
        var service = new OsmService(null, Scratch(), new HttpClient(new StubOverpass()));
        Assert.Null(service.Cached(World(), "../maryland"));
    }

    [Fact]
    public async Task WithoutAnEndpoint_NothingIsAsked()
    {
        var service = new OsmService(null, Scratch(), new HttpClient(new StubOverpass()));
        Assert.False(service.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(World(), "maryland"));
    }
}
