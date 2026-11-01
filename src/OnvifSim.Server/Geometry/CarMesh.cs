using System.Numerics;
using System.Text;

namespace OnvifSim.Geometry;

// A low-poly car facing north (-Z), wheels on y = 0. Its licence plate carries real glyphs: the camera
// renderer has no textures, so the characters are flat quads a few millimetres in front of the plate,
// generated from a 5x7 bitmap font. The same mesh is drawn in the camera pictures and in the editor.
public static class CarMesh
{
    private const int MaxCharacters = 8;

    private static readonly Vector3 Glass = ColorUtil.Rgb(32, 42, 52);
    private static readonly Vector3 Tyre = ColorUtil.Rgb(28, 28, 30);
    private static readonly Vector3 Lamp = ColorUtil.Rgb(240, 225, 170);
    private static readonly Vector3 TailLamp = ColorUtil.Rgb(180, 40, 35);
    private static readonly Vector3 PlateFrame = ColorUtil.Rgb(32, 32, 34);

    // Five bits per row, most significant bit first. Spaces and unknown characters draw nothing.
    private static readonly Dictionary<char, byte[]> Glyphs = new()
    {
        ['0'] = [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110],
        ['1'] = [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
        ['2'] = [0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111],
        ['3'] = [0b11110, 0b00001, 0b00001, 0b01110, 0b00001, 0b00001, 0b11110],
        ['4'] = [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010],
        ['5'] = [0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110],
        ['6'] = [0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110],
        ['7'] = [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000],
        ['8'] = [0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110],
        ['9'] = [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100],
        ['A'] = [0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001],
        ['B'] = [0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110],
        ['C'] = [0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110],
        ['D'] = [0b11110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b11110],
        ['E'] = [0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111],
        ['F'] = [0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000],
        ['G'] = [0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01110],
        ['H'] = [0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001],
        ['I'] = [0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
        ['J'] = [0b00111, 0b00010, 0b00010, 0b00010, 0b00010, 0b10010, 0b01100],
        ['K'] = [0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001],
        ['L'] = [0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111],
        ['M'] = [0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001],
        ['N'] = [0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001, 0b10001],
        ['O'] = [0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110],
        ['P'] = [0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000],
        ['Q'] = [0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101],
        ['R'] = [0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001],
        ['S'] = [0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110],
        ['T'] = [0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100],
        ['U'] = [0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110],
        ['V'] = [0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100],
        ['W'] = [0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b11011, 0b10001],
        ['X'] = [0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001],
        ['Y'] = [0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100],
        ['Z'] = [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111],
        ['-'] = [0b00000, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000],
    };

    public static Mesh Build(float width, float height, float depth, Vector3 paint, Vector3 plateBackground, string? plateText)
    {
        var m = new Mesh();
        float w = width, h = height, d = depth;

        Primitives.Box(m, w, 0.52f * h, d, paint, 0.18f * h);

        var cabin = new Mesh();
        Primitives.Box(cabin, 0.80f * w, 0.32f * h, 0.46f * d, Glass);
        m.Append(cabin, Matrix4x4.CreateTranslation(0, 0.66f * h, 0.05f * d));

        var roof = new Mesh();
        Primitives.Box(roof, 0.76f * w, 0.05f * h, 0.42f * d, paint);
        m.Append(roof, Matrix4x4.CreateTranslation(0, 0.96f * h, 0.05f * d));

        var wheel = new Mesh();
        float wheelRadius = 0.24f * h, wheelWidth = 0.10f * w;
        Primitives.Frustum(wheel, wheelRadius, wheelRadius, wheelWidth, 12, Tyre, -wheelWidth / 2);
        foreach (int side in new[] { -1, 1 })
            foreach (int end in new[] { -1, 1 })
                m.Append(wheel, Matrix4x4.CreateRotationZ(MathF.PI / 2)
                    * Matrix4x4.CreateTranslation(side * (w / 2 - wheelWidth / 2), wheelRadius, end * 0.31f * d));

        foreach (int side in new[] { -1, 1 })
        {
            var lamp = new Mesh();
            Primitives.Box(lamp, 0.16f * w, 0.08f * h, 0.04f * d, Lamp);
            m.Append(lamp, Matrix4x4.CreateTranslation(side * 0.30f * w, 0.52f * h, -d / 2 + 0.01f * d));
            var tail = new Mesh();
            Primitives.Box(tail, 0.16f * w, 0.08f * h, 0.04f * d, TailLamp);
            m.Append(tail, Matrix4x4.CreateTranslation(side * 0.30f * w, 0.52f * h, d / 2 - 0.01f * d));
        }

        float plateWidth = 0.34f * w, plateHeight = 0.10f * h;
        var plate = PlateMesh(plateWidth, plateHeight, plateBackground, plateText);
        float plateY = 0.28f * h;
        m.Append(plate, Matrix4x4.CreateTranslation(0, plateY, -d / 2 - 0.005f));
        m.Append(plate, Matrix4x4.CreateRotationY(MathF.PI) * Matrix4x4.CreateTranslation(0, plateY, d / 2 + 0.005f));
        return m;
    }

    // The plate and its characters in an XY plane facing -Z; layers sit a little in front of each other
    // because the rasterizer compares depth strictly and has no polygon offset.
    private static Mesh PlateMesh(float width, float height, Vector3 background, string? text)
    {
        var m = new Mesh();
        Quad(m, -width / 2, -height / 2, width / 2, height / 2, 0, PlateFrame);
        float border = 0.06f * height;
        Quad(m, -width / 2 + border, -height / 2 + border, width / 2 - border, height / 2 - border,
            -0.002f, background);

        var characters = Sanitize(text);
        if (characters.Length == 0) return m;

        int totalColumns = characters.Length * 6 - 1;
        float pixel = MathF.Min(0.86f * width / totalColumns, 0.66f * height / 7);
        float left = -totalColumns * pixel / 2;
        float top = 3.5f * pixel;
        var ink = Luminance(background) > 0.5f ? ColorUtil.Rgb(16, 16, 16) : ColorUtil.Rgb(240, 240, 240);
        for (int i = 0; i < characters.Length; i++)
        {
            if (!Glyphs.TryGetValue(characters[i], out var rows)) continue;
            float glyphLeft = left + i * 6 * pixel;
            for (int row = 0; row < 7; row++)
                for (int column = 0; column < 5; column++)
                    if ((rows[row] & (1 << (4 - column))) != 0)
                    {
                        float x = glyphLeft + column * pixel;
                        float y = top - (row + 1) * pixel;
                        Quad(m, x, y, x + pixel, y + pixel, -0.004f, ink);
                    }
        }
        return m;
    }

    // A flat face towards -Z, counter-clockwise seen from the front. The x coordinates are mirrored:
    // a viewer in front of that face looks along +Z, so its right is local -X, and text laid out with
    // increasing x would otherwise read backwards.
    private static void Quad(Mesh m, float x0, float y0, float x1, float y1, float z, Vector3 color) =>
        m.AddFlatQuad(new(-x0, y0, z), new(-x1, y0, z), new(-x1, y1, z), new(-x0, y1, z), color);

    // Null keeps the default plate; an empty string draws a blank one. Characters the font does not have
    // are dropped, which also keeps a hand-written world or /api/meshes from asking for shapes we cannot draw.
    private static string Sanitize(string? text)
    {
        if (text == null) return "A123BC";
        var kept = new StringBuilder(MaxCharacters);
        foreach (char c in text.Trim().ToUpperInvariant())
        {
            bool drawn = c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or ' ';
            if (drawn && kept.Length < MaxCharacters) kept.Append(c);
        }
        return kept.ToString();
    }

    private static float Luminance(Vector3 color) => 0.2126f * color.X + 0.7152f * color.Y + 0.0722f * color.Z;
}
