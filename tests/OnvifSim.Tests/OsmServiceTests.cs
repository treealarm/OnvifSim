using System.Net;
using System.Text;
using OnvifSim.World;

namespace OnvifSim.Tests;

// Reading the real world for the editor: one click asks Overpass what is there, and this says what of
// it the simulator can build. The answer is canned here — no test talks to the internet.
public class OsmServiceTests
{
    // The Overpass answer for a click at the middle of a small campus block: a building way, a
    // building drawn as a multipolygon with a courtyard, a footway past the point and a tree.
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

    private static async Task<(IReadOnlyList<OsmService.Candidate> Found, StubOverpass Stub)> Pick()
    {
        var stub = new StubOverpass();
        var service = new OsmService("https://osm.test/api/interpreter", new HttpClient(stub));
        return (await service.PickAsync(World(), 0, 0), stub);
    }

    [Fact]
    public async Task AClick_AsksForEveryKindInOneQuery()
    {
        var (_, stub) = await Pick();
        // nwr reaches buildings drawn as relations, a tree is a node: one request answers the click.
        Assert.Contains("nwr(around:30,38.9923,-76.9378)[building]", stub.LastQuery);
        Assert.Contains("node(around:30,38.9923,-76.9378)[natural=tree]", stub.LastQuery);
        Assert.Contains("out tags geom;", stub.LastQuery);
    }

    [Fact]
    public async Task ABuilding_IsOfferedWithItsHeightAndName()
    {
        var (found, _) = await Pick();
        var building = found.Single(c => c.OsmId == 11);
        Assert.Equal("building", building.Kind);
        Assert.Equal("way", building.OsmType);
        Assert.Equal("Hall", building.Name);
        Assert.Equal(11.78f, building.Height, 2);
        Assert.Equal(4, building.Points.Count);
        Assert.False(building.HasHoles);
    }

    // A courtyard is a hole: the prism cannot draw one, so the candidate says so and the editor
    // refuses it with a message instead of building a lid over the courtyard.
    [Fact]
    public async Task ABuildingWithACourtyard_SaysItHasHoles()
    {
        var (found, _) = await Pick();
        var court = found.Single(c => c.OsmId == 12);
        Assert.Equal("relation", court.OsmType);
        Assert.True(court.HasHoles);
        Assert.Equal(4, court.Points.Count); // the outer ring only
    }

    [Fact]
    public async Task APath_IsOfferedAsARoadOfItsOwnWidth()
    {
        var (found, _) = await Pick();
        var path = found.Single(c => c.OsmId == 13);
        Assert.Equal("highway", path.Kind);
        Assert.Equal(2.5f, path.Width, 2);
        Assert.Equal(2, path.Points.Count);
    }

    [Fact]
    public async Task ATree_IsOfferedAsAPoint()
    {
        var (found, _) = await Pick();
        var tree = found.Single(c => c.OsmId == 14);
        Assert.Equal("tree", tree.Kind);
        Assert.Single(tree.Points);
    }

    // An object far from the click is not what the click was about.
    [Fact]
    public async Task AnObjectAwayFromTheClick_IsNotOffered()
    {
        var stub = new StubOverpass();
        var service = new OsmService("https://osm.test/api/interpreter", new HttpClient(stub));
        // 40 m north-west of the block: nothing in the answer reaches there.
        var found = await service.PickAsync(World(), -30, -35);
        Assert.Empty(found);
    }

    [Fact]
    public async Task WithoutAnEndpoint_NothingIsAsked()
    {
        var service = new OsmService(null, new HttpClient(new StubOverpass()));
        Assert.False(service.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PickAsync(World(), 0, 0));
    }
}
