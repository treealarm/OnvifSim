using System.Net;
using OnvifSim.Streaming.Rtp;

namespace OnvifSim.Streaming.Rtsp;

// Where a session's RTP and RTCP packets go, as negotiated by SETUP.
public interface IRtpTransport
{
    // The Transport header of the SETUP response.
    string ResponseHeader { get; }

    ValueTask SendAsync(RtpPacketBatch batch, bool isRtcp, CancellationToken cancellationToken);
}

// RTP/AVP/TCP: packets interleaved into the RTSP control connection (RFC 2326 section 10.12).
public sealed class InterleavedTransport(RtspConnection connection, byte rtpChannel, byte rtcpChannel) : IRtpTransport
{
    public RtspConnection Connection => connection;

    public string ResponseHeader => $"RTP/AVP/TCP;unicast;interleaved={rtpChannel}-{rtcpChannel}";

    public ValueTask SendAsync(RtpPacketBatch batch, bool isRtcp, CancellationToken cancellationToken) =>
        connection.WriteAsync(batch.FrameForInterleaved(isRtcp ? rtcpChannel : rtpChannel), cancellationToken);
}

// RTP/AVP over UDP from the server's shared port pair to the client's ports. The destination is always
// the address the RTSP connection came from: honouring a "destination=" parameter would let anyone
// point the stream at a third party.
public sealed class UdpTransport(RtpUdpSockets sockets, IPAddress client, int clientRtpPort, int clientRtcpPort) : IRtpTransport
{
    private readonly IPEndPoint _rtpDestination = new(client, clientRtpPort);
    private readonly IPEndPoint _rtcpDestination = new(client, clientRtcpPort);

    public string ResponseHeader =>
        $"RTP/AVP;unicast;client_port={clientRtpPort}-{clientRtcpPort};server_port={sockets.RtpPort}-{sockets.RtcpPort}";

    public bool IsFromClient(IPEndPoint sender) =>
        sender.Address.Equals(client) && (sender.Port == clientRtpPort || sender.Port == clientRtcpPort);

    public async ValueTask SendAsync(RtpPacketBatch batch, bool isRtcp, CancellationToken cancellationToken)
    {
        var destination = isRtcp ? _rtcpDestination : _rtpDestination;
        for (int i = 0; i < batch.Count; i++)
            await sockets.SendAsync(batch.Packet(i), destination, isRtcp, cancellationToken);
    }
}
