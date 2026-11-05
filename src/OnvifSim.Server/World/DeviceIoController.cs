namespace OnvifSim.World;

// The one place a relay output's state changes, whoever asks: the editor's switch or ONVIF's
// SetRelayOutputState. What is switched is the logical state — the coil energised or not — and the
// contact follows it through the relay's idle state. A monostable relay de-energises DelaySeconds after
// being switched, the way a door strike or a gate closes again on its own, so its contact returns to
// the idle state on its own as well.
public sealed class DeviceIoController : IDisposable
{
    private readonly WorldStore _store;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    // One pending fall-back per relay, keyed case-insensitively as the world addresses devices. The
    // generation tells a timer that has been replaced from the one in force.
    private readonly Dictionary<string, (ITimer Timer, long Generation)> _timers = [];
    private long _generation;

    public DeviceIoController(WorldStore store, TimeProvider time)
    {
        _store = store;
        _time = time;
        // A timer belongs to the world it was armed in; its relay may mean something else after a switch.
        _store.Switched += _ => CancelTimers();
    }

    // Energises or de-energises a relay. False when the camera has no relay with that token.
    public bool SetRelay(string cameraId, string token, bool active)
    {
        RelayOutputModel? relay = null;
        _store.Update(world =>
        {
            relay = DeviceIo.FindRelay(world, cameraId, token);
            if (relay != null) relay.Active = active;
        });
        if (relay is null) return false;
        Arm(cameraId, token, relay.Mode, relay.DelaySeconds);
        return true;
    }

    public void Dispose() => CancelTimers();

    // Replaces the pending fall-back with one of the newest switch: pressing again restarts the delay.
    private void Arm(string cameraId, string token, RelayModeModel mode, float delaySeconds)
    {
        var key = Key(cameraId, token);
        lock (_gate)
        {
            if (_timers.Remove(key, out var previous)) previous.Timer.Dispose();
            if (mode != RelayModeModel.Monostable) return;
            long generation = ++_generation;
            _timers[key] = (_time.CreateTimer(_ => Release(cameraId, token, generation), null,
                TimeSpan.FromSeconds(Math.Max(0.05f, delaySeconds)), Timeout.InfiniteTimeSpan), generation);
        }
    }

    private void Release(string cameraId, string token, long generation)
    {
        // A timer that was replaced or cancelled has nothing to say.
        lock (_gate)
        {
            var key = Key(cameraId, token);
            if (!_timers.TryGetValue(key, out var pending) || pending.Generation != generation) return;
            _timers.Remove(key);
        }
        // De-energised, and the contact is back in the relay's idle state because it is derived from it.
        _store.Update(world =>
        {
            if (DeviceIo.FindRelay(world, cameraId, token) is { Mode: RelayModeModel.Monostable } relay)
                relay.Active = false;
        });
    }

    private void CancelTimers()
    {
        lock (_gate)
        {
            foreach (var pending in _timers.Values) pending.Timer.Dispose();
            _timers.Clear();
        }
    }

    private static string Key(string cameraId, string token) =>
        cameraId.ToLowerInvariant() + "\n" + token.ToLowerInvariant();
}
