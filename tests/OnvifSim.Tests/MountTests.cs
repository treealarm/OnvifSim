using System.Numerics;
using OnvifSim.Onvif.Services;
using OnvifSim.Rendering;
using OnvifSim.World;

namespace OnvifSim.Tests;

// How a camera's mount, its PTZ head and its picture rotation combine into where it looks, and how ONVIF
// counts a reversed axis.
public class MountTests
{
    private static readonly Terrain Flat = new(new TerrainModel { Size = 100, Resolution = 4, Heights = new float[25] });

    private static CameraModel Ptz(float heading = 0, float pitch = 0, float roll = 0, int rotate = 0) => new()
    {
        Id = "ptz", Kind = CameraKind.Ptz, Position = new Vec3(0, 5, 0), Heading = heading, Pitch = pitch, Roll = roll,
        Video = new VideoModel { Rotate = rotate }, Ptz = new PtzModel { TiltMin = -90, TiltMax = 90 },
    };

    private static CameraPose Pose(CameraModel cam, float pan, float tilt) => CameraPose.From(cam, Flat, new PtzPosition(pan, tilt, 0));

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-5f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    [Theory]
    [InlineData(0f)]
    [InlineData(77f)]
    [InlineData(240f)]
    public void ALevelMount_PansAboutTheVertical_AndTiltsFromTheHorizon(float heading)
    {
        var cam = Ptz(heading);
        foreach (float pan in new[] { -170f, -45f, 0f, 30f, 179f })
            foreach (float tilt in new[] { -89f, -30f, 0f, 15f })
            {
                var pose = Pose(cam, pan, tilt);
                var level = new CameraPose(pose.Position, heading + pan, tilt, 0, pose.HFov);
                Near(level.Forward, pose.Forward);
                Near(level.Right, pose.Right);
                Near(level.Up, pose.Up);
            }
    }

    [Fact]
    public void AMountUpsideDown_MirrorsBothAxes_AndTurnsThePictureOver()
    {
        var cam = Ptz(heading: 50, roll: 180);
        var level = Ptz(heading: 50);
        foreach (float pan in new[] { -60f, 10f, 120f })
            foreach (float tilt in new[] { -40f, 0f, 25f })
                Near(Pose(level, -pan, -tilt).Forward, Pose(cam, pan, tilt).Forward);
        Assert.True(Pose(cam, 0, 0).Up.Y < -0.99f, "the picture is upside down");
    }

    [Fact]
    public void AMountOnItsSide_PansUpAndDown_AndTiltsAcross()
    {
        var cam = Ptz(heading: 90, roll: 90);
        // Pan alone leaves the horizontal plane; tilt alone stays in it.
        Assert.True(MathF.Abs(Pose(cam, 30, 0).Forward.Y) > 0.49f);
        Assert.True(MathF.Abs(Pose(cam, 0, 30).Forward.Y) < 1e-5f);
        Assert.NotEqual(Pose(cam, 0, 0).Heading, Pose(cam, 0, 30).Heading, 1);
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(200f, -10f, 0f)]
    [InlineData(30f, 0f, 90f)]
    [InlineData(300f, 0f, 180f)]
    [InlineData(120f, 25f, -35f)]
    public void PanTiltTowards_IsWhereTheHeadLooksAlongADirection(float heading, float pitch, float roll)
    {
        var cam = Ptz(heading, pitch, roll);
        var rng = new Random(7);
        for (int i = 0; i < 50; i++)
        {
            var direction = Vector3.Normalize(new Vector3(rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1));
            var (pan, tilt) = CameraPose.PanTiltTowards(cam, direction);
            Near(direction, CameraPose.HeadOrientation(cam, pan, tilt).Forward, 1e-4f);
        }
    }

    // Where a world point lands in a picture, 0..1 from the top-left.
    private static Vector2 Project(CameraPose pose, Vector3 point, int width, int height)
    {
        var clip = Vector4.Transform(new Vector4(point, 1), pose.ViewProjection((float)width / height, 1000));
        return new Vector2((clip.X / clip.W + 1) / 2, (1 - clip.Y / clip.W) / 2);
    }

    [Fact]
    public void Rotate180_TurnsThePictureOver()
    {
        var point = new Vector3(3, 6.5f, -20);
        var upright = Project(Pose(Ptz(), 0, 0), point, 640, 360);
        var turned = Project(Pose(Ptz(rotate: 180), 0, 0), point, 640, 360);
        Near(new Vector3(1 - upright.X, 1 - upright.Y, 0), new Vector3(turned, 0), 1e-4f);
    }

    [Fact]
    public void Rotate90_TurnsThePictureClockwise_SoACameraOnItsSideSeesUpright()
    {
        // A point above the axis goes to the right of a picture turned clockwise.
        var above = new Vector3(0, 8, -20);
        var turned = Project(Pose(Ptz(rotate: 90), 0, 0), above, 360, 640);
        Assert.True(turned.X > 0.6f && MathF.Abs(turned.Y - 0.5f) < 1e-4f, $"got {turned}");
        // Hung on its side and turned back: up in the world is up in the picture again.
        Assert.True(Pose(Ptz(roll: 90, rotate: 90), 0, 0).Up.Y > 0.99f);
    }

    [Fact]
    public void AReversedAxis_CountsGenericPositionsStepsAndVelocitiesTheOtherWay()
    {
        var normal = new PtzModel();
        var reversed = new PtzModel { PanReverse = true, TiltReverse = true };
        Assert.Equal(CameraPtz.PanFromGeneric(normal, -0.25f), CameraPtz.PanFromGeneric(reversed, 0.25f), 4);
        Assert.Equal(CameraPtz.TiltFromGeneric(normal, -0.6f), CameraPtz.TiltFromGeneric(reversed, 0.6f), 4);
        Assert.Equal(-CameraPtz.PanStepFromGeneric(normal, 0.1f), CameraPtz.PanStepFromGeneric(reversed, 0.1f), 4);
        Assert.Equal(-CameraPtz.TiltStepFromGeneric(normal, 0.1f), CameraPtz.TiltStepFromGeneric(reversed, 0.1f), 4);
        Assert.Equal(-0.5f, CameraPtz.PanVelocityFromGeneric(reversed, 0.5f));
        Assert.Equal(-0.5f, CameraPtz.TiltVelocityFromGeneric(reversed, 0.5f));

        var at = new PtzPosition(40, -20, 0);
        var generic = CameraPtz.ToGeneric(reversed, at).PanTilt;
        Assert.Equal(at.Pan, CameraPtz.PanFromGeneric(reversed, generic.x), 3);
        Assert.Equal(at.Tilt, CameraPtz.TiltFromGeneric(reversed, generic.y), 3);
        Assert.Equal(-CameraPtz.ToGeneric(normal, at).PanTilt.x, generic.x, 4);

        Assert.Null(CameraPtz.Configuration(new PtzModel { PanReverse = true }).Extension);
        Assert.Equal(Onvif.Wsdl.ReverseMode.ON, CameraPtz.Configuration(reversed).Extension.PTControlDirection.Reverse.Mode);
    }
}
