namespace OnvifSim.Recording;

// Which cameras have lost their network, for testing what a client does about it. Such a camera's
// ONVIF port drops every request, its streams stop and cannot be opened again, and discovery does not
// answer for it, until the network is back. It goes on recording (RecordingLog), which is what lets a
// client recover the footage afterwards. Not saved: a restart brings every camera back.
public sealed class CameraNetwork
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _offline = new(StringComparer.OrdinalIgnoreCase);

    // Raised with the camera's id when it goes offline, for whoever has to cut what is already running.
    public event Action<string>? WentOffline;

    public bool IsOnline(string cameraId)
    {
        lock (_gate) return !_offline.Contains(cameraId);
    }

    public void SetOnline(string cameraId, bool online)
    {
        lock (_gate)
        {
            if (online)
            {
                _offline.Remove(cameraId);
                return;
            }
            if (!_offline.Add(cameraId)) return;
        }
        WentOffline?.Invoke(cameraId);
    }
}
