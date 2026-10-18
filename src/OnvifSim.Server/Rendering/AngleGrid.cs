using System.Globalization;
using System.Numerics;
using OnvifSim.World;

namespace OnvifSim.Rendering;

// A pan/tilt grid over a camera's picture: lines of equal pan and tilt, with their angles written on
// them, relative to the camera's mount, so a PTZ camera shows the very angles its head is at (pan 0,
// tilt 0 is the mount orientation; for a fixed camera, its own direction). The lines mark directions,
// not places: they sit at infinity like the sky and are drawn over everything, so the tilt scale stays
// readable below the horizon. The spacing follows the zoom, keeping a few lines across the picture.
public static class AngleGrid
{
    private static readonly float[] Steps = [1, 2, 5, 10, 15, 30, 45];
    private const int MaxLinesAcross = 5;
    private const int SamplesPerLine = 64;

    private static readonly Rgb LineColor = new(255, 255, 255);
    private static readonly Rgb PanColor = new(255, 220, 80);
    private static readonly Rgb TiltColor = new(120, 220, 255);
    private static readonly Rgb Outline = new(20, 20, 20);

    public static void Draw(Frame frame, CameraModel camera, CameraPose pose, PtzPosition ptz)
    {
        var view = new View(frame, pose);
        float hfov = Math.Clamp(pose.HFov, 0.5f, 170f);
        float step = Steps.FirstOrDefault(s => hfov / s <= MaxLinesAcross, Steps[^1]);
        // Where the picture's centre points, in mount-relative degrees.
        float centrePan = camera.Kind == CameraKind.Ptz ? ptz.Pan : 0;
        float centreTilt = camera.Kind == CameraKind.Ptz ? ptz.Tilt : 0;

        // The angular radius of the picture bounds the lines worth drawing; near a pole every pan is.
        float radius = MathF.Atan(MathF.Sqrt(view.TanH * view.TanH + view.TanV * view.TanV)) * 180 / MathF.PI;
        float tiltLow = Math.Max(-90, centreTilt - radius), tiltHigh = Math.Min(90, centreTilt + radius);
        float nearestPole = Math.Max(Math.Abs(tiltLow), Math.Abs(tiltHigh));
        float panHalf = nearestPole >= 89 ? 180 : Math.Min(180, radius / MathF.Cos(nearestPole * MathF.PI / 180));
        float panLow = centrePan - panHalf, panHigh = centrePan + panHalf;

        // In the mount's own frame, so the lines stay lines of equal pan and tilt on a mount that leans
        // or hangs on its side.
        Vector3 Direction(float pan, float tilt) => CameraPose.HeadOrientation(camera, pan, tilt).Forward;

        for (float pan = MathF.Ceiling(panLow / step) * step; pan <= panHigh; pan += step)
            DrawCurve(frame, view, i => Direction(pan, tiltLow + (tiltHigh - tiltLow) * i / SamplesPerLine));
        for (float tilt = MathF.Ceiling(tiltLow / step) * step; tilt <= tiltHigh; tilt += step)
            DrawCurve(frame, view, i => Direction(panLow + (panHigh - panLow) * i / SamplesPerLine, tilt));

        // Pan values along the line of tilt nearest the centre, tilt values along the nearest line of pan.
        int scale = Math.Max(1, frame.Height / 180);
        float labelTilt = Math.Clamp(MathF.Round(centreTilt / step) * step, -90, 90);
        float labelPan = MathF.Round(centrePan / step) * step;
        for (float pan = MathF.Ceiling(panLow / step) * step; pan <= panHigh; pan += step)
            if (view.Project(Direction(pan, labelTilt)) is { } at)
                DrawText(frame, string.Create(CultureInfo.InvariantCulture, $"{WrapPan(pan):0.#}°"), (int)at.X + 2 * scale, (int)at.Y + 2 * scale, scale, PanColor);
        for (float tilt = MathF.Ceiling(tiltLow / step) * step; tilt <= tiltHigh; tilt += step)
            if (view.Project(Direction(labelPan, tilt)) is { } at)
                DrawText(frame, string.Create(CultureInfo.InvariantCulture, $"{tilt:+0.#;-0.#;0}°"), (int)at.X + 2 * scale, (int)at.Y - 7 * scale, scale, TiltColor);
    }

    // Pan as -180..180, the way the default limits count it.
    private static float WrapPan(float pan)
    {
        float wrapped = ((pan + 180) % 360 + 360) % 360 - 180;
        return wrapped == -180 ? 180 : wrapped;
    }

    private static void DrawCurve(Frame frame, View view, Func<int, Vector3> sample)
    {
        Vector2? previous = null;
        for (int i = 0; i <= SamplesPerLine; i++)
        {
            var point = view.Project(sample(i));
            if (point is { } p && previous is { } q) DrawLine(frame, q, p, LineColor);
            previous = point;
        }
    }

    // Half-transparent, one pixel wide, clipped to the frame.
    private static void DrawLine(Frame frame, Vector2 from, Vector2 to, Rgb color)
    {
        var delta = to - from;
        int steps = (int)MathF.Ceiling(Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y)));
        if (steps > frame.Width + frame.Height) return; // a segment through a point behind the camera
        for (int i = 0; i <= steps; i++)
        {
            var p = steps == 0 ? from : from + delta * i / steps;
            Blend(frame, (int)p.X, (int)p.Y, color, 0.45f);
        }
    }

    private static void DrawText(Frame frame, string text, int x, int y, int scale, Rgb color)
    {
        // An outline first, so the digits read on sky and grass alike.
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (dx != 0 || dy != 0) DrawGlyphs(frame, text, x + dx * scale, y + dy * scale, scale, Outline);
        DrawGlyphs(frame, text, x, y, scale, color);
    }

    private static void DrawGlyphs(Frame frame, string text, int x, int y, int scale, Rgb color)
    {
        foreach (char c in text)
        {
            if (Font.TryGetValue(c, out var rows))
                for (int row = 0; row < rows.Length; row++)
                    for (int col = 0; col < 3; col++)
                        if ((rows[row] >> (2 - col) & 1) != 0)
                            FillBlock(frame, x + col * scale, y + row * scale, scale, color);
            x += 4 * scale;
        }
    }

    private static void FillBlock(Frame frame, int x, int y, int size, Rgb color)
    {
        for (int dy = 0; dy < size; dy++)
            for (int dx = 0; dx < size; dx++)
                Blend(frame, x + dx, y + dy, color, 1);
    }

    private static void Blend(Frame frame, int x, int y, Rgb color, float alpha)
    {
        if ((uint)x >= (uint)frame.Width || (uint)y >= (uint)frame.Height) return;
        int i = (y * frame.Width + x) * 4;
        var rgba = frame.Rgba;
        rgba[i] = (byte)(rgba[i] + (color.R - rgba[i]) * alpha);
        rgba[i + 1] = (byte)(rgba[i + 1] + (color.G - rgba[i + 1]) * alpha);
        rgba[i + 2] = (byte)(rgba[i + 2] + (color.B - rgba[i + 2]) * alpha);
    }

    private readonly record struct Rgb(byte R, byte G, byte B);

    // Projects directions the way SoftwareRasterizer projects geometry; null behind the camera.
    private readonly struct View(Frame frame, CameraPose pose)
    {
        private readonly (Vector3 Forward, Vector3 Right, Vector3 Up) _basis = pose.Basis();

        public float TanH { get; } = MathF.Tan(Math.Clamp(pose.HFov, 0.5f, 170f) * MathF.PI / 360);
        public float TanV => TanH * frame.Height / frame.Width;

        public Vector2? Project(Vector3 direction)
        {
            float depth = Vector3.Dot(direction, _basis.Forward);
            if (depth < 0.01f) return null;
            float x = Vector3.Dot(direction, _basis.Right) / depth / TanH;
            float y = Vector3.Dot(direction, _basis.Up) / depth / TanV;
            return new Vector2((x + 1) * 0.5f * frame.Width, (1 - y) * 0.5f * frame.Height);
        }
    }

    // 3x5 pixel glyphs, one row per entry, high bit on the left.
    private static readonly Dictionary<char, byte[]> Font = new()
    {
        ['0'] = [0b111, 0b101, 0b101, 0b101, 0b111],
        ['1'] = [0b010, 0b110, 0b010, 0b010, 0b111],
        ['2'] = [0b111, 0b001, 0b111, 0b100, 0b111],
        ['3'] = [0b111, 0b001, 0b111, 0b001, 0b111],
        ['4'] = [0b101, 0b101, 0b111, 0b001, 0b001],
        ['5'] = [0b111, 0b100, 0b111, 0b001, 0b111],
        ['6'] = [0b111, 0b100, 0b111, 0b101, 0b111],
        ['7'] = [0b111, 0b001, 0b010, 0b010, 0b010],
        ['8'] = [0b111, 0b101, 0b111, 0b101, 0b111],
        ['9'] = [0b111, 0b101, 0b111, 0b001, 0b111],
        ['-'] = [0b000, 0b000, 0b111, 0b000, 0b000],
        ['+'] = [0b000, 0b010, 0b111, 0b010, 0b000],
        ['.'] = [0b000, 0b000, 0b000, 0b000, 0b010],
        ['°'] = [0b010, 0b101, 0b010, 0b000, 0b000],
    };
}
