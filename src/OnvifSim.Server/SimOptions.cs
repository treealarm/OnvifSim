namespace OnvifSim;

// Command line: --data <dir>  --port <web port>  --rtsp-port <port>  --rtp-port <even port>
//               --onvif-base-port <port>  --advertise-host <host>  --discovery-port <port>  --bench
// (also as --name=value, which is how WebApplicationFactory passes UseSetting values to the app).
// Environment fallbacks: ONVIFSIM_DATA, ONVIFSIM_PORT, ONVIFSIM_RTSP_PORT, ONVIFSIM_RTP_PORT,
// ONVIFSIM_ONVIF_BASE_PORT, ONVIFSIM_ADVERTISE_HOST, ONVIFSIM_DISCOVERY_PORT (the Docker image sets them). Port 0 lets the OS pick a free port, which the tests use.
public sealed class SimOptions
{
    public string DataDirectory { get; init; } = "";
    public int WebPort { get; init; } = 8080;
    public int RtspPort { get; init; } = 8554;
    // RTP over UDP leaves from this port and RTCP from the next one, for every session.
    public int RtpPort { get; init; } = 6970;
    // Cameras without an explicit ONVIF port get this one, the next one and so on, in camera order.
    public int OnvifBasePort { get; init; } = 8001;
    // The host WS-Discovery puts into XAddrs; by default the local address of the route to the client.
    public string? AdvertiseHost { get; init; }
    // WS-Discovery's UDP port; 0 turns discovery off. Only tests move it: clients look on 3702.
    public int DiscoveryPort { get; init; } = 3702;
    public bool Bench { get; init; }

    public static SimOptions Parse(string[] args)
    {
        string? data = Environment.GetEnvironmentVariable("ONVIFSIM_DATA");
        int port = PortFromEnvironment("ONVIFSIM_PORT", 8080);
        int rtspPort = PortFromEnvironment("ONVIFSIM_RTSP_PORT", 8554);
        int rtpPort = PortFromEnvironment("ONVIFSIM_RTP_PORT", 6970);
        int onvifBasePort = PortFromEnvironment("ONVIFSIM_ONVIF_BASE_PORT", 8001);
        string? advertiseHost = Environment.GetEnvironmentVariable("ONVIFSIM_ADVERTISE_HOST");
        int discoveryPort = PortFromEnvironment("ONVIFSIM_DISCOVERY_PORT", 3702);
        bool bench = false;
        for (int i = 0; i < args.Length; i++)
        {
            var (name, value) = args[i].Split('=', 2) is [var n, var v] ? (n, v) : (args[i], null);
            if (name == "--bench")
            {
                bench = true;
                continue;
            }
            if (value == null)
            {
                if (i + 1 >= args.Length) continue;
                value = args[++i];
            }
            switch (name)
            {
                case "--data": data = value; break;
                case "--port": port = int.Parse(value); break;
                case "--rtsp-port": rtspPort = int.Parse(value); break;
                case "--rtp-port": rtpPort = int.Parse(value); break;
                case "--onvif-base-port": onvifBasePort = int.Parse(value); break;
                case "--advertise-host": advertiseHost = value; break;
                case "--discovery-port": discoveryPort = int.Parse(value); break;
            }
        }
        data ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnvifSim");
        if (rtpPort % 2 != 0) throw new ArgumentException($"The RTP port must be even (RTCP uses the next one), got {rtpPort}.");
        return new SimOptions
        {
            DataDirectory = Path.GetFullPath(data), WebPort = port, RtspPort = rtspPort, RtpPort = rtpPort,
            OnvifBasePort = onvifBasePort, AdvertiseHost = advertiseHost,
            DiscoveryPort = discoveryPort, Bench = bench,
        };
    }

    private static int PortFromEnvironment(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var port) ? port : fallback;
}
