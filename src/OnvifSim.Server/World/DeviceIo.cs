namespace OnvifSim.World;

// Reading a camera's relay outputs and digital inputs: where a device lives, which relay an input's wire
// runs from, and whether that wire drives it now. The scene, the editor API and the ONVIF handlers all
// ask here, so they cannot disagree about what a wire means.
public static class DeviceIo
{
    public static CameraModel? FindCamera(WorldModel world, string id) =>
        world.Cameras.FirstOrDefault(c => Same(c.Id, id));

    public static RelayOutputModel? FindRelay(WorldModel world, string cameraId, string token) =>
        FindCamera(world, cameraId)?.RelayOutputs.FirstOrDefault(r => Same(r.Token, token));

    public static DigitalInputModel? FindInput(WorldModel world, string cameraId, string token) =>
        FindCamera(world, cameraId)?.DigitalInputs.FirstOrDefault(i => Same(i.Token, token));

    // The wire that drives an input, if any.
    public static WireModel? WireOfInput(WorldModel world, string cameraId, string token) =>
        world.Wires.FirstOrDefault(w => Same(w.InputCameraId, cameraId) && Same(w.InputToken, token));

    // Whether the input is on: the state of the relay its wire runs from. Null when no wire drives it,
    // which is not the same as a wire from a relay that happens to be off.
    public static bool? InputActive(WorldModel world, string cameraId, string token) =>
        WireOfInput(world, cameraId, token) is { } wire
            ? FindRelay(world, wire.RelayCameraId, wire.RelayToken)?.Active
            : null;

    // True when the camera's devices move with the world rather than standing still.
    public static bool IsMounted(CameraModel camera) => !string.IsNullOrEmpty(camera.MountedOnVehicleId);

    public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
