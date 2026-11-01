namespace OnvifSim.World;

// Where a walker is on the ground, which way they face (degrees clockwise from north) and how far
// through a stride they are (0..1: a full cycle of both legs).
public readonly record struct WalkerPose(float X, float Z, float Heading, float StridePhase);

// Where something travelling a route is at a moment, and how far it has travelled since the clock began.
// Walkers use the distance for their stride; vehicles only need the place and the heading.
public readonly record struct RoutePose(float X, float Z, float Heading, double Walked);

// A route walked at a steady pace from the start of the clock, so the same moment always gives the same
// place, wherever it is asked. A single point stands still; a loop comes back to the start, a reverse
// route goes back the same way. Walkers and vehicles share it, so both move by one rule.
public static class RouteMotion
{
    public static RoutePose PoseAt(IReadOnlyList<GroundPoint> route, float speed, RouteReturn returnMode, DateTimeOffset time)
    {
        var path = PathOf(route, returnMode);
        double length = 0;
        for (int i = 1; i < path.Count; i++) length += Distance(path[i - 1], path[i]);
        if (length == 0 || speed <= 0) return Standing(route);

        double walked = time.ToUnixTimeMilliseconds() / 1000.0 * speed;
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
            return new RoutePose(from.X + (to.X - from.X) * t, from.Z + (to.Z - from.Z) * t, HeadingOf(from, to), walked);
        }
        return Standing(route);
    }

    // The points in travel order for one full round: back to the start for a loop, or the same way back
    // for a route that turns round at the end.
    private static List<GroundPoint> PathOf(IReadOnlyList<GroundPoint> route, RouteReturn returnMode)
    {
        if (route.Count < 2) return [.. route];
        return returnMode == RouteReturn.Loop
            ? [.. route, route[0]]
            : [.. route, .. Enumerable.Reverse(route).Skip(1)];
    }

    // At the first point, facing the second if there is one.
    private static RoutePose Standing(IReadOnlyList<GroundPoint> route)
    {
        if (route.Count == 0) return default;
        float heading = route.Count > 1 ? HeadingOf(route[0], route[1]) : 0;
        return new RoutePose(route[0].X, route[0].Z, heading, 0);
    }

    private static double Distance(GroundPoint a, GroundPoint b) => Math.Sqrt((b.X - a.X) * (double)(b.X - a.X) + (b.Z - a.Z) * (double)(b.Z - a.Z));

    // North is -Z, east +X.
    private static float HeadingOf(GroundPoint from, GroundPoint to) =>
        MathF.Atan2(to.X - from.X, -(to.Z - from.Z)) * 180 / MathF.PI;
}

// A walker's place at a moment: the route walked at a steady pace, plus which part of a stride they are
// in. Ground covered by one full stride (a step of each leg).
public static class WalkerMotion
{
    private const double StrideLength = 1.5;

    public static WalkerPose PoseAt(WalkerModel walker, DateTimeOffset time)
    {
        var pose = RouteMotion.PoseAt(walker.Route, walker.Speed, walker.Return, time);
        return new WalkerPose(pose.X, pose.Z, pose.Heading, (float)(pose.Walked / StrideLength % 1));
    }
}

// A vehicle's place at a moment, by the same route rule. Its heading follows the segment it is on.
public static class VehicleMotion
{
    public static RoutePose PoseAt(VehicleModel vehicle, DateTimeOffset time) =>
        RouteMotion.PoseAt(vehicle.Route, vehicle.Speed, vehicle.Return, time);
}
