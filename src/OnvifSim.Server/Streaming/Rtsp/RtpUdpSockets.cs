using System.Net;
using System.Net.Sockets;

namespace OnvifSim.Streaming.Rtsp;

// The server's RTP/RTCP UDP port pair, shared by every UDP session: a single fixed pair is easy to open
// in a firewall or map out of a container, and sessions differ by their destination anyway.
// Datagrams arriving on either port (RTCP receiver reports, NAT hole punching) tell sessions that their
// client is still alive.
public sealed class RtpUdpSockets : IDisposable
{
    private const int BindAttempts = 50;

    private readonly Socket _rtp;
    private readonly Socket _rtcp;

    private RtpUdpSockets(Socket rtp, Socket rtcp)
    {
        _rtp = rtp;
        _rtcp = rtcp;
    }

    // RTP goes out from an even port and RTCP from the next one (RFC 3550 section 11).
    public int RtpPort => ((IPEndPoint)_rtp.LocalEndPoint!).Port;
    public int RtcpPort => RtpPort + 1;

    // Binds rtpPort and rtpPort + 1, or any free even/odd pair when rtpPort is 0.
    // Throws SocketException when a fixed pair is taken.
    public static RtpUdpSockets Bind(int rtpPort)
    {
        if (rtpPort != 0) return new RtpUdpSockets(BindSocket(rtpPort), BindSocket(rtpPort + 1));

        for (int attempt = 0; attempt < BindAttempts; attempt++)
        {
            var rtp = BindSocket(0);
            int port = ((IPEndPoint)rtp.LocalEndPoint!).Port;
            if (port % 2 == 0 && port < IPEndPoint.MaxPort && TryBindSocket(port + 1) is { } rtcp)
                return new RtpUdpSockets(rtp, rtcp);
            rtp.Dispose();
        }
        throw new SocketException((int)SocketError.AddressAlreadyInUse);
    }

    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint destination, bool isRtcp, CancellationToken cancellationToken) =>
        (isRtcp ? _rtcp : _rtp).SendToAsync(packet, SocketFlags.None, destination, cancellationToken);

    // Runs until cancelled, reporting the sender of every datagram received on either port.
    public Task ReceiveAsync(Action<IPEndPoint> datagramFrom, CancellationToken cancellationToken) =>
        Task.WhenAll(Drain(_rtp, datagramFrom, cancellationToken), Drain(_rtcp, datagramFrom, cancellationToken));

    public void Dispose()
    {
        _rtp.Dispose();
        _rtcp.Dispose();
    }

    private static async Task Drain(Socket socket, Action<IPEndPoint> datagramFrom, CancellationToken cancellationToken)
    {
        var buffer = new byte[2048];
        EndPoint any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cancellationToken);
                datagramFrom(Normalize((IPEndPoint)result.RemoteEndPoint));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                // An ICMP error caused by an earlier send to a client that went away; keep listening.
            }
        }
    }

    // Dual-stack sockets report IPv4 peers as ::ffff:a.b.c.d; compare and log them as plain IPv4.
    public static IPEndPoint Normalize(IPEndPoint endPoint) =>
        endPoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(endPoint.Address.MapToIPv4(), endPoint.Port) : endPoint;

    private static Socket? TryBindSocket(int port)
    {
        try
        {
            return BindSocket(port);
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static Socket BindSocket(int port)
    {
        bool dualStack = Socket.OSSupportsIPv6;
        var socket = dualStack
            ? new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true }
            : new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(dualStack ? IPAddress.IPv6Any : IPAddress.Any, port));
            if (OperatingSystem.IsWindows())
            {
                // Windows reports an ICMP "port unreachable" from an earlier send as a reset on the next
                // receive; SIO_UDP_CONNRESET off keeps one vanished client from disturbing the shared socket.
                const int SioUdpConnReset = -1744830452;
                socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
