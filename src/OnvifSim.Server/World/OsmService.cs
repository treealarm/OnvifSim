using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OnvifSim.Geometry;

namespace OnvifSim.World;

// Real objects for a world, taken from OpenStreetMap once per world and read from this machine after.
// A service rather than a corner of an endpoint: it asks Overpass for the whole extent of the world,
// reads the elements, turns their corners into the world's own metres and says what of them this
// simulator can build — so a generator of whole worlds can use the same steps later. Nothing here
// knows about any particular city.
//
// One download rather than one query a click: a public Overpass takes tens of seconds at a busy
// hour, and a click that waits that long reads as a click that does nothing. The answer is kept
// beside the ground map's own cache and never reaches the world file, so a world stays a small piece
// of JSON and a machine without the internet still has what it fetched.
public sealed class OsmService
{
    public const string DefaultEndpoint = "https://overpass-api.de/api/interpreter";
    // How long the provider may spend on the query, in its own budget and as the client's patience. A
    // public Overpass under load takes tens of seconds at a peak; a click that gives up sooner is a
    // click that never answers.
    private const int QueryTimeoutS = 60;
    private const int MaxCandidates = 5;
    // One query a second, and never more than this from a response.
    private static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(1);
    private const int MaxResponseBytes = 32 * 1024 * 1024;

    // What the editor can build from one OSM element. A footprint for what stands on the ground, a
    // line for a road, one point for a tree. A footprint's Holes are its courtyards, each stored the
    // other way round from the outline (see Polygon.NormalizeHole).
    public sealed record Candidate(
        string OsmType, long OsmId, string Kind, string Name,
        IReadOnlyList<GroundPoint> Points, float Height, float Width,
        IReadOnlyList<IReadOnlyList<GroundPoint>> Holes);

    // Everything of the real world inside one world's extent, in that world's own metres: the WGS84
    // rectangle it was read from (so a moved anchor or a resized terrain is noticed) and when. The
    // Version is the shape of this record: a reading in an older shape is thrown away and read again,
    // because it does not know a courtyard's rings.
    public sealed record Area(
        int Version, double South, double West, double North, double East,
        DateTimeOffset FetchedAt, IReadOnlyList<Candidate> Objects)
    {
        public const int SchemaVersion = 2;
    }

    private readonly string? _endpoint;
    private readonly string _dataDirectory;
    private readonly HttpClient _http;
    private readonly ILogger? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastQuery = DateTimeOffset.MinValue;

    public OsmService(string? endpoint, string dataDirectory, HttpClient http, ILogger? log = null)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint;
        _dataDirectory = dataDirectory;
        _http = http;
        _log = log;
    }

    public bool Enabled => _endpoint is not null;

    // Where a world's objects are kept: beside its ground map, in the same data directory, under the
    // world's own name. A cache of this machine, never a part of the world.
    public string AreaFile(string name) => Path.Combine(_dataDirectory, "osm", name + ".json");

    // The world's objects as they were last fetched, or null when nothing was fetched yet, the file is
    // unreadable, or the world's extent has moved since (another anchor, another size).
    public Area? Cached(WorldModel world, string name)
    {
        if (!TerrainMaps.IsSafeName(name) || !File.Exists(AreaFile(name))) return null;
        Area? area;
        try
        {
            area = JsonSerializer.Deserialize<Area>(File.ReadAllText(AreaFile(name)), WorldStore.Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _log?.LogWarning("osm: {File} is unreadable: {Message}", AreaFile(name), ex.Message);
            return null;
        }
        if (area is null || area.Version != Area.SchemaVersion) return null;
        var bounds = TerrainMaps.BoundsOf(world.Geo, world.Terrain.Size);
        // Half a metre of slack: the bounds come from the same formula both times.
        bool same = Math.Abs(bounds.South - area.South) < 5e-7 && Math.Abs(bounds.North - area.North) < 5e-7
            && Math.Abs(bounds.West - area.West) < 5e-7 && Math.Abs(bounds.East - area.East) < 5e-7;
        return same ? area : null;
    }

    // Reads the world's whole extent from the provider in one request and keeps it. One world, one
    // download: after this a click costs nothing.
    public async Task<Area> DownloadAsync(WorldModel world, string name, CancellationToken cancellationToken = default)
    {
        if (_endpoint is null)
            throw new InvalidOperationException("No OSM endpoint is configured (ONVIFSIM_OSM_URL).");
        if (!TerrainMaps.IsSafeName(name))
            throw new ArgumentException("A world's name must be letters, digits and dashes.", nameof(name));

        var bounds = TerrainMaps.BoundsOf(world.Geo, world.Terrain.Size);
        var elements = await QueryAsync(Query(bounds.South, bounds.West, bounds.North, bounds.East), cancellationToken);
        var area = new Area(Area.SchemaVersion, bounds.South, bounds.West, bounds.North, bounds.East,
            DateTimeOffset.UtcNow, CandidatesOf(world, elements));

        var file = AreaFile(name);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var writing = file + ".tmp";
        File.WriteAllText(writing, JsonSerializer.Serialize(area, WorldStore.Json));
        File.Move(writing, file, overwrite: true);
        _log?.LogInformation("osm: {Count} objects of the real world kept for {World} in {File}",
            area.Objects.Count, name, file);
        return area;
    }

    private static string Query(double south, double west, double north, double east)
    {
        var box = string.Create(CultureInfo.InvariantCulture, $"({south:0.######},{west:0.######},{north:0.######},{east:0.######})");
        // nwr reaches relations too, which is how a building drawn as a multipolygon arrives; a tree
        // is a node. Everything in one request. "body" is what prints a relation's members, and a
        // courtyard is an inner member: "tags geom" answers a relation with no members at all.
        return $"[out:json][timeout:{QueryTimeoutS}];(" +
               $"nwr{box}[building];nwr{box}[amenity=parking];nwr{box}[natural=water];" +
               $"way{box}[highway];node{box}[natural=tree];" +
               ");out body geom;";
    }

    private async Task<List<Element>> QueryAsync(string query, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // The provider asks for a slow, identified client; a second between queries is the least a
            // click deserves.
            var wait = MinimumGap - (DateTimeOffset.UtcNow - _lastQuery);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            _lastQuery = DateTimeOffset.UtcNow;

            for (int attempt = 0; ; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(QueryTimeoutS + 15));
                using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("data", query)]);
                using var response = await _http.PostAsync(_endpoint, content, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                    if (bytes.Length > MaxResponseBytes)
                        throw new InvalidOperationException("the answer is too large to read");
                    return Parse(bytes);
                }
                // A busy or broken provider gets one more try; anything else is the client's problem.
                bool retryable = (int)response.StatusCode is 429 or >= 500;
                if (!retryable || attempt > 0)
                    throw new HttpRequestException((int)response.StatusCode == 429
                        ? "OpenStreetMap is busy right now (HTTP 429); try again in a minute."
                        : $"OpenStreetMap answered HTTP {(int)response.StatusCode}.");
                // A rate limit is a queue that has to drain; a broken server is worth asking again at
                // once.
                await Task.Delay(TimeSpan.FromSeconds((int)response.StatusCode == 429 ? 10 : 1), cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<Element> Parse(byte[] bytes)
    {
        var elements = new List<Element>();
        using var document = JsonDocument.Parse(bytes);
        if (!document.RootElement.TryGetProperty("elements", out var list)) return elements;
        foreach (var element in list.EnumerateArray())
        {
            var type = element.GetProperty("type").GetString() ?? "";
            long id = element.GetProperty("id").GetInt64();
            var tags = new Dictionary<string, string>();
            if (element.TryGetProperty("tags", out var tagObject))
                foreach (var tag in tagObject.EnumerateObject())
                    tags[tag.Name] = tag.Value.GetString() ?? "";

            var outers = new List<List<(double Lat, double Lon)>>();
            var inners = new List<List<(double Lat, double Lon)>>();
            if (element.TryGetProperty("geometry", out var geometry))
            {
                var ring = ReadGeometry(geometry);
                if (ring.Count > 0) outers.Add(ring);
            }
            // A multipolygon relation carries its rings as members: an outer ring is a part of the
            // object, an inner ring — a courtyard — is a hole in it.
            if (element.TryGetProperty("members", out var members))
                foreach (var member in members.EnumerateArray())
                {
                    var role = member.TryGetProperty("role", out var r) ? r.GetString() : null;
                    if (!member.TryGetProperty("geometry", out var memberGeometry)) continue;
                    var ring = ReadGeometry(memberGeometry);
                    if (ring.Count == 0) continue;
                    if (string.Equals(role, "inner", StringComparison.Ordinal)) inners.Add(ring);
                    else outers.Add(ring);
                }
            if (outers.Count > 0)
                elements.Add(new Element(type, id, tags, outers, inners));
            else if (element.TryGetProperty("lat", out var lat) && element.TryGetProperty("lon", out var lon))
                elements.Add(new Element(type, id, tags, [[(lat.GetDouble(), lon.GetDouble())]], []));
        }
        return elements;
    }

    private static List<(double Lat, double Lon)> ReadGeometry(JsonElement geometry)
    {
        var ring = new List<(double, double)>();
        foreach (var corner in geometry.EnumerateArray())
        {
            if (!corner.TryGetProperty("lat", out var lat) || !corner.TryGetProperty("lon", out var lon)) continue;
            ring.Add((lat.GetDouble(), lon.GetDouble()));
        }
        return ring;
    }

    // What of these elements this simulator can build, in the world's own metres. Everything the
    // world's extent holds: picking afterwards is a walk over this list, not another request. A
    // footprint whose courtyard cannot be built honestly is left out rather than offered without it:
    // a lid over a courtyard is worse than a missing building.
    private List<Candidate> CandidatesOf(WorldModel world, List<Element> elements)
    {
        var found = new List<Candidate>();
        foreach (var element in elements)
        {
            var kind = KindOf(element.Tags);
            if (kind is null || element.Outers.Count == 0) continue;
            var points = ToWorld(world, element.Outers[0]);

            if (kind == "tree")
            {
                found.Add(new Candidate(element.Type, element.Id, "tree", string.Empty, [points[0]], 12, 0, []));
                continue;
            }

            if (kind == "highway")
            {
                if (points.Count < 2) continue;
                found.Add(new Candidate(element.Type, element.Id, "highway", NameOf(element.Tags), points, 0,
                    RoadWidth(element.Tags), []));
                continue;
            }

            var ring = Polygon.Normalize(points);
            if (ring.Count < 3 || !Polygon.IsSimple(ring) || MathF.Abs(Polygon.SignedArea(ring)) < Polygon.MinAreaM2) continue;

            var holes = new List<IReadOnlyList<GroundPoint>>();
            bool buildable = true;
            foreach (var inner in element.Inners)
            {
                var hole = Polygon.NormalizeHole(ToWorld(world, inner));
                if (hole.Count < 3 || !Polygon.IsSimple(hole) ||
                    MathF.Abs(Polygon.SignedArea(hole)) < Polygon.MinAreaM2 ||
                    !Polygon.HoleFits(ring, hole) ||
                    holes.Any(other => Polygon.RingsCross(other, hole) ||
                                       Polygon.Contains(other, hole[0]) || Polygon.Contains(hole, other[0])))
                {
                    _log?.LogWarning("osm: {Type}/{Id} has a courtyard this world cannot hold; it is not offered",
                        element.Type, element.Id);
                    buildable = false;
                    break;
                }
                holes.Add(hole);
            }
            if (!buildable) continue;
            float net = MathF.Abs(Polygon.SignedArea(ring)) - holes.Sum(h => MathF.Abs(Polygon.SignedArea(h)));
            if (net < Polygon.MinAreaM2) continue;

            float height = kind == "building" ? BuildingHeight(element.Tags) : 0.05f;
            found.Add(new Candidate(element.Type, element.Id, kind, NameOf(element.Tags), ring, height, 0, holes));
        }
        return found;
    }

    private static List<GroundPoint> ToWorld(WorldModel world, List<(double Lat, double Lon)> ring) =>
        ring.Select(p => WorldGeo.ToWorld(world, p.Lat, p.Lon, world.Geo.Elevation))
            .Select(p => new GroundPoint(p.X, p.Z)).ToList();

    // What lies under a point of the world, nearest first: the footprint that holds the point comes
    // before anything beside it, then a road within its own width and a tree within a few metres. A
    // walk over the world's own objects, so a click answers at once and the nearest thing is not
    // guessed from what happened to be within a radius.
    public static IReadOnlyList<Candidate> Pick(Area area, float x, float z)
    {
        var click = new GroundPoint(x, z);
        var found = new List<(int Rank, double Distance, Candidate Candidate)>();
        foreach (var candidate in area.Objects)
        {
            int rank;
            double distance;
            switch (candidate.Kind)
            {
                case "tree":
                    if (candidate.Points.Count == 0) continue;
                    distance = Distance(candidate.Points[0], click);
                    if (distance > 8) continue;
                    rank = 1;
                    break;
                case "highway":
                    distance = DistanceToLine(candidate.Points, click) - candidate.Width / 2;
                    if (distance > 3) continue;
                    rank = 1;
                    break;
                default:
                    // A footprint that holds the point is what the click is about, whatever else
                    // passes near it; one whose edge is a couple of metres away is still offered,
                    // but after it.
                    if (Polygon.Contains(candidate.Points, click)) (rank, distance) = (0, 0);
                    else
                    {
                        // A footprint is a ring: the edge from its last corner back to the first is
                        // an edge like the others.
                        distance = DistanceToRing(candidate.Points, click);
                        if (distance > 2) continue;
                        rank = 1;
                    }
                    break;
            }
            found.Add((rank, distance, candidate));
        }
        return found.OrderBy(f => f.Rank).ThenBy(f => f.Distance).Take(MaxCandidates)
            .Select(f => f.Candidate).ToList();
    }

    private static float Distance(GroundPoint a, GroundPoint b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    // The distance to a ring, closing edge included: a normalized footprint does not repeat its
    // first corner at the end.
    private static float DistanceToRing(IReadOnlyList<GroundPoint> ring, GroundPoint p)
    {
        if (ring.Count < 2) return Distance(ring.Count == 1 ? ring[0] : default, p);
        float best = DistanceToSegment(ring[^1], ring[0], p);
        for (int i = 0; i + 1 < ring.Count; i++)
            best = MathF.Min(best, DistanceToSegment(ring[i], ring[i + 1], p));
        return best;
    }

    // What an element becomes in this world, or null for everything this simulator does not build.
    private static string? KindOf(Dictionary<string, string> tags)
    {
        if (tags.ContainsKey("building:part")) return "building";
        if (tags.ContainsKey("building")) return "building";
        if (tags.TryGetValue("amenity", out var amenity) && amenity == "parking") return "parking";
        if (tags.TryGetValue("natural", out var natural) && natural == "water") return "water";
        if (tags.TryGetValue("natural", out natural) && natural == "tree") return "tree";
        if (tags.ContainsKey("highway")) return "highway";
        return null;
    }

    private static string NameOf(Dictionary<string, string> tags) => tags.TryGetValue("name", out var name) ? name : "";

    // Height in metres: what OSM says, else the storeys it counts, else a storey block of four.
    private static float BuildingHeight(Dictionary<string, string> tags)
    {
        if (tags.TryGetValue("height", out var height) && float.TryParse(height.TrimEnd('m', ' '), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var metres) && metres is > 0 and <= 200)
            return metres;
        if (tags.TryGetValue("building:levels", out var levels) && float.TryParse(levels, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var storeys) && storeys is > 0 and < 60)
            return storeys * 3.2f;
        return 12;
    }

    // Width in metres: what OSM says, else the lanes it counts, else what the road kind usually is.
    private static float RoadWidth(Dictionary<string, string> tags)
    {
        if (tags.TryGetValue("width", out var width) && float.TryParse(width.TrimEnd('m', ' '), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var metres) && metres is >= 1 and <= 50)
            return metres;
        if (tags.TryGetValue("lanes", out var lanes) && float.TryParse(lanes, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var count) && count is > 0 and <= 16)
            return Math.Clamp(count * 3.25f, 3, 50);
        return tags.TryGetValue("highway", out var kind) && kind is "footway" or "path" or "steps" or "cycleway" or "pedestrian"
            ? 2.5f
            : 7;
    }

    private static float DistanceToLine(IReadOnlyList<GroundPoint> points, GroundPoint p)
    {
        float best = float.MaxValue;
        for (int i = 0; i + 1 < points.Count; i++)
            best = MathF.Min(best, DistanceToSegment(points[i], points[i + 1], p));
        return best;
    }

    private static float DistanceToSegment(GroundPoint a, GroundPoint b, GroundPoint p)
    {
        float dx = b.X - a.X, dz = b.Z - a.Z;
        float lengthSquared = dx * dx + dz * dz;
        float t = lengthSquared <= 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Z - a.Z) * dz) / lengthSquared, 0, 1);
        float x = a.X + dx * t, z = a.Z + dz * t;
        return MathF.Sqrt((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z));
    }

    private sealed record Element(
        string Type, long Id, Dictionary<string, string> Tags,
        List<List<(double Lat, double Lon)>> Outers, List<List<(double Lat, double Lon)>> Inners);
}
