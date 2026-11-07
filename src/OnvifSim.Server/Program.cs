using OnvifSim;
using OnvifSim.Analytics;
using OnvifSim.Editor;
using OnvifSim.Onvif;
using OnvifSim.Onvif.Events;
using OnvifSim.Onvif.Services;
using OnvifSim.Ptz;
using OnvifSim.Recording;
using OnvifSim.Rtsp;
using OnvifSim.Security;
using OnvifSim.Streaming;
using OnvifSim.Streaming.H264;
using OnvifSim.World;

var options = SimOptions.Parse(args);
// Validation is the editor's rule book, but a world opened from the library must pass it too.
var store = new WorldStore(options.DataDirectory, world => WorldValidation.Check(world, options.OnvifBasePort, options.RtspPort));

if (options.Bench)
{
    var groundMap = store.Current.Terrain.Map is { } mapName ? TerrainMaps.LoadCached(options.DataDirectory, mapName) : null;
    Bench.Run(store.Current, groundMap);
    return;
}

// Write the world the code builds and stop: a world file is the builder's output, never hand-edited.
if (options.WriteWorld is { Length: > 0 } writeWorld)
{
    var built = writeWorld == MarylandWorld.Name
        ? MarylandWorld.Create()
        : throw new ArgumentException($"No builder for the world '{writeWorld}'.");
    var path = Path.Combine(options.DataDirectory, "worlds", writeWorld + ".json");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(built, WorldStore.Json));
    Console.WriteLine($"Wrote {path}");
    return;
}

// The editor is built next to the binary (see OnvifSim.Server.csproj), whatever the working directory.
var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, WebRootPath = webRoot });
builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(options.WebPort));
// The editor makes a request per edit; ASP.NET Core's own per-request logging would bury everything else.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(store);
// The two things the world asks the internet for: a raster map under the terrain, and real objects
// when someone picks them in the editor. Both identify themselves, and both are optional.
builder.Services.AddSingleton(sp =>
{
    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd($"OnvifSim/{CameraIdentity.FirmwareVersion} (+https://treealarm.dev)");
    return new TerrainMaps(options.DataDirectory, options.MapTiles, http, sp.GetRequiredService<ILogger<TerrainMaps>>());
});
builder.Services.AddSingleton(sp =>
{
    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd($"OnvifSim/{CameraIdentity.FirmwareVersion} (+https://treealarm.dev)");
    return new OsmService(options.OsmEndpoint, http, sp.GetRequiredService<ILogger<OsmService>>());
});
builder.Services.AddSingleton<SceneState>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PtzSimulator>();
builder.Services.AddSingleton<DeviceIoController>();
builder.Services.AddSingleton(sp => H264Support.Detect(options, sp.GetRequiredService<ILogger<H264Support>>()));
builder.Services.AddSingleton<FeedManager>();
builder.Services.AddSingleton<AnalyticsManager>();
builder.Services.AddSingleton<PullPoints>();
builder.Services.AddSingleton<RecordingLog>();
builder.Services.AddSingleton<CameraNetwork>();
builder.Services.AddSingleton<ReplayStreams>();
builder.Services.AddSingleton<HttpAuthenticator>();
builder.Services.AddSingleton<RtspEndpoints>();
builder.Services.AddSingleton<CameraStreams>();
builder.Services.AddSingleton<IRtspMediaSource>(sp => sp.GetRequiredService<CameraStreams>());
builder.Services.AddSingleton(new RtspServerOptions
{
    Port = options.RtspPort, RtpPort = options.RtpPort, ServerName = "OnvifSim/" + CameraIdentity.FirmwareVersion,
});
builder.Services.AddSingleton<RtspServer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RtspServer>());
builder.Services.AddSingleton<RtspHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RtspHost>());
builder.Services.AddSingleton(sp => new OnvifDispatcher(
    [typeof(DeviceHandlers), typeof(DeviceIoHandlers), typeof(MediaHandlers), typeof(Media2Handlers), typeof(PtzHandlers),
     typeof(ImagingHandlers), typeof(EventHandlers), typeof(PullPointHandlers), typeof(SubscriptionManagerHandlers),
     typeof(RecordingHandlers), typeof(SearchHandlers), typeof(ReplayHandlers)],
    sp.GetRequiredService<HttpAuthenticator>(), sp.GetRequiredService<ILogger<OnvifDispatcher>>()));
builder.Services.AddSingleton<OnvifHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OnvifHost>());
builder.Services.AddHostedService<WsDiscoveryResponder>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    foreach (var c in WorldStore.Json.Converters) o.SerializerOptions.Converters.Add(c);
});

var app = builder.Build();

// A camera that loses its network loses its streams too: they stop, as packets would. The recording
// log is created here rather than on first use, so that cameras record from the start.
var rtspServer = app.Services.GetRequiredService<RtspServer>();
var cameraStreams = app.Services.GetRequiredService<CameraStreams>();
var network = app.Services.GetRequiredService<CameraNetwork>();
network.WentOffline += cameraId =>
{
    _ = rtspServer.CloseSessionsAsync((port, path) =>
        string.Equals(cameraStreams.CameraIdOf(port, path), cameraId, StringComparison.OrdinalIgnoreCase));
};
// A stream whose path or listener went away takes its sessions with it; RtspHost closes them once the
// new listeners are known.
// A new world starts with every camera online, and a replay belongs to the world it was recorded in.
store.Switched += _ => network.Reset();
store.Switched += switched =>
{
    _ = rtspServer.CloseSessionsAsync((port, path) =>
        path is [_, var last] && string.Equals(last, ReplayStreams.PathSegment, StringComparison.OrdinalIgnoreCase));
};
app.Services.GetRequiredService<RecordingLog>();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapEditorApi();
if (!File.Exists(Path.Combine(webRoot, "index.html")))
    app.MapGet("/", () => "The editor is not built (building it needs Node.js); the API is under /api.");

var editorUrl = $"http://localhost:{options.WebPort}/";
app.Logger.LogInformation("OnvifSim: data in {Data}, editor at {Url}", options.DataDirectory, editorUrl);
if (options.OpenBrowser) app.Lifetime.ApplicationStarted.Register(() => Browser.Open(editorUrl, app.Logger));
try
{
    app.Run();
}
catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
{
    // Almost always a port in use, typically by another OnvifSim: say so instead of crashing.
    app.Logger.LogCritical("OnvifSim cannot start: {Reason}. Is another instance running? " +
        "Ports can be moved with --port, --rtsp-port, --rtp-port and --onvif-base-port.", ex.Message);
    Environment.ExitCode = 1;
}

public partial class Program;
