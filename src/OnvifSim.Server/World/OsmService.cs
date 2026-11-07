using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OnvifSim.Geometry;

namespace OnvifSim.World;

// Real objects for a world, taken from OpenStreetMap where someone clicks. A service rather than a
// corner of an endpoint: it asks Overpass, reads the elements, turns their corners into the world's
// own metres and says what of them this simulator can build — so a generator of whole worlds can use
// the same steps later. Nothing here knows about any particular city.
public sealed class OsmService
{
    public const string DefaultEndpoint = "https://overpass-api.de/api/interpreter";
    // How far round the click the query reaches: enough for the object under it and its neighbours.
    private const int RadiusM = 30;
    private const int MaxCandidates = 5;
    private static readonly TimeSpan CacheLife = TimeSpan.FromSeconds(30);
    // One query a second, and never more than this from a response.
    private static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(1);
    private const int MaxResponseBytes = 32 * 1024 * 1024;

    // What the editor can build from one OSM element. A footprint for what stands on the ground, a
    // line for a road, one point for a tree.
    public sealed record Candidate(
        string OsmType, long OsmId, string Kind, string Name,
        IReadOnlyList<GroundPoint> Points, float Height, float Width, bool HasHoles);

    private readonly string? _endpoint;
    private readonly HttpClient _http;
    private readonly ILogger? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastQuery = DateTimeOffset.MinValue;
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<Candidate> Found)> _cache = [];

    public OsmService(string? endpoint, HttpClient http, ILogger? log = null)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint;
        _http = http;
        _log = log;
    }

    public bool Enabled => _endpoint is not null;

    // What lies under a point of the world, nearest first: the objects the editor can offer to build.
    public async Task<IReadOnlyList<Candidate>> PickAsync(WorldModel world, float x, float z, CancellationToken cancellationToken = default)
    {
        if (_endpoint is null)
            throw new InvalidOperationException("No OSM endpoint is configured (ONVIFSIM_OSM_URL).");

        var key = CacheKey(world, x, z);
        lock (_cache)
            if (_cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheLife)
                return hit.Found;

        var point = WorldGeo.ToGeo(world.Geo, new Vec3(x, 0, z));
        var query = Query(point.Latitude, point.Longitude);
        var elements = await QueryAsync(query, cancellationToken);
        var found = CandidatesOf(world, elements, x, z);
        lock (_cache) _cache[key] = (DateTimeOffset.UtcNow, found);
        return found;
    }

    private static string Query(double latitude, double longitude)
    {
        var at = string.Create(CultureInfo.InvariantCulture, $"(around:{RadiusM},{latitude:0.######},{longitude:0.######})");
        // nwr reaches relations too, which is how a building drawn as a multipolygon arrives; a tree
        // is a node. Everything in one request, so a click costs one round trip.
        return $"[out:json][timeout:25];(" +
               $"nwr{at}[building];nwr{at}[amenity=parking];nwr{at}[natural=water];" +
               $"way{at}[highway];node{at}[natural=tree];" +
               ");out tags geom;";
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
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
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
                    throw new HttpRequestException($"OSM answered {(int)response.StatusCode}");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
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

            var rings = new List<List<(double Lat, double Lon)>>();
            var holes = false;
            if (element.TryGetProperty("geometry", out var geometry))
            {
                var ring = ReadGeometry(geometry);
                if (ring.Count > 0) rings.Add(ring);
            }
            // A multipolygon relation carries its rings as members: an outer ring is a part of the
            // object, an inner ring is a hole in it.
            if (element.TryGetProperty("members", out var members))
                foreach (var member in members.EnumerateArray())
                {
                    var role = member.TryGetProperty("role", out var r) ? r.GetString() : null;
                    if (!member.TryGetProperty("geometry", out var memberGeometry)) continue;
                    var ring = ReadGeometry(memberGeometry);
                    if (ring.Count == 0) continue;
                    if (string.Equals(role, "inner", StringComparison.Ordinal)) { holes = true; continue; }
                    rings.Add(ring);
                }
            if (rings.Count > 0)
                elements.Add(new Element(type, id, tags, rings, holes));
            else if (element.TryGetProperty("lat", out var lat) && element.TryGetProperty("lon", out var lon))
                elements.Add(new Element(type, id, tags, [[(lat.GetDouble(), lon.GetDouble())]], false));
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

    // The elements the click can be about: a polygon that contains it, a road within its own width,
    // a tree within a few metres — nearest first, no more than a handful.
    private static List<Candidate> CandidatesOf(WorldModel world, List<Element> elements, float x, float z)
    {
        var click = new GroundPoint(x, z);
        var found = new List<(double Distance, Candidate Candidate)>();
        foreach (var element in elements)
        {
            var kind = KindOf(element.Tags);
            if (kind is null) continue;
            var points = element.Rings[0].Select(p => WorldGeo.ToWorld(world, p.Lat, p.Lon, world.Geo.Elevation))
                .Select(p => new GroundPoint(p.X, p.Z)).ToList();

            if (kind == "tree")
            {
                var distance = MathF.Sqrt((points[0].X - x) * (points[0].X - x) + (points[0].Z - z) * (points[0].Z - z));
                if (distance > 8) continue;
                found.Add((distance, new Candidate(element.Type, element.Id, "tree", string.Empty, points, 12, 0, false)));
                continue;
            }

            if (kind == "highway")
            {
                var width = RoadWidth(element.Tags);
                var distance = DistanceToLine(points, click);
                if (distance > width / 2 + 3) continue;
                found.Add((distance, new Candidate(element.Type, element.Id, "highway", NameOf(element.Tags), points, 0, width, false)));
                continue;
            }

            var ring = Polygon.Normalize(points);
            if (ring.Count < 3 || !Polygon.IsSimple(ring) || MathF.Abs(Polygon.SignedArea(ring)) < Polygon.MinAreaM2) continue;
            if (!Polygon.Contains(ring, click) && DistanceToLine(ring, click) > 2) continue;
            float height = kind == "building" ? BuildingHeight(element.Tags) : 0.05f;
            found.Add((0, new Candidate(element.Type, element.Id, kind, NameOf(element.Tags), ring, height, 0, element.HasHoles)));
        }
        return found.OrderBy(f => f.Distance).Take(MaxCandidates).Select(f => f.Candidate).ToList();
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

    private static string CacheKey(WorldModel world, float x, float z) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{world.Geo.Latitude:0.0000}/{world.Geo.Longitude:0.0000}/{world.Geo.North:0.0}/{world.Terrain.Size:0}/{Math.Floor(x / 5)}/{Math.Floor(z / 5)}");

    private sealed record Element(
        string Type, long Id, Dictionary<string, string> Tags,
        List<List<(double Lat, double Lon)>> Rings, bool HasHoles);
}
