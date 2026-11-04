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

public enum DetectionKind { Person, Vehicle }

// Where the object stands on the ground, and which way it faces (degrees clockwise from north).
public readonly record struct GroundPose(float X, float Z, float Heading);

// Where an object is at that moment: ground truth that no real analytics engine has, kept for tests
// that check what a client made of the box.
public readonly record struct Sighting(string ObjectId, DetectionKind Kind, FrameBox Box, GroundPose Pose);

// The people and cars a camera sees, as an analytics engine on the camera would report them, except
// that these are exact: the box is the figure as the picture draws it, cut to the frame. An object
// counts as seen when some of its body is in plain view (not hidden behind a house, a tree or a hill)
// and it is tall enough in the picture for a detector to pick it out. Objects whose owner turned
// Detectable off are never reported.
public static class ObjectDetector
{
    // A figure shorter than this (in normalized units, so 0.05 is 2.5% of the frame height, 9 pixels at
    // 360 lines) is too small to detect.
    public const float MinBoxHeight = 0.05f;

    // Heights up the body, as fractions of its height, from which the camera is looked at: head, chest,
    // hips, knees for a person; roof, windows, wheels for a car. One clear line is enough.
    private static readonly float[] SightHeights = [0.9f, 0.65f, 0.4f, 0.15f];

    // What the camera's picture at the size of its video source shows of the walkers and cars at the time.
    public static List<Sighting> Detect(SceneState.Snapshot snapshot, CameraModel camera, PtzPosition ptz, DateTimeOffset time)
    {
        var scene = snapshot.Scene;
        var pose = CameraPose.From(camera, snapshot.World, time, ptz);
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
            if (!InPlainView(pose, scene, walk.X, walk.Z, walker.Height)) continue;
            sightings.Add(new Sighting(walker.Id, DetectionKind.Person, box, new GroundPose(walk.X, walk.Z, walk.Heading)));
        }
        foreach (var vehicle in snapshot.World.Vehicles.Where(v => v.Detectable &&
            !string.Equals(v.Id, camera.MountedOnVehicleId, StringComparison.OrdinalIgnoreCase)))
        {
            var drive = VehicleMotion.PoseAt(vehicle, time);
            var figure = SceneBuilder.VehicleChunk(vehicle, drive, scene.Terrain);
            if (!frustum.Sees(figure.Center, figure.Radius)) continue;
            if (BoxOf(figure.Mesh.Positions, viewProjection) is not { } box || box.Top - box.Bottom < MinBoxHeight) continue;
            if (!InPlainView(pose, scene, drive.X, drive.Z, vehicle.Height)) continue;
            sightings.Add(new Sighting(vehicle.Id, DetectionKind.Vehicle, box, new GroundPose(drive.X, drive.Z, drive.Heading)));
        }
        return sightings;
    }

    // Whether at least one line from the camera to the body gets past houses, trees and hills.
    private static bool InPlainView(CameraPose pose, RenderScene scene, float x, float z, float height)
    {
        var feet = new Vector3(x, scene.Terrain.HeightAt(x, z), z);
        return SightHeights.Any(h => LineOfSight.IsClear(scene, pose.Position, feet + Vector3.UnitY * (height * h)));
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
