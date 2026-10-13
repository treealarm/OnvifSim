using System.Numerics;

namespace OnvifSim.Rendering;

// What a camera pose takes in, for a picture of a given aspect (width / height) out to a far distance:
// spheres wholly outside it can be skipped, by the renderer and by anything deciding whether a frame
// would change.
public readonly struct ViewFrustum
{
    public const float Near = 0.3f;

    private readonly Vector3 _eye, _right, _up, _forward;
    private readonly float _far;
    // The side planes, as the cosine and sine of the half angles.
    private readonly float _cosH, _sinH, _cosV, _sinV;

    public ViewFrustum(in CameraPose pose, float aspect, float far)
    {
        (_forward, _right, _up) = pose.Basis();
        _eye = pose.Position;
        _far = far;
        float tanH = MathF.Tan(Math.Clamp(pose.HFov, 0.5f, 170f) * MathF.PI / 360);
        float tanV = tanH / aspect;
        _cosH = 1 / MathF.Sqrt(1 + tanH * tanH);
        _sinH = tanH * _cosH;
        _cosV = 1 / MathF.Sqrt(1 + tanV * tanV);
        _sinV = tanV * _cosV;
    }

    public bool Sees(Vector3 center, float radius)
    {
        // In view space: x right, y up, z towards the viewer.
        var d = center - _eye;
        float x = Vector3.Dot(d, _right), y = Vector3.Dot(d, _up), z = -Vector3.Dot(d, _forward);
        return !(z - radius > -Near || -z - radius > _far ||
                 x * _cosH + z * _sinH > radius || -x * _cosH + z * _sinH > radius ||
                 y * _cosV + z * _sinV > radius || -y * _cosV + z * _sinV > radius);
    }
}
