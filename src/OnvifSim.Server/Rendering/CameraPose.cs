using System.Numerics;
using OnvifSim.World;

namespace OnvifSim.Rendering;

// Where a camera is and where it looks: its eye, the unit vectors of its picture (forward into the
// scene, right and up along the picture's edges) and its horizontal field of view. Heading, Pitch and
// Roll describe the same orientation as angles, for reports: heading is degrees clockwise from north
// (-Z), pitch degrees up from the horizon, roll turns the picture's frame clockwise as seen from behind.
public readonly record struct CameraPose(Vector3 Position, Vector3 Forward, Vector3 Right, Vector3 Up, float HFov)
{
    // A pose from angles, as a fixed camera's mount is given.
    public CameraPose(Vector3 position, float heading, float pitch, float roll, float hfov)
        : this(position, Orientation.FromAngles(heading, pitch, roll), hfov) { }

    private CameraPose(Vector3 position, Orientation orientation, float hfov)
        : this(position, orientation.Forward, orientation.Right, orientation.Up, hfov) { }

    // Pose of a camera from its configuration; a PTZ camera adds its stored pan/tilt/zoom to the mount.
    public static CameraPose From(CameraModel cam, Terrain terrain) => From(cam, terrain, cam.Ptz?.Position ?? default);

    // Pose of a camera whose PTZ head is at ptz (a fixed camera ignores it). The mount is a rigid body:
    // the head pans about the mount's own up axis and tilts about its own right axis after the pan, so
    // a mount hung upside down, on its side or leaning turns the head with it. The picture is then
    // turned by the video's Rotate, the way a camera rotates its image digitally.
    public static CameraPose From(CameraModel cam, Terrain terrain, PtzPosition ptz)
    {
        var pos = new Vector3(cam.Position.X, terrain.HeightAt(cam.Position.X, cam.Position.Z) + cam.Position.Y, cam.Position.Z);
        var orientation = cam.Kind == CameraKind.Ptz && cam.Ptz != null ? HeadOrientation(cam, ptz.Pan, ptz.Tilt) : Mount(cam);
        float hfov = cam.Kind == CameraKind.Ptz && cam.Ptz is { } limits ? ZoomToHFov(ptz.Zoom, limits.FovWide, limits.FovTele) : cam.HFov;
        return new CameraPose(pos, orientation.Rolled(-cam.Video.Rotate), hfov);
    }

    // The orientation of the camera's mount: for a PTZ camera, where the head points at pan = tilt = 0.
    public static Orientation Mount(CameraModel cam) => Orientation.FromAngles(cam.Heading, cam.Pitch, cam.Roll);

    // Where a PTZ head at pan/tilt (degrees, relative to the mount) points, before the picture's Rotate.
    public static Orientation HeadOrientation(CameraModel cam, float pan, float tilt) => Mount(cam).Compose(Orientation.FromAngles(pan, tilt, 0));

    // The pan and tilt (degrees, relative to the mount) at which a PTZ head looks along a world direction:
    // the inverse of HeadOrientation. Pan is -180..180; looking straight along the mount's up axis, any
    // pan does and 0 is returned.
    public static (float Pan, float Tilt) PanTiltTowards(CameraModel cam, Vector3 direction)
    {
        var local = Mount(cam).ToLocal(Vector3.Normalize(direction));
        float tilt = MathF.Asin(Math.Clamp(local.Y, -1, 1)) * 180 / MathF.PI;
        float pan = MathF.Abs(local.Y) > 0.999999f ? 0 : MathF.Atan2(local.X, -local.Z) * 180 / MathF.PI;
        return (pan, tilt);
    }

    // Zoom 0..1 maps to focal length geometrically between the wide and tele ends, which is how a real
    // zoom lens feels: equal zoom steps give equal magnification ratios.
    public static float ZoomToHFov(float zoom, float fovWide, float fovTele)
    {
        float fw = 1 / MathF.Tan(fovWide * MathF.PI / 360), ft = 1 / MathF.Tan(fovTele * MathF.PI / 360);
        float f = fw * MathF.Pow(ft / fw, Math.Clamp(zoom, 0, 1));
        return 2 * MathF.Atan(1 / f) * 180 / MathF.PI;
    }

    // 0..360.
    public float Heading
    {
        get
        {
            float heading = MathF.Atan2(Forward.X, -Forward.Z) * 180 / MathF.PI;
            return heading < 0 ? heading + 360 : heading;
        }
    }

    public float Pitch => MathF.Asin(Math.Clamp(Forward.Y, -1, 1)) * 180 / MathF.PI;

    // The angle from the level frame at this heading and pitch to the picture's frame.
    public float Roll
    {
        get
        {
            var level = Orientation.FromAngles(Heading, Pitch, 0);
            return MathF.Atan2(-Vector3.Dot(Right, level.Up), Vector3.Dot(Right, level.Right)) * 180 / MathF.PI;
        }
    }

    // World to clip space for a picture of the given aspect (width / height), out to far: x and y of
    // clip / w run -1..1 from left to right and from bottom to top.
    public Matrix4x4 ViewProjection(float aspect, float far)
    {
        var (fwd, right, up) = Basis();
        var view = new Matrix4x4(
            right.X, up.X, -fwd.X, 0,
            right.Y, up.Y, -fwd.Y, 0,
            right.Z, up.Z, -fwd.Z, 0,
            -Vector3.Dot(right, Position), -Vector3.Dot(up, Position), Vector3.Dot(fwd, Position), 1);
        float tanV = HalfTangent / aspect;
        return view * Matrix4x4.CreatePerspectiveFieldOfView(2 * MathF.Atan(tanV), aspect, ViewFrustum.Near, far);
    }

    // Tangent of half the horizontal field of view, which is kept to a range the projection can take.
    public float HalfTangent => MathF.Tan(Math.Clamp(HFov, 0.5f, 170f) * MathF.PI / 360);

    // Camera basis: forward, right and up unit vectors in world space.
    public (Vector3 Forward, Vector3 Right, Vector3 Up) Basis() => (Forward, Right, Up);
}

// An orthonormal frame in world space: Forward is where it looks, Right and Up span the plane across
// it, Up = Right x Forward.
public readonly record struct Orientation(Vector3 Forward, Vector3 Right, Vector3 Up)
{
    // From a compass heading (degrees clockwise from north, -Z), a pitch (degrees up from the horizon)
    // and a roll (degrees, clockwise as seen from behind).
    public static Orientation FromAngles(float heading, float pitch, float roll)
    {
        float h = heading * MathF.PI / 180, p = pitch * MathF.PI / 180;
        var fwd = new Vector3(MathF.Sin(h) * MathF.Cos(p), MathF.Sin(p), -MathF.Cos(h) * MathF.Cos(p));
        // Built from the heading rather than cross(fwd, worldUp) so looking straight down still works.
        var right = new Vector3(MathF.Cos(h), 0, MathF.Sin(h));
        var up = Vector3.Cross(right, fwd);
        return new Orientation(fwd, right, up).Rolled(roll);
    }

    // Turned about its own forward axis by degrees, clockwise as seen from behind.
    public Orientation Rolled(float degrees)
    {
        if (degrees == 0) return this;
        var q = Quaternion.CreateFromAxisAngle(Forward, degrees * MathF.PI / 180);
        return this with { Right = Vector3.Transform(Right, q), Up = Vector3.Transform(Up, q) };
    }

    // A world vector in this frame's terms, laid out as world coordinates are: x along Right, y along
    // Up, z backwards (as world z points south, away from a frame that looks north).
    public Vector3 ToLocal(Vector3 world) => new(Vector3.Dot(world, Right), Vector3.Dot(world, Up), -Vector3.Dot(world, Forward));

    // The inverse of ToLocal.
    public Vector3 ToWorld(Vector3 local) => Right * local.X + Up * local.Y - Forward * local.Z;

    // A frame given relative to this one (its vectors in ToLocal's terms) in world space.
    public Orientation Compose(Orientation relative) =>
        new(ToWorld(relative.Forward), ToWorld(relative.Right), ToWorld(relative.Up));
}
