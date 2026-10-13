using System.Numerics;

namespace OnvifSim.Geometry;

// A low-poly person of the given height facing north (-Z), feet on y = 0, caught at a point of their
// stride: legs swing from the hips and arms from the shoulders, each arm against its leg.
public static class PersonMesh
{
    private const float LegSwing = 28 * MathF.PI / 180;
    private const float ArmSwing = 22 * MathF.PI / 180;

    private static readonly Vector3 Skin = ColorUtil.Rgb(214, 170, 130);
    private static readonly Vector3 Shoes = ColorUtil.Rgb(40, 36, 34);

    public static Mesh Build(float height, float stridePhase, Vector3 top, Vector3 trousers)
    {
        var m = new Mesh();
        float h = height;
        float swing = MathF.Sin(stridePhase * MathF.Tau);
        float hip = 0.48f * h, shoulder = 0.8f * h;

        Limb(m, new Vector3(-0.06f * h, hip, 0), 0.075f * h, 0.47f * h, swing * LegSwing, trousers, Shoes);
        Limb(m, new Vector3(0.06f * h, hip, 0), 0.075f * h, 0.47f * h, -swing * LegSwing, trousers, Shoes);
        Primitives.Box(m, 0.23f * h, shoulder - hip + 0.02f * h, 0.13f * h, top, hip - 0.02f * h);
        Limb(m, new Vector3(-0.16f * h, shoulder - 0.01f * h, 0), 0.06f * h, 0.36f * h, -swing * ArmSwing, top, Skin);
        Limb(m, new Vector3(0.16f * h, shoulder - 0.01f * h, 0), 0.06f * h, 0.36f * h, swing * ArmSwing, top, Skin);
        Primitives.Box(m, 0.06f * h, 0.04f * h, 0.06f * h, Skin, shoulder);
        Primitives.Blob(m, new Vector3(0.065f * h, 0.075f * h, 0.07f * h), new Vector3(0, 0.915f * h, 0), 0, 0, Skin, Vector3.Zero);
        return m;
    }

    // An arm or a leg hanging from a joint, turned forward by angle (radians; positive moves the end
    // north), with its last tenth in the end colour: a hand or a shoe.
    private static void Limb(Mesh m, Vector3 joint, float thickness, float length, float angle, Vector3 color, Vector3 endColor)
    {
        var limb = new Mesh();
        Primitives.Box(limb, thickness, length * 0.9f, thickness, color, -length * 0.9f);
        Primitives.Box(limb, thickness * 1.05f, length * 0.1f, thickness * 1.3f, endColor, -length);
        m.Append(limb, Matrix4x4.CreateRotationX(angle) * Matrix4x4.CreateTranslation(joint));
    }
}
