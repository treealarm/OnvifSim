using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using OnvifSim.Recording;
using OnvifSim.World;

namespace OnvifSim.Onvif;

// WS-Discovery (the 2005/04 draft ONVIF uses) on UDP 3702. Every camera is a device of its own: a Probe
// gets one ProbeMatches message per camera (clients such as OnvifLib read only the first match of a
// message), and cameras announce themselves with Hello and Bye as they come and go.
//
// XAddrs carry the address the client can reach the simulator on: the configured advertise host, or
// the local address of the route back to the sender.
public sealed class WsDiscoveryResponder(SimOptions options, SceneState scene, OnvifHost host, CameraNetwork network, ILogger<WsDiscoveryResponder> logger)
    : BackgroundService
{
    private const int MaxMessageBytes = 64 * 1024;
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("239.255.255.250");

    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Wsa = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    private static readonly XNamespace Disc = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
    private static readonly XNamespace Dn = "http://www.onvif.org/ver10/network/wsdl";
    private static readonly XNamespace Tds = "http://www.onvif.org/ver10/device/wsdl";
    private const string DiscoveryUrn = "urn:schemas-xmlsoap-org:ws:2005:04:discovery";
    private const string AnonymousRole = "http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous";

    private static readonly XmlReaderSettings ReaderSettings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    // AppSequence: a new instance id per run, and a counter over every message sent.
    private int Port => options.DiscoveryPort;

    private readonly long _instanceId = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private long _messageNumber;
    private Socket? _socket;
    // Cameras announced with Hello, and the port each was announced with.
    private readonly Dictionary<string, int> _announced = new(StringComparer.OrdinalIgnoreCase);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (Port == 0)
        {
            logger.LogInformation("WS-Discovery is off");
            return base.StartAsync(cancellationToken);
        }
        try
        {
            _socket = Bind(Port);
            logger.LogInformation("WS-Discovery on UDP {Port}, joined {Group} on {Count} interface(s)", Port, MulticastGroup, JoinGroup(_socket));
        }
        catch (SocketException ex)
        {
            logger.LogWarning("WS-Discovery is off: cannot bind UDP {Port}: {Reason}", Port, ex.Message);
        }
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        host.ListenersChanged -= Announce;
        await base.StopAsync(cancellationToken);
        if (_socket == null) return;
        lock (_announced)
        {
            foreach (var cameraId in _announced.Keys) SendToGroup(Bye(cameraId));
            _announced.Clear();
        }
        _socket.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_socket == null) return;
        host.ListenersChanged += Announce;
        Announce();

        var buffer = new byte[MaxMessageBytes];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!stoppingToken.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex)
            {
                logger.LogDebug("WS-Discovery receive failed: {Reason}", ex.Message);
                continue;
            }
            var sender = (IPEndPoint)received.RemoteEndPoint;
            try
            {
                AnswerProbe(buffer.AsSpan(0, received.ReceivedBytes), sender);
            }
            catch (Exception ex) when (ex is XmlException or SocketException)
            {
                logger.LogDebug("WS-Discovery: ignored a message from {Sender}: {Reason}", sender, ex.Message);
            }
        }
    }

    private void AnswerProbe(ReadOnlySpan<byte> message, IPEndPoint sender)
    {
        XDocument document;
        using (var reader = XmlReader.Create(new MemoryStream(message.ToArray()), ReaderSettings))
            document = XDocument.Load(reader);
        var probe = document.Root?.Element(Soap + "Body")?.Element(Disc + "Probe");
        if (probe == null) return;
        var messageId = document.Root!.Element(Soap + "Header")?.Element(Wsa + "MessageID")?.Value.Trim();
        if (!AsksForCameras(probe)) return;

        var address = AdvertisedAddress(sender.Address);
        var requestedScopes = SplitList(probe.Element(Disc + "Scopes")?.Value);
        int answered = 0;
        foreach (var camera in scene.Current.World.Cameras)
        {
            if (host.PortOf(camera.Id) is not { } port || !network.IsOnline(camera.Id)) continue;
            var scopes = CameraIdentity.Scopes(camera);
            if (!requestedScopes.All(r => scopes.Any(s => s.StartsWith(r, StringComparison.OrdinalIgnoreCase)))) continue;
            var match = Envelope(
                Header("http://schemas.xmlsoap.org/ws/2005/04/discovery/ProbeMatches", AnonymousRole, messageId),
                new XElement(Disc + "ProbeMatches", new XElement(Disc + "ProbeMatch", Description(camera, address, port))));
            _socket!.SendTo(match, sender);
            answered++;
        }
        logger.LogDebug("WS-Discovery: Probe from {Sender}, {Count} camera(s) answered", sender, answered);
    }

    // An empty Types matches everything; otherwise a camera matches NetworkVideoTransmitter or Device.
    // Prefixes vary between clients, so types are compared by local name.
    private static bool AsksForCameras(XElement probe)
    {
        var types = SplitList(probe.Element(Disc + "Types")?.Value);
        return types.Length == 0 || types.Any(t => t.Split(':').Last() is "NetworkVideoTransmitter" or "Device");
    }

    // Hello for cameras that started serving (or moved), Bye for those that stopped, on every interface.
    private void Announce()
    {
        if (_socket == null) return;
        var serving = scene.Current.World.Cameras
            .Select(c => (Camera: c, Port: host.PortOf(c.Id)))
            .Where(c => c.Port != null)
            .ToDictionary(c => c.Camera.Id, c => (c.Camera, Port: c.Port!.Value), StringComparer.OrdinalIgnoreCase);
        lock (_announced)
        {
            foreach (var gone in _announced.Keys.Where(id => !serving.ContainsKey(id)).ToList())
            {
                SendToGroup(Bye(gone));
                _announced.Remove(gone);
            }
            foreach (var (id, (camera, port)) in serving)
            {
                if (_announced.TryGetValue(id, out int announcedPort) && announcedPort == port) continue;
                foreach (var address in MulticastInterfaces())
                    SendToGroup(Envelope(
                        Header("http://schemas.xmlsoap.org/ws/2005/04/discovery/Hello", DiscoveryUrn, null),
                        new XElement(Disc + "Hello", Description(camera, AdvertisedAddress(address), port))), address);
                _announced[id] = port;
            }
        }
    }

    private byte[] Bye(string cameraId) => Envelope(
        Header("http://schemas.xmlsoap.org/ws/2005/04/discovery/Bye", DiscoveryUrn, null),
        new XElement(Disc + "Bye", EndpointReference(cameraId)));

    private static object[] Description(CameraModel camera, string address, int port) =>
    [
        EndpointReference(camera.Id),
        new XElement(Disc + "Types", "dn:NetworkVideoTransmitter tds:Device"),
        new XElement(Disc + "Scopes", string.Join(" ", CameraIdentity.Scopes(camera))),
        new XElement(Disc + "XAddrs", $"http://{address}:{port}/onvif/device_service"),
        new XElement(Disc + "MetadataVersion", 1),
    ];

    private static XElement EndpointReference(string cameraId) =>
        new(Wsa + "EndpointReference", new XElement(Wsa + "Address", CameraIdentity.EndpointReference(cameraId)));

    private XElement Header(string action, string to, string? relatesTo) =>
        new(Soap + "Header",
            new XElement(Wsa + "MessageID", "urn:uuid:" + Guid.NewGuid()),
            relatesTo == null ? null : new XElement(Wsa + "RelatesTo", relatesTo),
            new XElement(Wsa + "To", to),
            new XElement(Wsa + "Action", action),
            new XElement(Disc + "AppSequence",
                new XAttribute("InstanceId", _instanceId),
                new XAttribute("MessageNumber", Interlocked.Increment(ref _messageNumber))));

    private static byte[] Envelope(XElement header, XElement body)
    {
        var envelope = new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", Soap.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "a", Wsa.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "d", Disc.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "dn", Dn.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "tds", Tds.NamespaceName),
            header,
            new XElement(Soap + "Body", body));
        return Encoding.UTF8.GetBytes(envelope.ToString(SaveOptions.DisableFormatting));
    }

    // The configured host, or the local address of the route towards the client.
    private string AdvertisedAddress(IPAddress towards)
    {
        if (!string.IsNullOrEmpty(options.AdvertiseHost)) return options.AdvertiseHost;
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Connect(towards, Port);
        return ((IPEndPoint)probe.LocalEndPoint!).Address.ToString();
    }

    private void SendToGroup(byte[] message, IPAddress? throughInterface = null)
    {
        try
        {
            if (throughInterface != null)
                _socket!.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, throughInterface.GetAddressBytes());
            _socket!.SendTo(message, new IPEndPoint(MulticastGroup, Port));
        }
        catch (SocketException ex)
        {
            logger.LogDebug("WS-Discovery: multicast send via {Interface} failed: {Reason}", throughInterface, ex.Message);
        }
    }

    // Shares the port with any other WS-Discovery listener on the host (Windows has one).
    private static Socket Bind(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private int JoinGroup(Socket socket)
    {
        int joined = 0;
        foreach (var address in MulticastInterfaces())
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastGroup, address));
                joined++;
            }
            catch (SocketException ex)
            {
                logger.LogDebug("WS-Discovery: cannot join {Group} on {Address}: {Reason}", MulticastGroup, address, ex.Message);
            }
        }
        return joined;
    }

    // The IPv4 address of every interface that is up and can multicast, loopback included so that
    // clients on the same machine find the simulator too.
    private static IEnumerable<IPAddress> MulticastInterfaces() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && (n.SupportsMulticast || n.NetworkInterfaceType == NetworkInterfaceType.Loopback))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Distinct();

    private static string[] SplitList(string? value) =>
        value?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
}
