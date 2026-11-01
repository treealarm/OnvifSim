using System.Net;
using OnvifSim.Rtsp;
using OnvifSim.World;

namespace OnvifSim.Streaming;

// Keeps the RTSP listeners equal to what the world asks for: every camera's streams are served on the
// port it names, or on the global --rtsp-port for cameras that keep the default. The listeners follow
// the world like the ONVIF ones do; one port that cannot be taken leaves the others working.
public sealed class RtspHost(
    SimOptions options, WorldStore store, RtspServer server, CameraStreams streams, RtspEndpoints endpoints, ILogger<RtspHost> logger)
    : IHostedService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _stopped;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReconcileAsync();
        store.Changed += OnWorldChanged;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        store.Changed -= OnWorldChanged;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _stopped = true;
            await server.SetEndpointsAsync([]);
            endpoints.Set(new Dictionary<string, int>());
        }
        finally
        {
            _gate.Release();
        }
    }

    // Called inside the store's lock: hand the work off, and let the reconcile read the latest world.
    private void OnWorldChanged(WorldModel world) => _ = ReconcileAsync();

    private async Task ReconcileAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_stopped) return;
            var world = store.Current;
            var wanted = new Dictionary<(string Address, int Port), (IPAddress? Address, int Port)>();
            var cameraEndpoints = new Dictionary<string, (IPAddress? Address, int Port)>(StringComparer.OrdinalIgnoreCase);
            foreach (var camera in world.Cameras)
            {
                if (CameraStreamsModel.Effective(camera).Count == 0) continue;
                IPAddress? address = string.IsNullOrWhiteSpace(camera.BindAddress) ? null : IPAddress.Parse(camera.BindAddress.Trim());
                int port = camera.RtspPort != 0 ? camera.RtspPort : options.RtspPort;
                wanted[(address?.ToString() ?? "*", port)] = (address, port);
                cameraEndpoints[camera.Id] = (address, port);
            }

            await server.SetEndpointsAsync(wanted.Values.ToList());

            var ports = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (cameraId, endpoint) in cameraEndpoints)
                if (server.BoundPortOf(endpoint.Address, endpoint.Port) is { } bound)
                    ports[cameraId] = bound;
            endpoints.Set(ports);
            // Sessions of a stream or camera that is no longer there end with it.
            await server.CloseSessionsAsync((port, path) => !streams.Exists(world, port, path));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RTSP listeners could not follow the world change");
        }
        finally
        {
            _gate.Release();
        }
    }
}
