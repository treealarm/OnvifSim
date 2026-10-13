namespace OnvifSim;

// Command line: --data <dir>  --port <web port>  --bench
// Environment fallbacks: ONVIFSIM_DATA, ONVIFSIM_PORT (the Docker image sets them).
public sealed class SimOptions
{
    public string DataDirectory { get; init; } = "";
    public int WebPort { get; init; } = 8080;
    public bool Bench { get; init; }

    public static SimOptions Parse(string[] args)
    {
        string? data = Environment.GetEnvironmentVariable("ONVIFSIM_DATA");
        int port = int.TryParse(Environment.GetEnvironmentVariable("ONVIFSIM_PORT"), out var p) ? p : 8080;
        bool bench = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data" when i + 1 < args.Length: data = args[++i]; break;
                case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
                case "--bench": bench = true; break;
            }
        }
        data ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnvifSim");
        return new SimOptions { DataDirectory = Path.GetFullPath(data), WebPort = port, Bench = bench };
    }
}
