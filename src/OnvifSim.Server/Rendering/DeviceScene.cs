using System.Numerics;
using OnvifSim.Geometry;
using OnvifSim.World;

namespace OnvifSim.Rendering;

// The lamps and sensor blocks that stand for a camera's relay outputs and digital inputs, and the wires
// between them. Devices of a camera standing on the ground belong to the static scene; those of a camera
// on a car move with it, and so do the wires that touch them, so they are built for every frame as the
// car itself is.
public static class DeviceScene
{
    private const float WireAlpha = 0.8f;
    private static readonly Vector3 WireColor = ColorUtil.Rgb(42, 42, 46);

    // Devices that stand still, for RenderScene.Build.
    public static List<SceneChunk> Static(WorldModel world)
    {
        var terrain = new Terrain(world.Terrain);
        var chunks = new List<SceneChunk>();
        foreach (var camera in world.Cameras)
            if (!DeviceIo.IsMounted(camera))
                chunks.AddRange(Devices(world, terrain, camera, DateTimeOffset.MinValue));
        // A lamp belongs to the world, not to a camera, so it never moves with one.
        foreach (var lamp in world.Lamps)
            chunks.Add(Placed($"lamp/{lamp.Id}", DeviceMesh.Lamp(DeviceIo.LampLit(world, lamp.Id), lamp.Color),
                new Vector3(lamp.Position.X, terrain.HeightAt(lamp.Position.X, lamp.Position.Z) + lamp.Position.Y, lamp.Position.Z)));
        return chunks;
    }

    // Wires whose both ends stand still. A wire touching a device on a car moves and is drawn per frame.
    public static List<RenderScene.Chunk> StaticWires(WorldModel world) =>
        world.Wires
            .Where(wire => !Moves(world, wire))
            .Select(wire => WireChunk(world, wire, DateTimeOffset.MinValue))
            .OfType<RenderScene.Chunk>()
            .ToList();

    // Devices that move with a car, and the wires touching them, as far as the camera can see them.
    public static List<RenderScene.Chunk> Moving(WorldModel world, RenderScene scene, ViewFrustum frustum, DateTimeOffset at)
    {
        var visible = new List<RenderScene.Chunk>();
        foreach (var camera in world.Cameras)
        {
            if (!DeviceIo.IsMounted(camera)) continue;
            foreach (var chunk in Devices(world, new Terrain(world.Terrain), camera, at))
                if (frustum.Sees(chunk.Center, chunk.Radius))
                    visible.Add(scene.Light(chunk));
        }
        foreach (var wire in world.Wires)
        {
            if (!Moves(world, wire)) continue;
            if (WireChunk(world, wire, at) is { } chunk && frustum.Sees(chunk.Center, chunk.Radius))
                visible.Add(chunk);
        }
        return visible;
    }

    // Where a device stands: offset.X to the camera's right and offset.Z behind it in the mount's level
    // frame (a camera on a car adds the car's heading), and offset.Y above the ground there. The camera's
    // pitch and roll do not tilt its devices: a lamp hangs from its post, it does not lean with the lens.
    public static Vector3 DevicePoint(CameraModel camera, WorldModel world, Terrain terrain, DateTimeOffset at, Vec3 offset)
    {
        var pose = CameraPose.From(camera, world, at, default);
        var level = Orientation.FromAngles(camera.Heading + BaseHeading(camera, world, at), 0, 0);
        var horizontal = level.ToWorld(new Vector3(offset.X, 0, offset.Z));
        float x = pose.Position.X + horizontal.X, z = pose.Position.Z + horizontal.Z;
        return new Vector3(x, terrain.HeightAt(x, z) + offset.Y, z);
    }

    private static IEnumerable<SceneChunk> Devices(WorldModel world, Terrain terrain, CameraModel camera, DateTimeOffset at)
    {
        foreach (var relay in camera.RelayOutputs)
            yield return Placed($"{camera.Id}/relay/{relay.Token}", DeviceMesh.Lamp(relay.Active),
                DevicePoint(camera, world, terrain, at, relay.Position));
        foreach (var input in camera.DigitalInputs)
            yield return Placed($"{camera.Id}/input/{input.Token}",
                DeviceMesh.Sensor(DeviceIo.InputActive(world, camera.Id, input.Token) == true),
                DevicePoint(camera, world, terrain, at, input.Position));
    }

    private static SceneChunk Placed(string name, Mesh mesh, Vector3 at)
    {
        var placed = new Mesh();
        placed.Append(mesh, Matrix4x4.CreateTranslation(at));
        var (center, radius) = placed.BoundingSphere();
        return new SceneChunk { Name = name, Mesh = placed, Center = center, Radius = radius };
    }

    // From the relay's shade down to the top of the sensor block or the shade of the lamp it drives, a
    // straight thin line: what a cable run looks like from a camera, without pretending to know where
    // the cable goes.
    private static RenderScene.Chunk? WireChunk(WorldModel world, WireModel wire, DateTimeOffset at)
    {
        var relayCamera = DeviceIo.FindCamera(world, wire.RelayCameraId);
        var relay = DeviceIo.FindRelay(world, wire.RelayCameraId, wire.RelayToken);
        if (relayCamera is null || relay is null) return null;
        var terrain = new Terrain(world.Terrain);
        if (Sink(world, wire, terrain, at) is not { } sink) return null;
        var from = DevicePoint(relayCamera, world, terrain, at, relay.Position) + new Vector3(0, DeviceMesh.LampHeight - 0.2f, 0);
        var to = sink.Position + new Vector3(0, sink.Height, 0);
        var mesh = new Mesh();
        mesh.AddVertex(from, Vector3.UnitY, WireColor);
        mesh.AddVertex(to, Vector3.UnitY, WireColor);
        return RenderScene.Unlit(mesh, WireAlpha, [0, 1]);
    }

    // Where a wire ends and at what height above the ground: an input's block on its camera, or a
    // lamp's shade standing in the world. Null when the sink is gone.
    private static (Vector3 Position, float Height)? Sink(WorldModel world, WireModel wire, Terrain terrain, DateTimeOffset at)
    {
        var inputCamera = DeviceIo.FindCamera(world, wire.InputCameraId);
        var input = DeviceIo.FindInput(world, wire.InputCameraId, wire.InputToken ?? "");
        if (inputCamera is not null && input is not null)
            return (DevicePoint(inputCamera, world, terrain, at, input.Position), DeviceMesh.SensorHeight);
        var lamp = DeviceIo.FindLamp(world, wire.LampId);
        if (lamp is not null)
            return (new Vector3(lamp.Position.X,
                terrain.HeightAt(lamp.Position.X, lamp.Position.Z) + lamp.Position.Y, lamp.Position.Z),
                DeviceMesh.LampHeight - 0.2f);
        return null;
    }

    // Whether a wire's ends move: its relay or its input is on a car. A lamp stands in the world and
    // never moves with one.
    private static bool Moves(WorldModel world, WireModel wire) =>
        DeviceIo.FindCamera(world, wire.RelayCameraId) is { } relayCamera && DeviceIo.IsMounted(relayCamera) ||
        DeviceIo.FindCamera(world, wire.InputCameraId) is { } sinkCamera && DeviceIo.IsMounted(sinkCamera);

    private static float BaseHeading(CameraModel camera, WorldModel world, DateTimeOffset at)
    {
        if (!DeviceIo.IsMounted(camera)) return 0;
        var vehicle = world.Vehicles.FirstOrDefault(v => string.Equals(v.Id, camera.MountedOnVehicleId, StringComparison.OrdinalIgnoreCase));
        return vehicle == null ? 0 : VehicleMotion.PoseAt(vehicle, at).Heading;
    }
}
