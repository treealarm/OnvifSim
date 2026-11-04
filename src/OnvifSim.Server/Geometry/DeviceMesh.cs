using System.Numerics;

namespace OnvifSim.Geometry;

// The shapes that stand for a camera's relay outputs and digital inputs, in local coordinates with the
// base on the ground: a lamp post with a shade for a relay, a small block for an input. The shade or the
// top of the block shows the state, as a lamp in the world would.
public static class DeviceMesh
{
    // Height of the lamp's shade, and of the top of a sensor block, above the ground: where a wire meets
    // each of them.
    public const float LampHeight = 2.6f;
    public const float SensorHeight = 0.42f;

    private static readonly Vector3 Post = ColorUtil.Rgb(66, 68, 72);
    private static readonly Vector3 LampLit = ColorUtil.Rgb(255, 216, 102);
    private static readonly Vector3 LampDark = ColorUtil.Rgb(92, 94, 98);
    private static readonly Vector3 SensorLit = ColorUtil.Rgb(82, 208, 108);
    private static readonly Vector3 SensorDark = ColorUtil.Rgb(84, 94, 104);
    private static readonly Vector3 SensorBody = ColorUtil.Rgb(52, 54, 58);

    public static Mesh Lamp(bool active)
    {
        var mesh = new Mesh();
        Primitives.Frustum(mesh, 0.08f, 0.05f, LampHeight - 0.3f, 6, Post);
        // Opening downwards, so the lit side is seen from below.
        Primitives.Frustum(mesh, 0.38f, 0.1f, 0.45f, 8, active ? LampLit : LampDark, LampHeight - 0.45f);
        return mesh;
    }

    public static Mesh Sensor(bool active)
    {
        var mesh = new Mesh();
        Primitives.Box(mesh, 0.26f, SensorHeight - 0.12f, 0.26f, SensorBody);
        Primitives.Box(mesh, 0.34f, 0.12f, 0.34f, active ? SensorLit : SensorDark, SensorHeight - 0.12f);
        return mesh;
    }
}
