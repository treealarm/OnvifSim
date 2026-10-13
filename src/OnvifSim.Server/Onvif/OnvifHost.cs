using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using OnvifSim.Ptz;
using OnvifSim.Security;
using OnvifSim.Streaming;
using OnvifSim.Streaming.Rtsp;
using OnvifSim.World;

namespace OnvifSim.Onvif;

// Serves every camera's ONVIF services on a port of its own, as separate devices would be: SOAP at
// /onvif/{service} and the snapshot at /onvif/snapshot.jpg. Listeners follow the world: cameras that
// are added, removed or moved to another port are picked up whenever the world changes.
public sealed class OnvifHost(
    SimOptions options, WorldStore store, SceneState scene, PtzSimulator ptz, OnvifDispatcher dispatcher, FeedManager feeds,
    HttpAuthenticator authenticator, RtspServer rtsp, ILoggerFactory loggers) : IHostedService
{
    private sealed record Listener(int RequestedPort, int Port, WebApplication App);

    private const long MaxRequestBytes = 1024 * 1024;

    private readonly ILogger _logger = loggers.CreateLogger<OnvifHost>();
    private readonly Dictionary<string, Listener> _listeners = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private bool _stopped;

    public event Action? ListenersChanged;

    // The port a camera's ONVIF service listens on, or null when it is not served.
    public int? PortOf(string cameraId)
    {
        lock (_listeners) return _listeners.TryGetValue(cameraId, out var listener) ? listener.Port : null;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReconcileAsync();
        store.Changed += OnWorldChanged;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        store.Changed -= OnWorldChanged;
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            _stopped = true;
            List<Listener> all;
            lock (_listeners)
            {
                all = [.. _listeners.Values];
                _listeners.Clear();
            }
            await Task.WhenAll(all.Select(l => StopListenerAsync(l)));
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    // The port a camera asks for: its own, or the next one after the base port in camera order.
    // 0 lets the OS pick, which the tests use.
    public static int RequestedPort(WorldModel world, CameraModel camera, int basePort) =>
        camera.OnvifPort != 0 ? camera.OnvifPort
        : basePort == 0 ? 0
        : basePort + world.Cameras.IndexOf(camera);

    // Called inside the store's lock: hand the work off, and let the reconcile read the latest world.
    private void OnWorldChanged(WorldModel world) => _ = ReconcileAsync();

    private async Task ReconcileAsync()
    {
        await _reconcileGate.WaitAsync();
        try
        {
            if (_stopped) return;
            var world = store.Current;
            var wanted = world.Cameras.ToDictionary(c => c.Id, c => RequestedPort(world, c, options.OnvifBasePort), StringComparer.OrdinalIgnoreCase);

            List<(string Id, Listener Listener)> obsolete;
            lock (_listeners)
                obsolete = _listeners
                    .Where(l => !wanted.TryGetValue(l.Key, out int port) || port != l.Value.RequestedPort)
                    .Select(l => (l.Key, l.Value))
                    .ToList();
            foreach (var (id, listener) in obsolete)
            {
                lock (_listeners) _listeners.Remove(id);
                await StopListenerAsync(listener);
            }

            foreach (var (id, port) in wanted)
            {
                bool running;
                lock (_listeners) running = _listeners.ContainsKey(id);
                if (!running) await StartListenerAsync(id, port);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ONVIF listeners could not follow the world change");
        }
        finally
        {
            _reconcileGate.Release();
        }
        ListenersChanged?.Invoke();
    }

    private async Task StartListenerAsync(string cameraId, int port)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddLogging();
        builder.Services.AddSingleton(loggers);
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(port);
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = MaxRequestBytes;
        });
        var app = builder.Build();
        app.Run(http => HandleAsync(http, cameraId));
        try
        {
            await app.StartAsync();
        }
        catch (IOException ex)
        {
            // Typically the port is taken; the other cameras keep working.
            _logger.LogError("Camera {Camera}: cannot serve ONVIF on port {Port}: {Reason}", cameraId, port, ex.Message);
            await app.DisposeAsync();
            return;
        }
        int boundPort = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
        lock (_listeners) _listeners[cameraId] = new Listener(port, boundPort, app);
        _logger.LogInformation("Camera {Camera}: ONVIF at http://localhost:{Port}/onvif/device_service", cameraId, boundPort);
    }

    private static async Task StopListenerAsync(Listener listener)
    {
        await listener.App.StopAsync();
        await listener.App.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext http, string cameraId)
    {
        var camera = scene.FindCamera(cameraId);
        var path = http.Request.Path.Value ?? "";
        if (camera == null || !path.StartsWith("/onvif/", StringComparison.OrdinalIgnoreCase))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (HttpMethods.IsGet(http.Request.Method) && path.Equals("/onvif/snapshot.jpg", StringComparison.OrdinalIgnoreCase))
        {
            await SnapshotAsync(http, camera);
            return;
        }
        if (!HttpMethods.IsPost(http.Request.Method))
        {
            http.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        await dispatcher.HandleAsync(http, new OnvifRequestContext(camera, store, ptz, dispatcher, http.Request, rtsp.Port, feeds.H264Available));
    }

    // The URL GetSnapshotUri hands out, protected by the camera's users like everything else.
    private async Task SnapshotAsync(HttpContext http, CameraModel camera)
    {
        if (!authenticator.IsAuthorized(http.Request.Method, http.Request.Headers.Authorization.ToString(), camera.Users))
        {
            foreach (var challenge in authenticator.Challenges()) http.Response.Headers.Append("WWW-Authenticate", challenge);
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var jpeg = feeds.Snapshot(camera.Id);
        if (jpeg == null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        http.Response.ContentType = "image/jpeg";
        http.Response.ContentLength = jpeg.Length;
        await http.Response.Body.WriteAsync(jpeg, http.RequestAborted);
    }
}
