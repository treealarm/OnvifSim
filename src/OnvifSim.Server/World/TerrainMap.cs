using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OnvifSim.Rendering;
using StbImageSharp;
using StbImageWriteSharp;
using SharpColor = StbImageSharp.ColorComponents;
using WriteColor = StbImageWriteSharp.ColorComponents;

namespace OnvifSim.World;

// A raster map drawn under a world. The picture itself is a local cache under the data directory —
// never part of a world file and never shipped — and is fetched once from the configured tile
// provider for the world's own extent. A map that cannot be had is simply not drawn: the terrain keeps
// its own colour and the world stays valid.
//
// The picture is a function of where a point is on the Earth, not a square stretched over a square:
// every output pixel is a world coordinate taken back to WGS84 and then to a tile, so the world's
// anchor, its true north and any terrain size all come out right.
public sealed class TerrainMaps
{
    public const string DefaultTileUrl = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
    private const string DefaultAttribution = "© OpenStreetMap contributors";
    // The cache picture's side in pixels, and how fine it should be on the ground (metres per pixel).
    private const int PictureSide = 2048;
    private const double TargetMetresPerPixel = 0.2;
    // A world bigger than this many tiles gets a coarser picture rather than a hundred thousand requests.
    private const int MaxTiles = 256;

    public sealed record Meta(
        double South, double West, double North, double East, int Zoom, string Attribution, DateTimeOffset FetchedAt);

    private sealed record Bounds(double South, double West, double North, double East);
    private sealed record Loaded(string Name, Texture Texture, Meta Meta);

    private readonly string _directory;
    private readonly string? _tileUrl;
    private readonly HttpClient? _http;
    private readonly ILogger? _log;
    private readonly Lock _gate = new();
    // One bake at a time: a world whose extent just changed makes the scene ask for its picture
    // while the editor asks for the same one, and two writers over one file is a lock, not a picture.
    private readonly SemaphoreSlim _bakeGate = new(1, 1);
    private readonly Dictionary<string, Task> _fetching = [];
    private readonly Dictionary<string, DateTimeOffset> _failedAt = [];
    private readonly HashSet<string> _reported = [];
    private volatile Loaded? _loaded;
    // How long a map that could not be fetched is left alone before it is tried again: an offline box
    // must not ask every time a camera is rebuilt, and a box that comes online must not wait forever.
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    // Raised when a map has been loaded: the scene that has no picture yet is built again.
    public event Action? Changed;

    // tileUrlTemplate is the raster provider — any {z}/{x}/{y} service, not necessarily OSM. null or
    // empty turns ground maps off entirely, and no request is ever made.
    public TerrainMaps(string dataDirectory, string? tileUrlTemplate, HttpClient? http = null, ILogger? log = null)
    {
        _directory = Path.Combine(dataDirectory, "maps");
        _tileUrl = string.IsNullOrWhiteSpace(tileUrlTemplate) ? null : tileUrlTemplate;
        _http = http;
        _log = log;
    }

    // The WGS84 rectangle the terrain of a world covers: the bounding box of its four corners, so a
    // rotated world (a true north that is not 0) still gets a correct rectangle.
    public static Meta BoundsOf(GeoModel geo, float size, int zoom = 0, string attribution = DefaultAttribution)
    {
        var b = BoundsFor(geo, size);
        return new Meta(b.South, b.West, b.North, b.East, zoom, attribution, DateTimeOffset.UnixEpoch);
    }

    // The cached picture for the world's terrain, or null until it is there (a fetch is started the
    // first time it is asked for). A map whose bounds no longer match the world is not drawn at all.
    public Texture? Get(WorldModel world)
    {
        var name = world.Terrain.Map;
        if (string.IsNullOrWhiteSpace(name) || world.Terrain.ShowMap == false) return null;
        // Where the picture belongs is the world's own extent, so a world that holds no bounds — or
        // holds the ones of the extent before it moved — is still read for the one it has.
        var wanted = Wanted(world);
        var loaded = _loaded;
        if (loaded is not null && loaded.Name == name && SameBounds(loaded.Meta, wanted)) return loaded.Texture;
        StartFetch(world, name, wanted);
        return null;
    }

    // Downloads the map for this world again, whatever is cached, and returns what it covers. The
    // editor calls this when someone asks for a fresh picture; a world switch does not.
    public async Task<Meta> BakeAsync(WorldModel world, string name, CancellationToken cancellationToken = default)
    {
        if (_tileUrl is null || _http is null)
            throw new InvalidOperationException("No tile provider is configured (ONVIFSIM_MAP_TILE_URL).");
        if (string.IsNullOrWhiteSpace(name) || !IsSafeName(name))
            throw new ArgumentException($"'{name}' is not a map name.");

        await _bakeGate.WaitAsync(cancellationToken);
        try
        {
            return await BakeLocked(world, name, cancellationToken);
        }
        finally
        {
            _bakeGate.Release();
        }
    }

    private async Task<Meta> BakeLocked(WorldModel world, string name, CancellationToken cancellationToken)
    {
        var bounds = Wanted(world);
        int zoom = ChooseZoom(bounds);
        var tiles = await DownloadAsync(bounds, zoom, cancellationToken);

        var attribution = string.IsNullOrWhiteSpace(world.Terrain.MapAttribution) ? DefaultAttribution : world.Terrain.MapAttribution;
        var rgba = new byte[PictureSide * PictureSide * 4];
        var geo = world.Geo;
        float half = world.Terrain.Size / 2;
        for (int py = 0; py < PictureSide; py++)
        {
            float z = -half + (py + 0.5f) / PictureSide * world.Terrain.Size;
            for (int px = 0; px < PictureSide; px++)
            {
                float x = -half + (px + 0.5f) / PictureSide * world.Terrain.Size;
                var geoPosition = WorldGeo.ToGeo(geo, new Vec3(x, 0, z));
                var (gx, gy) = MercatorPixels(geoPosition.Latitude, geoPosition.Longitude, zoom);
                SampleMosaic(tiles, gx, gy, out var r, out var g, out var b);
                int o = (py * PictureSide + px) * 4;
                rgba[o] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = 255;
            }
        }

        Directory.CreateDirectory(_directory);
        await using (var stream = File.Create(PicturePath(name)))
            new ImageWriter().WritePng(rgba, PictureSide, PictureSide, WriteColor.RedGreenBlueAlpha, stream);

        var meta = new Meta(bounds.South, bounds.West, bounds.North, bounds.East, zoom, attribution,
            DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(MetaPath(name), JsonSerializer.Serialize(meta, MetaJson), cancellationToken);
        _loaded = new Loaded(name, new Texture { Width = PictureSide, Height = PictureSide, Rgba = rgba }, meta);
        Changed?.Invoke();
        return meta;
    }

    // The cached map's metadata, for the editor: what is on disk and what it covers.
    public Meta? ReadMeta(string name)
    {
        if (!IsSafeName(name)) return null;
        try
        {
            var path = MetaPath(name);
            return File.Exists(path) ? JsonSerializer.Deserialize<Meta>(File.ReadAllText(path), MetaJson) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The cached picture of a map, decoded, or null when it is not there: what a bench or a test
    // needs without a network or a world behind it.
    public static Texture? LoadCached(string dataDirectory, string name)
    {
        if (!IsSafeName(name)) return null;
        var path = Path.Combine(dataDirectory, "maps", name + ".png");
        return File.Exists(path) ? Decode(path) : null;
    }

    public string? PictureFile(string name) =>
        IsSafeName(name) && File.Exists(PicturePath(name)) ? PicturePath(name) : null;

    // A map name reaches the filesystem: letters, digits and dashes only, so it can never leave the
    // cache directory.
    public static bool IsSafeName(string name) =>
        name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static readonly JsonSerializerOptions MetaJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private string PicturePath(string name) => Path.Combine(_directory, name + ".png");

    private string MetaPath(string name) => Path.Combine(_directory, name + ".json");

    // The rectangle the picture is read for: the world's own extent, worked out from where the world
    // stands — not the bounds the world happens to hold. Those are the record of the last picture
    // baked for it, and a world that moved holds the old one until a bake succeeds.
    private static Bounds Wanted(WorldModel world) => BoundsFor(world.Geo, world.Terrain.Size);

    private static Bounds BoundsFor(GeoModel geo, float size)
    {
        float half = size / 2;
        double south = double.MaxValue, west = double.MaxValue, north = double.MinValue, east = double.MinValue;
        foreach (var (x, z) in new[] { (-half, -half), (half, -half), (half, half), (-half, half) })
        {
            var p = WorldGeo.ToGeo(geo, new Vec3(x, 0, z));
            south = Math.Min(south, p.Latitude);
            north = Math.Max(north, p.Latitude);
            west = Math.Min(west, p.Longitude);
            east = Math.Max(east, p.Longitude);
        }
        return new Bounds(south, west, north, east);
    }

    private static bool SameBounds(Meta meta, Bounds bounds) =>
        Math.Abs(meta.South - bounds.South) < 1e-6 && Math.Abs(meta.West - bounds.West) < 1e-6 &&
        Math.Abs(meta.North - bounds.North) < 1e-6 && Math.Abs(meta.East - bounds.East) < 1e-6;

    private void StartFetch(WorldModel world, string name, Bounds wanted)
    {
        if (!IsSafeName(name))
        {
            ReportOnce(name, $"'{name}' is not a map name; no ground map is drawn.");
            return;
        }
        lock (_gate)
        {
            if (_fetching.ContainsKey(name)) return;
            if (_failedAt.TryGetValue(name, out var failedAt) && DateTimeOffset.UtcNow - failedAt < RetryAfter) return;
            _fetching[name] = Task.Run(() => FetchAsync(world, name, wanted));
        }
    }

    private async Task FetchAsync(WorldModel world, string name, Bounds wanted)
    {
        try
        {
            if (_loaded is { } loaded && loaded.Name == name && SameBounds(loaded.Meta, wanted)) return;
            var meta = ReadMeta(name);
            if (meta is null || !SameBounds(meta, wanted) || !File.Exists(PicturePath(name)))
            {
                if (_tileUrl is null || _http is null)
                {
                    ReportOnce(name, "no tile provider is configured; the ground is drawn in its own colour.");
                    return;
                }
                meta = await BakeAsync(world, name, CancellationToken.None);
                return; // BakeAsync has loaded and announced it.
            }
            var texture = Decode(PicturePath(name));
            _loaded = new Loaded(name, texture, meta);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            lock (_gate) _failedAt[name] = DateTimeOffset.UtcNow;
            ReportOnce(name, $"the ground map '{name}' could not be fetched: {ex.Message}");
        }
        finally
        {
            lock (_gate) _fetching.Remove(name);
        }
    }

    private static Texture Decode(string path)
    {
        var image = ImageResult.FromMemory(File.ReadAllBytes(path), SharpColor.RedGreenBlueAlpha);
        if (image.Width <= 0 || image.Height <= 0 || image.Width > 4096 || image.Height > 4096)
            throw new InvalidOperationException($"The map picture {image.Width}×{image.Height} is not something to draw.");
        return new Texture { Width = image.Width, Height = image.Height, Rgba = image.Data };
    }

    private void ReportOnce(string name, string message)
    {
        lock (_gate)
            if (!_reported.Add(name)) return;
        _log?.LogWarning("{Message}", message);
    }

    // ── Tiles ─────────────────────────────────────────────────────────────────

    private int ChooseZoom(Bounds bounds)
    {
        double middleLatitude = (bounds.South + bounds.North) / 2;
        double resolution = 156543.03392 * Math.Cos(middleLatitude * Math.PI / 180); // metres per pixel at z0
        int zoom = (int)Math.Round(Math.Log2(resolution / TargetMetresPerPixel));
        zoom = Math.Clamp(zoom, 1, 19);
        while (zoom > 1 && TileCount(bounds, zoom) > MaxTiles) zoom--;
        return zoom;
    }

    private static int TileCount(Bounds bounds, int zoom)
    {
        var (minX, minY) = TileOf(bounds.North, bounds.West, zoom);
        var (maxX, maxY) = TileOf(bounds.South, bounds.East, zoom);
        return (maxX - minX + 1) * (maxY - minY + 1);
    }

    private static (int X, int Y) TileOf(double latitude, double longitude, int zoom)
    {
        double n = 1 << zoom;
        double x = (longitude + 180) / 360 * n;
        double rad = latitude * Math.PI / 180;
        double y = (1 - Math.Log(Math.Tan(rad) + 1 / Math.Cos(rad)) / Math.PI) / 2 * n;
        return ((int)Math.Floor(x), (int)Math.Floor(y));
    }

    private static (double X, double Y) MercatorPixels(double latitude, double longitude, int zoom)
    {
        double n = 1 << zoom;
        double x = (longitude + 180) / 360 * n * 256;
        double rad = latitude * Math.PI / 180;
        double y = (1 - Math.Log(Math.Tan(rad) + 1 / Math.Cos(rad)) / Math.PI) / 2 * n * 256;
        return (x, y);
    }

    // Every tile the rectangle touches, decoded: two downloads at a time, as the provider's policy asks.
    private async Task<Dictionary<(int X, int Y), byte[]>> DownloadAsync(Bounds bounds, int zoom, CancellationToken cancellationToken)
    {
        var (minX, minY) = TileOf(bounds.North, bounds.West, zoom);
        var (maxX, maxY) = TileOf(bounds.South, bounds.East, zoom);
        var results = new System.Collections.Concurrent.ConcurrentDictionary<(int X, int Y), byte[]>();
        using var slots = new SemaphoreSlim(2, 2);
        int failures = 0;
        var tasks = new List<Task>();
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                var key = (x, y);
                tasks.Add(Task.Run(async () =>
                {
                    await slots.WaitAsync(cancellationToken);
                    try
                    {
                        var bytes = await FetchTileAsync(key, zoom, cancellationToken);
                        results[key] = bytes;
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref failures);
                    }
                    finally
                    {
                        slots.Release();
                    }
                }, cancellationToken));
            }
        await Task.WhenAll(tasks);
        if (results.IsEmpty)
            throw new InvalidOperationException("the tile provider answered none of the requests");
        if (failures > 0)
            _log?.LogWarning("{Count} of the map's tiles could not be fetched; those patches are left blank.", failures);

        var tiles = new Dictionary<(int X, int Y), byte[]>();
        foreach (var (key, bytes) in results)
        {
            var image = ImageResult.FromMemory(bytes, SharpColor.RedGreenBlueAlpha);
            if (image.Width != 256 || image.Height != 256) continue;
            tiles[key] = image.Data;
        }
        return tiles;
    }

    private async Task<byte[]> FetchTileAsync((int X, int Y) tile, int zoom, CancellationToken cancellationToken)
    {
        var url = _tileUrl!
            .Replace("{s}", "a", StringComparison.Ordinal)
            .Replace("{z}", zoom.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{x}", tile.X.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{y}", tile.Y.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        using var response = await _http!.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{url}: {(int)response.StatusCode}");
        var type = response.Content.Headers.ContentType?.MediaType;
        // A throttling or captive-portal page is not a tile.
        if (type is not null && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException($"{url}: answered {type}");
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static void SampleMosaic(Dictionary<(int X, int Y), byte[]> tiles, double gx, double gy,
        out byte r, out byte g, out byte b)
    {
        int x0 = (int)Math.Floor(gx), y0 = (int)Math.Floor(gy);
        double tx = gx - x0, ty = gy - y0;
        Sample(tiles, x0, y0, out var r00, out var g00, out var b00);
        Sample(tiles, x0 + 1, y0, out var r10, out var g10, out var b10);
        Sample(tiles, x0, y0 + 1, out var r01, out var g01, out var b01);
        Sample(tiles, x0 + 1, y0 + 1, out var r11, out var g11, out var b11);
        r = (byte)Mix(Mix(r00, r10, tx), Mix(r01, r11, tx), ty);
        g = (byte)Mix(Mix(g00, g10, tx), Mix(g01, g11, tx), ty);
        b = (byte)Mix(Mix(b00, b10, tx), Mix(b01, b11, tx), ty);
    }

    private static void Sample(Dictionary<(int X, int Y), byte[]> tiles, int px, int py, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        int tileX = (int)Math.Floor(px / 256.0), tileY = (int)Math.Floor(py / 256.0);
        if (!tiles.TryGetValue((tileX, tileY), out var data)) return;
        int localX = px - tileX * 256, localY = py - tileY * 256;
        int o = (localY * 256 + localX) * 4;
        r = data[o];
        g = data[o + 1];
        b = data[o + 2];
    }

    private static double Mix(double a, double b, double t) => a + (b - a) * t;
}
