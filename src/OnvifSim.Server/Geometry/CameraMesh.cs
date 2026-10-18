using System.Numerics;
using OnvifSim.Rendering;
using OnvifSim.World;

namespace OnvifSim.Geometry;

// A camera as other cameras see it: a pole from the ground up to a bracket under the camera, and the
// body with its lens, turned the way the camera looks (for a PTZ camera, its head), the lens ending at
// the eye. A little larger than life, as the editor draws it, so it can be found from across the yard.
public static class CameraMesh
{
    public static readonly Vector3 PtzColor = ColorUtil.Rgb(255, 159, 28);
    public static readonly Vector3 FixedColor = ColorUtil.Rgb(58, 160, 255);
    private static readonly Vector3 Metal = ColorUtil.Rgb(150, 152, 156);
    private static readonly Vector3 Glass = ColorUtil.Rgb(24, 28, 34);

    private const float BodyWidth = 0.6f, BodyHeight = 0.45f, BodyLength = 0.9f;
    private const float LensRadius = 0.15f, LensLength = 0.12f;
    private const float PoleRadius = 0.08f;

    // Its own colour, or its kind's.
    public static Vector3 ColorOf(CameraModel camera) =>
        ColorUtil.Parse(camera.Color, camera.Kind == CameraKind.Ptz ? PtzColor : FixedColor);

    // In world coordinates: the body's colour, the eye, the ground under it and where the camera looks.
    public static Mesh Build(Vector3 color, Vector3 eye, float ground, Orientation look)
    {
        var m = new Mesh();
        float bracket = eye.Y - BodyHeight;
        if (bracket > ground)
        {
            var pole = new Mesh();
            Primitives.Frustum(pole, PoleRadius, PoleRadius, bracket - ground, 6, Metal, caps: false);
            m.Append(pole, Matrix4x4.CreateTranslation(eye.X, ground, eye.Z));
        }
        var plate = new Mesh();
        Primitives.Box(plate, 0.3f, 0.08f, 0.3f, Metal);
        m.Append(plate, Matrix4x4.CreateTranslation(eye.X, bracket - 0.08f, eye.Z));

        // Local space: x right, y up, z backwards from the lens, so -z is where the camera looks.
        var body = new Mesh();
        var box = new Mesh();
        Primitives.Box(box, BodyWidth, BodyHeight, BodyLength, color, -BodyHeight / 2);
        body.Append(box, Matrix4x4.CreateTranslation(0, 0, LensLength + BodyLength / 2));
        var lens = new Mesh();
        Primitives.Frustum(lens, LensRadius, LensRadius, LensLength, 8, Glass);
        body.Append(lens, Matrix4x4.CreateRotationX(-MathF.PI / 2) * Matrix4x4.CreateTranslation(0, 0, LensLength));
        m.Append(body, new Matrix4x4(
            look.Right.X, look.Right.Y, look.Right.Z, 0,
            look.Up.X, look.Up.Y, look.Up.Z, 0,
            -look.Forward.X, -look.Forward.Y, -look.Forward.Z, 0,
            eye.X, eye.Y, eye.Z, 1));
        return m;
    }
}
