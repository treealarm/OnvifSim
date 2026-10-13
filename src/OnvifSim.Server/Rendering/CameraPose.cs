using System.Numerics;
using OnvifSim.World;

namespace OnvifSim.Rendering;

// Where a camera is and where it looks. Heading is degrees clockwise from north (-Z), pitch is degrees
// up from the horizon, roll turns the camera clockwise as seen from behind it; HFov is the horizontal
// field of view.
public readonly record struct CameraPose(Vector3 Position, float Heading, float Pitch, float Roll, float HFov)
{
    // Pose of a camera from its configuration; a PTZ camera adds its stored pan/tilt/zoom to the mount.
    public static CameraPose From(CameraModel cam, Terrain terrain) => From(cam, terrain, cam.Ptz?.Position ?? default);

    // Pose of a camera whose PTZ head is at ptz (a fixed camera ignores it).
    public static CameraPose From(CameraModel cam, Terrain terrain, PtzPosition ptz)
    {
        var pos = new Vector3(cam.Position.X, terrain.HeightAt(cam.Position.X, cam.Position.Z) + cam.Position.Y, cam.Position.Z);
        if (cam.Kind == CameraKind.Ptz && cam.Ptz is { } limits)
            return new CameraPose(pos, cam.Heading + ptz.Pan, cam.Pitch + ptz.Tilt, cam.Roll, ZoomToHFov(ptz.Zoom, limits.FovWide, limits.FovTele));
        return new CameraPose(pos, cam.Heading, cam.Pitch, cam.Roll, cam.HFov);
    }

    // Zoom 0..1 maps to focal length geometrically between the wide and tele ends, which is how a real
    // zoom lens feels: equal zoom steps give equal magnification ratios.
    public static float ZoomToHFov(float zoom, float fovWide, float fovTele)
    {
        float fw = 1 / MathF.Tan(fovWide * MathF.PI / 360), ft = 1 / MathF.Tan(fovTele * MathF.PI / 360);
        float f = fw * MathF.Pow(ft / fw, Math.Clamp(zoom, 0, 1));
        return 2 * MathF.Atan(1 / f) * 180 / MathF.PI;
    }

    // Camera basis: forward, right and up unit vectors in world space.
    public (Vector3 Forward, Vector3 Right, Vector3 Up) Basis()
    {
        float h = Heading * MathF.PI / 180, p = Pitch * MathF.PI / 180, r = Roll * MathF.PI / 180;
        var fwd = new Vector3(MathF.Sin(h) * MathF.Cos(p), MathF.Sin(p), -MathF.Cos(h) * MathF.Cos(p));
        // Built from the heading rather than cross(fwd, worldUp) so looking straight down still works.
        var right = new Vector3(MathF.Cos(h), 0, MathF.Sin(h));
        var up = Vector3.Cross(right, fwd);
        if (r != 0)
        {
            var q = Quaternion.CreateFromAxisAngle(fwd, r);
            right = Vector3.Transform(right, q);
            up = Vector3.Transform(up, q);
        }
        return (fwd, right, up);
    }
}
