namespace OnvifSim;

// Command line: --data <dir>  --port <web port>  --rtsp-port <port>  --rtp-port <even port>  --bench
// (also as --name=value, which is how WebApplicationFactory passes UseSetting values to the app).
// Environment fallbacks: ONVIFSIM_DATA, ONVIFSIM_PORT, ONVIFSIM_RTSP_PORT, ONVIFSIM_RTP_PORT (the Docker
// image sets them). Port 0 lets the OS pick a free port, which the tests use.
public sealed class SimOptions
{
    public string DataDirectory { get; init; } = "";
    public int WebPort { get; init; } = 8080;
    public int RtspPort { get; init; } = 8554;
    // RTP over UDP leaves from this port and RTCP from the next one, for every session.
    public int RtpPort { get; init; } = 6970;
    public bool Bench { get; init; }

    public static SimOptions Parse(string[] args)
    {
        string? data = Environment.GetEnvironmentVariable("ONVIFSIM_DATA");
        int port = PortFromEnvironment("ONVIFSIM_PORT", 8080);
        int rtspPort = PortFromEnvironment("ONVIFSIM_RTSP_PORT", 8554);
        int rtpPort = PortFromEnvironment("ONVIFSIM_RTP_PORT", 6970);
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
            }
        }
        data ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnvifSim");
        if (rtpPort % 2 != 0) throw new ArgumentException($"The RTP port must be even (RTCP uses the next one), got {rtpPort}.");
        return new SimOptions
        {
            DataDirectory = Path.GetFullPath(data), WebPort = port, RtspPort = rtspPort, RtpPort = rtpPort, Bench = bench,
        };
    }

    private static int PortFromEnvironment(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var port) ? port : fallback;
}
