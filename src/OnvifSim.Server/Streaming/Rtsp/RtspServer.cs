using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using OnvifSim.Analytics;
using OnvifSim.Security;
using OnvifSim.World;

namespace OnvifSim.Streaming.Rtsp;

// The RTSP server: one TCP port for every camera (rtsp://host:8554/{cameraId}) and one shared UDP port
// pair for RTP/RTCP. Owns the sessions, which may outlive the connection that created them (UDP).
public sealed class RtspServer(
    SimOptions options, SceneState scene, FeedManager feeds, AnalyticsManager analytics, HttpAuthenticator authenticator,
    ILogger<RtspServer> logger) : BackgroundService
{
    private static readonly TimeSpan TimeoutCheckInterval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, RtspSession> _sessions = new();
    private TcpListener? _listener;

    // The bound ports; with port 0 in the options these are the ones the OS picked.
    public int Port { get; private set; }
    public int? RtpPort => Udp?.RtpPort;

    public SceneState Scene => scene;
    public FeedManager Feeds => feeds;
    public AnalyticsManager Analytics => analytics;
    public HttpAuthenticator Authenticator => authenticator;
    // Null when the UDP port pair could not be bound; SETUP then offers TCP only.
    public RtpUdpSockets? Udp { get; private set; }

    // Binds before the host reports itself started, so a port conflict fails the start and tests can
    // read the actual ports right away.
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = TcpListener.Create(options.RtspPort);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        try
        {
            Udp = RtpUdpSockets.Bind(options.RtpPort);
        }
        catch (SocketException ex)
        {
            logger.LogWarning("RTSP over UDP is off: cannot bind UDP ports {Rtp}-{Rtcp}: {Reason}", options.RtpPort, options.RtpPort + 1, ex.Message);
        }
        logger.LogInformation("RTSP at rtsp://localhost:{Port}/{{camera}}, RTP over UDP from ports {Udp}",
            Port, Udp == null ? "(off)" : $"{Udp.RtpPort}-{Udp.RtcpPort}");
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancel the loops first: stopping the listener under a pending accept fails it with a
        // SocketException that would look like a crash.
        await base.StopAsync(cancellationToken);
        _listener?.Stop();
        await Task.WhenAll(_sessions.Values.Select(CloseSessionAsync));
        Udp?.Dispose();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(
            AcceptConnectionsAsync(stoppingToken),
            Udp?.ReceiveAsync(OnDatagram, stoppingToken) ?? Task.CompletedTask,
            CloseTimedOutSessionsAsync(stoppingToken));

    public RtspSession? FindSession(string id) => _sessions.GetValueOrDefault(id);

    public void AddSession(RtspSession session) => _sessions[session.Id] = session;

    public async Task CloseSessionAsync(RtspSession session)
    {
        if (!_sessions.TryRemove(session.Id, out _)) return;
        await session.StopAsync();
        logger.LogInformation("RTSP session {Session} for {Camera} closed", session.Id, session.CameraId);
    }

    // A TCP session cannot outlive its connection: its packets have nowhere else to go.
    public Task CloseSessionsOfAsync(RtspConnection connection) =>
        Task.WhenAll(_sessions.Values
            .Where(s => s.BelongsTo(connection))
            .Select(CloseSessionAsync));

    private async Task AcceptConnectionsAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener!.AcceptSocketAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            socket.NoDelay = true;
            _ = ServeAsync(socket, stoppingToken);
        }
    }

    private async Task ServeAsync(Socket socket, CancellationToken stoppingToken)
    {
        await using var connection = new RtspConnection(socket, this, logger);
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
}
