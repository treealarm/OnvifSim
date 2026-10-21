using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using OnvifSim.Rtsp.Rtp;

namespace OnvifSim.Rtsp;

// One RTSP control connection. Requests are answered in order; RTP of sessions set up over TCP shares
// the socket, so every write goes through one lock.
//
// URLs: rtsp://host:port/{media path} is a stream, as the IRtspMediaSource finds it, and
// {media path}/{track control} one of its tracks. A session set up on this connection over TCP ends
// with it; a UDP session outlives it until TEARDOWN or timeout, as RFC 2326 intends.
internal sealed class RtspConnection : IAsyncDisposable
{
    private const string PublicMethods = "OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER, SET_PARAMETER";
    // Option tags a client may Require (RFC 2326 12.32): ONVIF replay, whose Range: clock= and
    // Rate-Control the media source honours for its recordings.
    private static readonly string[] SupportedOptions = ["onvif-replay"];
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
                await WriteAsync(response.Serialize(request.CSeq, _server.ServerName), linked.Token);
                // RTP must not reach the client before the PLAY response does.
                if (request.Method == "PLAY" && response.Status == 200 && session != null)
                    StartPlaying(session);
            }
        }
        catch (InvalidDataException ex)
        {
            _logger.LogInformation("RTSP {Client}: closing after a malformed request: {Reason}", RemoteEndPoint, ex.Message);
            await TryWriteAsync(new RtspResponse(400).Serialize("0", _server.ServerName));
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
        {
            var unsupported = required.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(option => !SupportedOptions.Contains(option, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unsupported.Count > 0) return (new RtspResponse(551).With("Unsupported", string.Join(", ", unsupported)), null);
        }

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
            "PLAY" => Play(request, session),
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
        if (!TryResolve(request.Url, out var media, out var path, out _, out var streamUrl)) return new RtspResponse(404);
        if (!IsAuthorized(request, path)) return Unauthorized();
        return new RtspResponse(200)
            .With("Content-Base", streamUrl + "/")
            .WithBody("application/sdp", BuildSdp(media, streamUrl));
    }

    // A new session with its first track, or another track of the same stream added to a session.
    // Every track of a session goes over the same lower transport, and tracks are added before PLAY.
    private (RtspResponse, RtspSession?) Setup(RtspRequest request, RtspSession? existing)
    {
        if (!TryResolve(request.Url, out _, out var path, out var definition, out _)) return (new RtspResponse(404), existing);
        if (existing != null)
        {
            if (!existing.Path.SequenceEqual(path)) return (new RtspResponse(459), existing);
            // A repeated SETUP of a track confirms what is set up.
            if (existing.TrackOf(definition.Control) is { } same) return (new RtspResponse(200).With("Transport", same.TransportHeader), existing);
            if (existing.IsPlaying) return (new RtspResponse(455), existing);
        }
        if (!IsAuthorized(request, path)) return (Unauthorized(), existing);

        var transport = NegotiateTransport(request.Header("Transport"), existing?.Tracks.Count ?? 0);
        if (transport == null || (existing != null && ((transport is UdpTransport) != existing.IsUdp || existing.SharesChannel(transport))))
            return (new RtspResponse(461), existing);

        var session = existing ?? new RtspSession(path, _logger);
        var track = session.AddTrack(definition, request.Url, transport);
        if (existing == null) _server.AddSession(session);
        _logger.LogInformation("RTSP {Client}: session {Session} for {Path}: {Track} over {Transport}",
            RemoteEndPoint, session.Id, session.PathText, definition.Control, transport is InterleavedTransport ? "TCP" : "UDP");
        return (new RtspResponse(200).With("Transport", track.TransportHeader), session);
    }

    private RtspResponse Play(RtspRequest request, RtspSession? session)
    {
        if (session == null) return new RtspResponse(454);
        if (_server.Media.Find(session.Path) == null) return new RtspResponse(404);
        if (RtspPlayRequest.Parse(request) is not { } play) return new RtspResponse(457);
        // A PLAY of a session already playing changes nothing (RFC 2326 10.5 would queue it).
        if (!session.IsPlaying) session.PlayRequest = play;

        long now = Stopwatch.GetTimestamp();
        var rtpInfo = session.Tracks.Select(track =>
        {
            var stream = track.Stream;
            uint rtpTime = stream.HasSentFrames ? stream.RtpTimeAt(now) : stream.InitialTimestamp;
            return $"url={track.Url};seq={stream.NextSequenceNumber};rtptime={rtpTime}";
        });
        return new RtspResponse(200)
            .With("Range", session.PlayRequest.RangeHeader)
            .With("RTP-Info", string.Join(",", rtpInfo));
    }

    private void StartPlaying(RtspSession session)
    {
        session.Play(failed =>
        {
            _ = _server.CloseSessionAsync(failed);
            // Over TCP the stream and the control connection are one: a client whose stream ended
            // learns it from the connection closing.
            if (!failed.IsUdp) Close();
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
    // A client that leaves the interleaved channels to the server gets the next free pair.
    private IRtpTransport? NegotiateTransport(string? header, int tracksSetUp)
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
                        : (2 * tracksSetUp, 2 * tracksSetUp + 1);
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

    private bool IsAuthorized(RtspRequest request, IReadOnlyList<string> path)
    {
        if (_server.Media.IsAuthorized(request, path)) return true;
        if (request.Header("Authorization") != null)
            _logger.LogInformation("RTSP {Client}: wrong credentials for {Path}", RemoteEndPoint, string.Join('/', path));
        return false;
    }

    private RtspResponse Unauthorized()
    {
        var response = new RtspResponse(401);
        foreach (var challenge in _server.Media.Challenges()) response.With("WWW-Authenticate", challenge);
        return response;
    }

    // Maps a request URL to its media and track: the URL's own media and its first track, or else the
    // media one segment up and the track the last segment names. streamUrl is the URL of the stream as
    // the client addresses this server (scheme, host and port from the request, no credentials or
    // query), so the control URLs in the SDP work through NAT and port mapping.
    private bool TryResolve(string url, out RtspMedia media, out IReadOnlyList<string> path, out RtspTrack track, out string streamUrl)
    {
        media = null!;
        path = [];
        track = null!;
        streamUrl = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var raw = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var segments = raw.Select(Uri.UnescapeDataString).ToArray();
        int length;
        if (_server.Media.Find(segments) is { Tracks: [var first, ..] } whole)
            (media, track, length) = (whole, first, segments.Length);
        else if (segments.Length > 1 && _server.Media.Find(segments[..^1]) is { } parent
                 && parent.Tracks.FirstOrDefault(t => t.Control == segments[^1]) is { } named)
            (media, track, length) = (parent, named, segments.Length - 1);
        else
            return false;
        path = segments[..length];
        streamUrl = $"{uri.Scheme}://{uri.Authority}/{string.Join('/', raw[..length])}";
        return true;
    }

    private string BuildSdp(RtspMedia media, string streamUrl)
    {
        var address = LocalEndPoint.Address;
        string addressType = address.AddressFamily == AddressFamily.InterNetworkV6 ? "IP6" : "IP4";
        var sdp = new StringBuilder()
            .Append("v=0\r\n")
            .Append($"o=- {Random.Shared.NextInt64(1, long.MaxValue)} 1 IN {addressType} {address}\r\n")
            .Append($"s={media.Name}\r\n")
            .Append($"c=IN {addressType} {(addressType == "IP6" ? "::" : "0.0.0.0")}\r\n")
            .Append("t=0 0\r\n")
            .Append("a=control:*\r\n")
            .Append("a=range:npt=0-\r\n");
        foreach (var track in media.Tracks)
        {
            sdp.Append($"m={track.MediaType} 0 RTP/AVP {track.PayloadType}\r\n")
                .Append($"a=rtpmap:{track.PayloadType} {track.Encoding}/{RtpStream.ClockRate}\r\n");
            if (track.FormatParameters != null) sdp.Append($"a=fmtp:{track.PayloadType} {track.FormatParameters}\r\n");
            if (track.FrameRate != null) sdp.Append($"a=framerate:{track.FrameRate}\r\n");
            sdp.Append($"a=control:{streamUrl}/{track.Control}\r\n");
        }
        return sdp.ToString();
    }
}
