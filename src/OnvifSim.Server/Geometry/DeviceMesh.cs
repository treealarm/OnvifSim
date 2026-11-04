using System.Numerics;

namespace OnvifSim.Geometry;

// The lamp of the world: an actuator a relay drives, standing on the ground as a lamp post. A camera's
// own relay outputs and digital inputs are settings a client reads and writes over ONVIF, not shapes in
// the world, so they have no mesh.
public static class DeviceMesh
{
    // Height of the top of the lamp's bulb above the ground.
    public const float LampHeight = 2.6f;

    private static readonly Vector3 Post = ColorUtil.Rgb(66, 68, 72);
    private static readonly Vector3 LampLit = ColorUtil.Rgb(255, 216, 102);
    private static readonly Vector3 LampDark = ColorUtil.Rgb(92, 94, 98);

    // A classic lamp post: a pole, a collar, and a round bulb that glows in its colour while lit.
    // glow: the bulb's colour, "#rrggbb"; null keeps the simulator's warm lamp yellow.
    public static Mesh Lamp(bool active, string? glow = null)
    {
        var lit = ColorUtil.Parse(glow, LampLit);
        var mesh = new Mesh();
        Primitives.Frustum(mesh, 0.07f, 0.045f, 2.1f, 8, Post);
        Primitives.Frustum(mesh, 0.1f, 0.1f, 0.12f, 8, Post, 2.1f);
        Primitives.Blob(mesh, new Vector3(0.28f), new Vector3(0, 2.32f, 0), 0, 0, active ? lit : LampDark, Vector3.Zero);
        return mesh;
    }
}
