using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OnvifSim.Rtsp;

public sealed class RtspServerOptions
{
    // The TCP port of the control connections; 0 takes any free one. Cameras may ask for other ports.
    public int Port { get; init; } = 554;
    // The UDP port RTP goes out from, RTCP from the next one; 0 takes any free pair.
    public int RtpPort { get; init; } = 6970;
    // The Server header of every response.
    public string ServerName { get; init; } = "OnvifSim.Rtsp";
}

// The RTSP server: any number of TCP listeners (the global --rtsp-port plus one per camera that asks
// for its own), all feeding one shared UDP port pair for RTP/RTCP. Owns the sessions, which may outlive
// the connection that created them (UDP).
public sealed class RtspServer(RtspServerOptions options, IRtspMediaSource media, ILogger<RtspServer> logger) : BackgroundService
{
    private static readonly TimeSpan TimeoutCheckInterval = TimeSpan.FromSeconds(5);

    private sealed record Listener(string Key, TcpListener Socket, int RequestedPort, int BoundPort);

    private readonly ConcurrentDictionary<string, RtspSession> _sessions = new();
    private readonly ConcurrentDictionary<string, Listener> _listeners = new();
    private readonly SemaphoreSlim _listenerGate = new(1, 1);
    private CancellationToken _stoppingToken;

    // The bound global port; with port 0 in the options this is the one the OS picked.
    public int Port { get; private set; }
    public int? RtpPort => Udp?.RtpPort;

    internal IRtspMediaSource Media => media;
    internal string ServerName => options.ServerName;
    // Null when the UDP port pair could not be bound; SETUP then offers TCP only.
    internal RtpUdpSockets? Udp { get; private set; }

    // Binds the global listener before the host reports itself started, so a port conflict fails the
    // start and tests can read the actual ports right away.
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var listener = StartListener(null, options.Port)
                       ?? throw new IOException($"Failed to bind RTSP port {options.Port}.");
        _listeners[listener.Key] = listener;
        Port = listener.BoundPort;
        try
        {
            Udp = RtpUdpSockets.Bind(options.RtpPort);
        }
        catch (SocketException ex)
        {
            logger.LogWarning("RTSP over UDP is off: cannot bind UDP ports {Rtp}-{Rtcp}: {Reason}", options.RtpPort, options.RtpPort + 1, ex.Message);
        }
        logger.LogInformation("RTSP at rtsp://localhost:{Port}/, RTP over UDP from ports {Udp}",
            Port, Udp == null ? "(off)" : $"{Udp.RtpPort}-{Udp.RtcpPort}");
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancel the loops first: stopping the listener under a pending accept fails it with a
        // SocketException that would look like a crash.
        await base.StopAsync(cancellationToken);
        await _listenerGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var listener in _listeners.Values) listener.Socket.Stop();
            _listeners.Clear();
        }
        finally
        {
            _listenerGate.Release();
        }
        await Task.WhenAll(_sessions.Values.Select(CloseSessionAsync));
        Udp?.Dispose();
    }

    // The listeners the world needs: a camera's streams are served only on the listener its RTSP port
    // asks for. Called again whenever the world changes; listeners no longer wanted are closed with
    // their sessions, new ones are opened (a port that cannot be taken is logged and left for the next
    // world change).
    public async Task SetEndpointsAsync(IReadOnlyCollection<(IPAddress? Address, int Port)> endpoints)
    {
        await _listenerGate.WaitAsync();
        try
        {
            var wanted = endpoints
                .Select(endpoint => (Key: KeyOf(endpoint.Address, endpoint.Port), endpoint.Address, endpoint.Port))
                .ToDictionary(endpoint => endpoint.Key, endpoint => endpoint);

            foreach (var (key, listener) in _listeners.ToList())
            {
                if (wanted.ContainsKey(key)) continue;
                _listeners.TryRemove(key, out _);
                await StopListenerAsync(listener);
            }

            var started = new List<Listener>();
            foreach (var endpoint in wanted.Values)
            {
                if (_listeners.ContainsKey(endpoint.Key)) continue;
                var listener = StartListener(endpoint.Address, endpoint.Port);
                if (listener == null) continue;
                _listeners[listener.Key] = listener;
                if (endpoint.Address == null && endpoint.Port == options.Port) Port = listener.BoundPort;
                started.Add(listener);
            }

            foreach (var listener in started) StartAcceptLoop(listener);
        }
        finally
        {
            _listenerGate.Release();
        }
    }

    // The port a listener actually took, for the address and requested port the world asked for.
    public int? BoundPortOf(IPAddress? address, int requestedPort)
    {
        return _listeners.TryGetValue(KeyOf(address, requestedPort), out var listener) ? listener.BoundPort : null;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        foreach (var listener in _listeners.Values) StartAcceptLoop(listener);
        return Task.WhenAll(
            Udp?.ReceiveAsync(OnDatagram, stoppingToken) ?? Task.CompletedTask,
            CloseTimedOutSessionsAsync(stoppingToken));
    }

    internal RtspSession? FindSession(string id) => _sessions.GetValueOrDefault(id);

    internal void AddSession(RtspSession session) => _sessions[session.Id] = session;

    internal async Task CloseSessionAsync(RtspSession session)
    {
        if (!_sessions.TryRemove(session.Id, out _)) return;
        await session.StopAsync();
        logger.LogInformation("RTSP session {Session} for {Path} closed", session.Id, session.PathText);
    }

    // Ends the sessions of every stream whose listener port and path match, as if their media had gone:
    // for the application to cut off a source (a camera that lost its network, or a stream that was
    // deleted). Over TCP the connection stays open and simply goes quiet, as it would with the network
    // down.
    public Task CloseSessionsAsync(Func<int, IReadOnlyList<string>, bool> matches) =>
        Task.WhenAll(_sessions.Values.Where(session => matches(session.LocalPort, session.Path)).Select(CloseSessionAsync));

    internal Task CloseSessionsOfAsync(RtspConnection connection) =>
        Task.WhenAll(_sessions.Values
            .Where(s => s.BelongsTo(connection))
            .Select(CloseSessionAsync));

    private void StartAcceptLoop(Listener listener)
    {
        _ = Task.Run(() => AcceptConnectionsAsync(listener, _stoppingToken), CancellationToken.None);
    }

    private async Task AcceptConnectionsAsync(Listener listener, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.Socket.AcceptSocketAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            socket.NoDelay = true;
            _ = ServeAsync(socket, listener.Key, stoppingToken);
        }
    }

    private async Task ServeAsync(Socket socket, string listenerKey, CancellationToken stoppingToken)
    {
        await using var connection = new RtspConnection(socket, this, listenerKey, logger);
        logger.LogDebug("RTSP {Client}: connected", connection.RemoteEndPoint);
        try
        {
            await connection.RunAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RTSP {Client}: connection failed", connection.RemoteEndPoint);
        }
        logger.LogDebug("RTSP {Client}: disconnected", connection.RemoteEndPoint);
    }

    // RTCP receiver reports (or any datagram) from a UDP client keep its session alive.
    private void OnDatagram(IPEndPoint sender)
    {
        foreach (var session in _sessions.Values)
            if (session.IsFromClient(sender))
                session.Touch();
    }

    private async Task CloseTimedOutSessionsAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeoutCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                long now = Environment.TickCount64;
                foreach (var session in _sessions.Values)
                    if (session.IsUdp && session.HasTimedOut(now))
                    {
                        logger.LogInformation("RTSP session {Session}: no keep-alive for {Timeout} s", session.Id, RtspSession.TimeoutSeconds);
                        await CloseSessionAsync(session);
                    }
            }
        }
        catch (OperationCanceledException) { }
    }

    private Listener? StartListener(IPAddress? address, int port)
    {
        TcpListener socket = address == null ? TcpListener.Create(port) : new TcpListener(address, port);
        try
        {
            socket.Start();
        }
        catch (SocketException ex)
        {
            socket.Stop();
            logger.LogError("RTSP cannot listen on {Endpoint}: {Reason}", Describe(address, port), ex.Message);
            return null;
        }
        int bound = ((IPEndPoint)socket.LocalEndpoint).Port;
        logger.LogInformation("RTSP listening on {Endpoint}, port {Port}", address?.ToString() ?? "every interface", bound);
        return new Listener(KeyOf(address, port), socket, port, bound);
    }

    private async Task StopListenerAsync(Listener listener)
    {
        listener.Socket.Stop();
        await Task.WhenAll(_sessions.Values
            .Where(session => session.ListenerKey == listener.Key)
            .Select(CloseSessionAsync));
    }

    private static string KeyOf(IPAddress? address, int port) => $"{address?.ToString() ?? "*"}:{port}";

    private static string Describe(IPAddress? address, int port) => $"{address?.ToString() ?? "every interface"}:{port}";
}
