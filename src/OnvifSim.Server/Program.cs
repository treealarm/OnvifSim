using OnvifSim;
using OnvifSim.Editor;
using OnvifSim.Onvif;
using OnvifSim.Onvif.Services;
using OnvifSim.Ptz;
using OnvifSim.Security;
using OnvifSim.Streaming;
using OnvifSim.Streaming.H264;
using OnvifSim.Streaming.Rtsp;
using OnvifSim.World;

var options = SimOptions.Parse(args);
var store = new WorldStore(options.DataDirectory);

if (options.Bench)
{
    Bench.Run(store.Current);
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
builder.Services.AddSingleton<SceneState>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PtzSimulator>();
builder.Services.AddSingleton(sp => H264Support.Detect(options, sp.GetRequiredService<ILogger<H264Support>>()));
builder.Services.AddSingleton<FeedManager>();
builder.Services.AddSingleton<HttpAuthenticator>();
builder.Services.AddSingleton<RtspServer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RtspServer>());
builder.Services.AddSingleton(sp => new OnvifDispatcher(
    [typeof(DeviceHandlers), typeof(MediaHandlers), typeof(Media2Handlers), typeof(PtzHandlers), typeof(ImagingHandlers)],
    sp.GetRequiredService<HttpAuthenticator>(), sp.GetRequiredService<ILogger<OnvifDispatcher>>()));
builder.Services.AddSingleton<OnvifHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OnvifHost>());
builder.Services.AddHostedService<WsDiscoveryResponder>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    foreach (var c in WorldStore.Json.Converters) o.SerializerOptions.Converters.Add(c);
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapEditorApi();
if (!File.Exists(Path.Combine(webRoot, "index.html")))
    app.MapGet("/", () => "The editor is not built (building it needs Node.js); the API is under /api.");

app.Logger.LogInformation("OnvifSim: data in {Data}, editor at http://localhost:{Port}/", options.DataDirectory, options.WebPort);
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
