using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OnvifSim.Streaming.Rtp;
using OnvifSim.World;

namespace OnvifSim.Streaming.Rtsp;

// One RTSP control connection. Requests are answered in order; RTP of sessions set up over TCP shares
// the socket, so every write goes through one lock.
//
// URLs: rtsp://host:port/{cameraId} is the stream, {cameraId}/track1 its only track. A session set up
// on this connection over TCP ends with it; a UDP session outlives it until TEARDOWN or timeout, as
// RFC 2326 intends.
public sealed class RtspConnection : IAsyncDisposable
{
    private const string TrackName = "track1";
    private const string PublicMethods = "OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER, SET_PARAMETER";
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly RtspServer _server;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _closed = new();

    public RtspConnection(Socket socket, RtspServer server, ILogger logger)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _server = server;
        _logger = logger;
        RemoteEndPoint = RtpUdpSockets.Normalize((IPEndPoint)socket.RemoteEndPoint!);
        LocalEndPoint = RtpUdpSockets.Normalize((IPEndPoint)socket.LocalEndPoint!);
    }

    public IPEndPoint RemoteEndPoint { get; }
    public IPEndPoint LocalEndPoint { get; }

    public async Task RunAsync(CancellationToken serverStopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverStopping, _closed.Token);
        var reader = new RtspRequestReader(_stream);
        try
        {
            while (await reader.ReadAsync(linked.Token) is { } request)
            {
                _logger.LogDebug("RTSP {Client}: {Method} {Url}", RemoteEndPoint, request.Method, request.Url);
                var (response, session) = await HandleAsync(request);
                await WriteAsync(response.Serialize(request.CSeq), linked.Token);
                // RTP must not reach the client before the PLAY response does.
                if (request.Method == "PLAY" && response.Status == 200 && session != null)
                    StartPlaying(session);
            }
        }
        catch (InvalidDataException ex)
        {
            _logger.LogInformation("RTSP {Client}: closing after a malformed request: {Reason}", RemoteEndPoint, ex.Message);
            await TryWriteAsync(new RtspResponse(400).Serialize("0"));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
    }

    // Sends a response or interleaved packets. A client that stops reading for WriteTimeout is dropped,
    // so a stalled player cannot pin a sending loop forever.
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        timeout.CancelAfter(WriteTimeout);
        await _writeLock.WaitAsync(timeout.Token);
        try
        {
            await _stream.WriteAsync(data, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (_closed.IsCancellationRequested) throw new IOException("The connection is closed.");
            Close();
            throw new IOException($"The client stopped reading for {WriteTimeout.TotalSeconds:0} s.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Close() => _closed.Cancel();

    public async ValueTask DisposeAsync()
    {
        Close();
        await _server.CloseSessionsOfAsync(this);
        await _stream.DisposeAsync();
    }

    private async Task TryWriteAsync(byte[] data)
    {
        try
        {
            await WriteAsync(data, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
    }

    private async Task<(RtspResponse Response, RtspSession? Session)> HandleAsync(RtspRequest request)
    {
        if (request.Header("Require") is { } required)
            return (new RtspResponse(551).With("Unsupported", required), null);

        RtspSession? session = null;
        if (request.SessionId is { } sessionId)
        {
            session = _server.FindSession(sessionId);
            if (session == null) return (new RtspResponse(454), null);
            session.Touch();
        }

        RtspResponse response;
        if (request.Method == "SETUP")
            (response, session) = Setup(request, session);
        else response = request.Method switch
        {
            "OPTIONS" => new RtspResponse(200).With("Public", PublicMethods),
            "DESCRIBE" => Describe(request),
            "PLAY" => Play(session),
            "PAUSE" => await PauseAsync(session),
            "TEARDOWN" => await TeardownAsync(session),
            "GET_PARAMETER" => new RtspResponse(200),
            "SET_PARAMETER" => new RtspResponse(request.Body.Length == 0 ? 200 : 451),
            _ => new RtspResponse(501).With("Public", PublicMethods),
        };
        if (session != null && request.Method != "TEARDOWN")
            response.With("Session", $"{session.Id};timeout={RtspSession.TimeoutSeconds}");
        return (response, session);
    }

    private RtspResponse Describe(RtspRequest request)
    {
        if (!TryResolve(request.Url, out var camera, out var streamUrl)) return new RtspResponse(404);
        if (!IsAuthorized(request, camera)) return Unauthorized();
        return new RtspResponse(200)
            .With("Content-Base", streamUrl + "/")
            .WithBody("application/sdp", BuildSdp(camera, streamUrl));
    }

    private (RtspResponse, RtspSession?) Setup(RtspRequest request, RtspSession? existing)
    {
        if (!TryResolve(request.Url, out var camera, out _)) return (new RtspResponse(404), existing);
        if (existing != null)
        {
            // One track only, so a repeated SETUP can only confirm what is already set up.
            var confirmation = string.Equals(existing.CameraId, camera.Id, StringComparison.OrdinalIgnoreCase)
                ? new RtspResponse(200).With("Transport", existing.TransportHeader)
                : new RtspResponse(459);
            return (confirmation, existing);
        }
        if (!IsAuthorized(request, camera)) return (Unauthorized(), null);

        var transport = NegotiateTransport(request.Header("Transport"));
        if (transport == null) return (new RtspResponse(461), null);

        var session = new RtspSession(camera.Id, request.Url, transport, _logger);
        _server.AddSession(session);
        _logger.LogInformation("RTSP {Client}: session {Session} for {Camera} over {Transport}",
            RemoteEndPoint, session.Id, camera.Id, transport is InterleavedTransport ? "TCP" : "UDP");
        return (new RtspResponse(200).With("Transport", session.TransportHeader), session);
    }

    private RtspResponse Play(RtspSession? session)
    {
        if (session == null) return new RtspResponse(454);
        if (_server.Scene.FindCamera(session.CameraId) == null) return new RtspResponse(404);

        var stream = session.Stream;
        uint rtpTime = stream.HasSentFrames ? stream.RtpTimeAt(Stopwatch.GetTimestamp()) : stream.InitialTimestamp;
        return new RtspResponse(200)
            .With("Range", "npt=0.000-")
            .With("RTP-Info", $"url={session.TrackUrl};seq={stream.NextSequenceNumber};rtptime={rtpTime}");
    }

    private void StartPlaying(RtspSession session)
    {
        var feed = _server.Feeds.Get(session.CameraId);
        if (feed == null) return;
        session.Play(feed, failed =>
        {
            _ = _server.CloseSessionAsync(failed);
            // Over TCP the stream and the control connection are one: a client whose stream ended
            // learns it from the connection closing.
            if (failed.Transport is InterleavedTransport) Close();
        });
    }

    private static async Task<RtspResponse> PauseAsync(RtspSession? session)
    {
        if (session == null) return new RtspResponse(454);
        await session.StopAsync();
        return new RtspResponse(200);
    }

    private async Task<RtspResponse> TeardownAsync(RtspSession? session)
    {
        if (session == null) return new RtspResponse(454);
        await _server.CloseSessionAsync(session);
        return new RtspResponse(200);
    }

    // Takes the first transport the client offers that the server can do; multicast is not offered.
    private IRtpTransport? NegotiateTransport(string? header)
    {
        if (header == null) return null;
        foreach (var spec in header.Split(','))
        {
            var parts = spec.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Contains("multicast", StringComparer.OrdinalIgnoreCase)) continue;
            var parameters = parts.Skip(1)
                .Select(p => p.Split('=', 2))
                .ToDictionary(kv => kv[0], kv => kv.Length > 1 ? kv[1] : "", StringComparer.OrdinalIgnoreCase);

            switch (parts[0].ToUpperInvariant())
            {
                case "RTP/AVP/TCP":
                    var (rtpChannel, rtcpChannel) = parameters.TryGetValue("interleaved", out var channels)
                        ? ParseRange(channels, 255) ?? (-1, -1)
                        : (0, 1);
                    if (rtpChannel < 0) continue;
                    return new InterleavedTransport(this, (byte)rtpChannel, (byte)rtcpChannel);

                case "RTP/AVP" or "RTP/AVP/UDP":
                    if (_server.Udp == null || !parameters.TryGetValue("client_port", out var ports)) continue;
                    if (ParseRange(ports, IPEndPoint.MaxPort) is not var (rtpPort, rtcpPort) || rtpPort == 0) continue;
                    return new UdpTransport(_server.Udp, RemoteEndPoint.Address, rtpPort, rtcpPort);
            }
        }
        return null;
    }

    // "a-b" or "a" (meaning a-(a+1)), both within 0..max.
    private static (int First, int Second)? ParseRange(string text, int max)
    {
        var bounds = text.Split('-');
        if (!int.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out int first)) return null;
        int second = first + 1;
        if (bounds.Length > 1 && !int.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out second)) return null;
        return first <= max && second <= max ? (first, second) : null;
    }

    private bool IsAuthorized(RtspRequest request, CameraModel camera)
    {
        if (_server.Authenticator.IsAuthorized(request.Method, request.Header("Authorization"), camera.Users)) return true;
        if (request.Header("Authorization") != null)
            _logger.LogInformation("RTSP {Client}: wrong credentials for {Camera}", RemoteEndPoint, camera.Id);
        return false;
    }

    private RtspResponse Unauthorized()
    {
        var response = new RtspResponse(401);
        foreach (var challenge in _server.Authenticator.Challenges()) response.With("WWW-Authenticate", challenge);
        return response;
    }

    // Maps a request URL to a camera. streamUrl is the URL of the camera's stream as the client
    // addresses this server (scheme, host and port from the request, no credentials or query), so
    // the control URLs in the SDP work through NAT and port mapping.
    private bool TryResolve(string url, out CameraModel camera, out string streamUrl)
    {
        camera = null!;
        streamUrl = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is 0 or > 2) return false;
        if (segments.Length == 2 && segments[1] != TrackName) return false;
        if (_server.Scene.FindCamera(Uri.UnescapeDataString(segments[0])) is not { } found) return false;
        camera = found;
        streamUrl = $"{uri.Scheme}://{uri.Authority}/{segments[0]}";
        return true;
    }

    private string BuildSdp(CameraModel camera, string streamUrl)
    {
        var address = LocalEndPoint.Address;
        string addressType = address.AddressFamily == AddressFamily.InterNetworkV6 ? "IP6" : "IP4";
        var sdp = new StringBuilder()
            .Append("v=0\r\n")
            .Append($"o=- {Random.Shared.NextInt64(1, long.MaxValue)} 1 IN {addressType} {address}\r\n")
            .Append($"s={(camera.Name.Length > 0 ? camera.Name : camera.Id)}\r\n")
            .Append($"c=IN {addressType} {(addressType == "IP6" ? "::" : "0.0.0.0")}\r\n")
            .Append("t=0 0\r\n")
            .Append("a=control:*\r\n")
            .Append("a=range:npt=0-\r\n")
            .Append($"m=video 0 RTP/AVP {JpegRtpStream.PayloadType}\r\n")
            .Append($"a=rtpmap:{JpegRtpStream.PayloadType} JPEG/{JpegRtpStream.ClockRate}\r\n")
            .Append($"a=framerate:{camera.Video.Fps}\r\n")
            .Append($"a=control:{streamUrl}/{TrackName}\r\n");
        return sdp.ToString();
    }
}
