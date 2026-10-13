namespace OnvifSim.World;

// Where a walker is on the ground, which way they face (degrees clockwise from north) and how far
// through a stride they are (0..1: a full cycle of both legs).
public readonly record struct WalkerPose(float X, float Z, float Heading, float StridePhase);

// A walker's place at a moment: their route walked at a steady pace from the start of the clock, so
// the same moment always gives the same place, wherever it is asked.
public static class WalkerMotion
{
    // Ground covered by one full stride (a step of each leg).
    private const double StrideLength = 1.5;

    public static WalkerPose PoseAt(WalkerModel walker, DateTimeOffset time)
    {
        var path = PathOf(walker);
        double length = 0;
        for (int i = 1; i < path.Count; i++) length += Distance(path[i - 1], path[i]);
        if (length == 0 || walker.Speed <= 0) return Standing(walker);

        double walked = time.ToUnixTimeMilliseconds() / 1000.0 * walker.Speed;
        float phase = (float)(walked / StrideLength % 1);
        double along = walked % length;
        for (int i = 1; i < path.Count; i++)
        {
            var (from, to) = (path[i - 1], path[i]);
            double segment = Distance(from, to);
            if (along > segment)
            {
                along -= segment;
                continue;
            }
            float t = segment == 0 ? 0 : (float)(along / segment);
            return new WalkerPose(from.X + (to.X - from.X) * t, from.Z + (to.Z - from.Z) * t, HeadingOf(from, to), phase);
        }
        return Standing(walker);
    }

    // The points in walking order for one full round: back to the start for a loop, or the same way
    // back for a walker who turns round at the end.
    private static List<GroundPoint> PathOf(WalkerModel walker)
    {
        var route = walker.Route;
        if (route.Count < 2) return [.. route];
        return walker.Return == WalkerReturn.Loop
            ? [.. route, route[0]]
            : [.. route, .. Enumerable.Reverse(route).Skip(1)];
    }

    // At the first point, facing the second if there is one.
    private static WalkerPose Standing(WalkerModel walker)
    {
        var route = walker.Route;
        if (route.Count == 0) return default;
        float heading = route.Count > 1 ? HeadingOf(route[0], route[1]) : 0;
        return new WalkerPose(route[0].X, route[0].Z, heading, 0);
    }

    private static double Distance(GroundPoint a, GroundPoint b) => Math.Sqrt((b.X - a.X) * (double)(b.X - a.X) + (b.Z - a.Z) * (double)(b.Z - a.Z));

    // North is -Z, east +X.
    private static float HeadingOf(GroundPoint from, GroundPoint to) =>
        MathF.Atan2(to.X - from.X, -(to.Z - from.Z)) * 180 / MathF.PI;
}
