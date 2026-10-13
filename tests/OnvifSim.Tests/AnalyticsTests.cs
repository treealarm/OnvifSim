using System.Xml.Linq;
using Microsoft.Extensions.Time.Testing;
using OnvifSim.Analytics;
using OnvifSim.Onvif;
using OnvifSim.Ptz;
using OnvifSim.Streaming;
using OnvifSim.World;

namespace OnvifSim.Tests;

// The default world's cam1 stands at (0, 6, 60) looking north (-Z), slightly down. Each test puts one
// person standing (a route of one point) somewhere in front of it.
public class AnalyticsTests
{
    private static readonly XNamespace Tt = "http://www.onvif.org/ver10/schema";
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch.AddDays(20000));

    private static SceneState WorldWithOnePerson(TempDir dir, float x, float z, Action<WorldModel>? change = null)
    {
        var store = new WorldStore(dir.Path);
        store.Update(w =>
        {
            w.Walkers = [new WalkerModel { Id = "p", Route = [new GroundPoint(x, z)] }];
            change?.Invoke(w);
        });
        return new SceneState(store);
    }

    private static CameraModel Cam1(SceneState scene) => scene.Current.World.Cameras.Single(c => c.Id == "cam1");

    [Fact]
    public void Box_IsWhereThePictureDrawsThePerson()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        store.Update(w => (w.Objects, w.Walkers) = ([], [new WalkerModel { Id = "p", Route = [new GroundPoint(3, 40)] }]));
        var scene = new SceneState(store);
        var withPerson = scene.Current;
        store.Update(w => w.Walkers = []);
        var camera = Cam1(scene);
        var (width, height) = (camera.Video.Width, camera.Video.Height);

        var renderer = new CachingFrameRenderer(_time);
        var with = renderer.RenderFrame(withPerson, camera, default, width, height).Rgba.ToArray();
        var without = renderer.RenderFrame(scene.Current, camera, default, width, height).Rgba;

        // The pixels the person changed, as a pixel rectangle.
        int left = width, right = -1, top = height, bottom = -1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                if (with[i] == without[i] && with[i + 1] == without[i + 1] && with[i + 2] == without[i + 2]) continue;
                (left, right, top, bottom) = (Math.Min(left, x), Math.Max(right, x), Math.Min(top, y), Math.Max(bottom, y));
            }
        Assert.True(right > left, "the person is in the picture");

        var box = Assert.Single(PersonDetector.Detect(withPerson, camera, default, _time.GetUtcNow())).Box;
        // Normalized to pixel edges; the drawn pixels lie inside the box, within a pixel of its edges.
        float boxLeft = (box.Left + 1) / 2 * width, boxRight = (box.Right + 1) / 2 * width;
        float boxTop = (1 - box.Top) / 2 * height, boxBottom = (1 - box.Bottom) / 2 * height;
        Assert.InRange(left, boxLeft - 1, boxLeft + 1);
        Assert.InRange(right + 1, boxRight - 1, boxRight + 1);
        Assert.InRange(top, boxTop - 1, boxTop + 1);
        Assert.InRange(bottom + 1, boxBottom - 1, boxBottom + 1);
    }

    [Fact]
    public void PersonBehindAHouse_IsNotSeen_UntilTheyStepAside()
    {
        using var dir = new TempDir();
        static WorldObject House(float x) => new() { Id = "h", Type = "house", Position = new Vec3(x, 0, 48), Scale = new Vec3(8, 6, 6) };

        var hidden = WorldWithOnePerson(dir, 0, 35, w => w.Objects = [House(0)]);
        Assert.Empty(PersonDetector.Detect(hidden.Current, Cam1(hidden), default, _time.GetUtcNow()));

        var aside = WorldWithOnePerson(dir, 0, 35, w => w.Objects = [House(20)]);
        Assert.Single(PersonDetector.Detect(aside.Current, Cam1(aside), default, _time.GetUtcNow()));
    }

    [Theory]
    [InlineData(0, -150)] // far away: a few pixels tall
    [InlineData(0, 80)]   // behind the camera
    [InlineData(-90, 40)] // off to the side, outside the field of view
    public void PersonTooSmallOrOutOfView_IsNotSeen(float x, float z)
    {
        using var dir = new TempDir();
        var scene = WorldWithOnePerson(dir, x, z, w => w.Objects = []);
        Assert.Empty(PersonDetector.Detect(scene.Current, Cam1(scene), default, _time.GetUtcNow()));
    }

    [Fact]
    public void Tracker_KeepsAnIdWhileInView_AndGivesANewOneAfterLeaving()
    {
        var tracker = new ObjectTracker();
        var box = new FrameBox(-0.1f, 0.2f, 0.1f, -0.2f);
        Sighting Seen(string walker) => new(walker, box);

        var first = tracker.Update([Seen("a"), Seen("b")]);
        Assert.Equal([1, 2], first.Select(o => o.ObjectId));
        Assert.Equal([1, 2], tracker.Update([Seen("b"), Seen("a")]).OrderBy(o => o.WalkerId).Select(o => o.ObjectId));
        Assert.Equal([2], tracker.Update([Seen("b")]).Select(o => o.ObjectId));
        Assert.Equal([3, 2], tracker.Update([Seen("a"), Seen("b")]).Select(o => o.ObjectId));
    }

    [Fact]
    public void Document_DescribesEachPersonAsAHumanWithABox_AndThePtzHeadWhenAskedTo()
    {
        var camera = new CameraModel
        {
            Id = "c", Kind = CameraKind.Ptz,
            Ptz = new PtzModel(),
            Metadata = new MetadataModel { Analytics = true, PtzStatus = true },
        };
        var time = new DateTimeOffset(2026, 10, 13, 12, 0, 0, 250, TimeSpan.Zero);
        var frame = new AnalyticsFrame(time, camera,
            [new TrackedObject(7, "w", new FrameBox(-0.5f, 0.25f, -0.25f, -0.75f))],
            new PtzState(new PtzPosition(0, 0, 0.5f), PanTiltMoving: true, ZoomMoving: false));

        var root = XDocument.Parse(System.Text.Encoding.UTF8.GetString(MetadataDocument.Write(frame))).Root!;
        Assert.Equal(Tt + "MetadataStream", root.Name);
        var xmlFrame = root.Element(Tt + "VideoAnalytics")!.Element(Tt + "Frame")!;
        Assert.Equal("2026-10-13T12:00:00.250Z", (string?)xmlFrame.Attribute("UtcTime"));
        var obj = Assert.Single(xmlFrame.Elements(Tt + "Object"));
        Assert.Equal("7", (string?)obj.Attribute("ObjectId"));
        var shape = obj.Element(Tt + "Appearance")!.Element(Tt + "Shape")!;
        var bounds = shape.Element(Tt + "BoundingBox")!;
        Assert.Equal(["-0.5", "0.25", "-0.25", "-0.75"], new[] { "left", "top", "right", "bottom" }.Select(a => (string)bounds.Attribute(a)!));
        Assert.Equal("-0.375", (string?)shape.Element(Tt + "CenterOfGravity")!.Attribute("x"));
        var objectClass = obj.Element(Tt + "Appearance")!.Element(Tt + "Class")!;
        Assert.Equal("Human", (string?)objectClass.Element(Tt + "ClassCandidate")!.Element(Tt + "Type"));
        Assert.Equal("Human", (string?)objectClass.Element(Tt + "Type"));

        var status = root.Element(Tt + "PTZ")!.Element(Tt + "PTZStatus")!;
        Assert.Equal("MOVING", (string?)status.Element(Tt + "MoveStatus")!.Element(Tt + "PanTilt"));
        Assert.Equal("0.5", (string?)status.Element(Tt + "Position")!.Element(Tt + "Zoom")!.Attribute("x"));

        camera.Metadata = new MetadataModel { Analytics = false, PtzStatus = false };
        Assert.Empty(XDocument.Parse(System.Text.Encoding.UTF8.GetString(MetadataDocument.Write(frame))).Root!.Elements());
    }
}
