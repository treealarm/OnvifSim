namespace OnvifSim.World;

// Reading a camera's relay outputs, digital inputs and lamps: where a device lives, which relays are
// wired to it, and what that wiring makes it read now. The scene, the editor API and the ONVIF handlers
// all ask here, so they cannot disagree about what a wire means.
public static class DeviceIo
{
    public static CameraModel? FindCamera(WorldModel world, string? id) =>
        id is null ? null : world.Cameras.FirstOrDefault(c => Same(c.Id, id));

    public static RelayOutputModel? FindRelay(WorldModel world, string cameraId, string token) =>
        FindCamera(world, cameraId)?.RelayOutputs.FirstOrDefault(r => Same(r.Token, token));

    public static DigitalInputModel? FindInput(WorldModel world, string? cameraId, string token) =>
        FindCamera(world, cameraId)?.DigitalInputs.FirstOrDefault(i => Same(i.Token, token));

    public static LampModel? FindLamp(WorldModel world, string? id) =>
        id is null ? null : world.Lamps.FirstOrDefault(l => Same(l.Id, id));

    // The wires into an input or a lamp. An input may be driven by several relays; so may a lamp.
    public static IEnumerable<WireModel> WiresToInput(WorldModel world, string cameraId, string token) =>
        world.Wires.Where(w => Same(w.InputCameraId, cameraId) && Same(w.InputToken, token));

    public static IEnumerable<WireModel> WiresToLamp(WorldModel world, string id) =>
        world.Wires.Where(w => Same(w.LampId, id));

    // Whether the relay a wire runs from is closed (carrying current, ONVIF active).
    public static bool RelayClosed(WorldModel world, WireModel wire) =>
        FindRelay(world, wire.RelayCameraId, wire.RelayToken)?.Active == true;

    // Whether the input is on. A closed relay contact shunts the input to zero and an open one leaves
    // it pulled up, so a wired input reads inverted: any closed relay gives false, all open gives
    // true. Null when no wire drives it, which is not the same as a wire from a relay that is off.
    public static bool? InputActive(WorldModel world, string cameraId, string token)
    {
        var wires = WiresToInput(world, cameraId, token).ToList();
        return wires.Count == 0 ? null : !wires.Any(wire => RelayClosed(world, wire));
    }

    // Whether a lamp is lit. A normally-open lamp lights while any linked relay is closed; a normally
    // closed one while none is. A lamp with no wire at all is simply off.
    public static bool LampLit(WorldModel world, string id)
    {
        var lamp = FindLamp(world, id);
        if (lamp is null) return false;
        var wires = WiresToLamp(world, id).ToList();
        if (wires.Count == 0) return false;
        bool closed = wires.Any(wire => RelayClosed(world, wire));
        return closed != lamp.NormallyClosed;
    }

    // True when the camera's devices move with the world rather than standing still.
    public static bool IsMounted(CameraModel camera) => !string.IsNullOrEmpty(camera.MountedOnVehicleId);

    public static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
