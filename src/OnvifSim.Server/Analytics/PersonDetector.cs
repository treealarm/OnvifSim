using System.Numerics;
using OnvifSim.Geometry;
using OnvifSim.Rendering;
using OnvifSim.World;

namespace OnvifSim.Analytics;

// A box in ONVIF's normalized frame coordinates: x runs from -1 at the left edge to 1 at the right,
// y from -1 at the bottom to 1 at the top.
public readonly record struct FrameBox(float Left, float Top, float Right, float Bottom)
{
    public float CenterX => (Left + Right) / 2;
    public float CenterY => (Top + Bottom) / 2;
}

// Walk is where the walker stands at that moment: ground truth that no real analytics engine has, kept
// for tests that check what a client made of the box.
public readonly record struct Sighting(string WalkerId, FrameBox Box, WalkerPose Walk);

// The people a camera sees, as an analytics engine on the camera would report them, except that these
// are exact: the box is the walker's figure as the picture draws it, cut to the frame. A person counts
// as seen when some of the body is in plain view (not hidden behind a house, a tree or a hill) and the
// figure is tall enough in the picture for a detector to pick it out. Walkers who are not Detectable
// are never reported.
public static class PersonDetector
{
    // A figure shorter than this (in normalized units, so 0.05 is 2.5% of the frame height, 9 pixels at
    // 360 lines) is too small to detect.
    public const float MinBoxHeight = 0.05f;

    // Heights up the body, as fractions of the person's height, from which the camera is looked at:
    // head, chest, hips, knees. One clear line is enough.
    private static readonly float[] SightHeights = [0.9f, 0.65f, 0.4f, 0.15f];

    // What the camera's picture at the size of its video source shows of the walkers at the time.
    public static List<Sighting> Detect(SceneState.Snapshot snapshot, CameraModel camera, PtzPosition ptz, DateTimeOffset time)
    {
        var scene = snapshot.Scene;
        var pose = CameraPose.From(camera, scene.Terrain, ptz);
        float aspect = (float)camera.Video.Width / camera.Video.Height;
        var frustum = new ViewFrustum(pose, aspect, scene.FogEnd);
        var viewProjection = pose.ViewProjection(aspect, scene.FogEnd);

        var sightings = new List<Sighting>();
        foreach (var walker in snapshot.World.Walkers.Where(w => w.Detectable))
        {
            var walk = WalkerMotion.PoseAt(walker, time);
            var figure = SceneBuilder.WalkerChunk(walker, walk, scene.Terrain);
            if (!frustum.Sees(figure.Center, figure.Radius)) continue;
            if (BoxOf(figure.Mesh.Positions, viewProjection) is not { } box || box.Top - box.Bottom < MinBoxHeight) continue;

            var feet = new Vector3(walk.X, scene.Terrain.HeightAt(walk.X, walk.Z), walk.Z);
            if (SightHeights.Any(h => LineOfSight.IsClear(scene, pose.Position, feet + Vector3.UnitY * (walker.Height * h))))
                sightings.Add(new Sighting(walker.Id, box, walk));
        }
        return sightings;
    }

    // The frame box around the points in front of the camera, cut to the frame; null when nothing of
    // it is inside.
    private static FrameBox? BoxOf(IEnumerable<Vector3> points, Matrix4x4 viewProjection)
    {
        float left = float.MaxValue, right = float.MinValue, bottom = float.MaxValue, top = float.MinValue;
        foreach (var point in points)
        {
            var clip = Vector4.Transform(new Vector4(point, 1), viewProjection);
            if (clip.W < ViewFrustum.Near) continue;
            float x = clip.X / clip.W, y = clip.Y / clip.W;
            (left, right) = (MathF.Min(left, x), MathF.Max(right, x));
            (bottom, top) = (MathF.Min(bottom, y), MathF.Max(top, y));
        }
        (left, right, bottom, top) = (MathF.Max(left, -1), MathF.Min(right, 1), MathF.Max(bottom, -1), MathF.Min(top, 1));
        return left < right && bottom < top ? new FrameBox(left, top, right, bottom) : null;
    }
}
