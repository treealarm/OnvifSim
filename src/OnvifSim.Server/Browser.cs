using System.ComponentModel;
using System.Diagnostics;

namespace OnvifSim;

// Opens the editor in the user's browser when the simulator runs on a desktop: started from the
// Start menu, a desktop shortcut or a terminal. A service, a container or an SSH session has no
// browser to show, so on Linux it waits for a graphical session (DISPLAY or WAYLAND_DISPLAY).
public static class Browser
{
    public static void Open(string url, ILogger logger)
    {
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("DISPLAY") is null
            && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null)
            return;
        var start = OperatingSystem.IsWindows() ? new ProcessStartInfo(url) { UseShellExecute = true }
            : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", [url])
            : new ProcessStartInfo("xdg-open", [url]);
        try
        {
            using var process = Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Nothing to open it with; the log line with the address is there to copy.
            logger.LogInformation("Could not open a browser ({Reason}); the editor is at {Url}", ex.Message, url);
        }
    }
}
