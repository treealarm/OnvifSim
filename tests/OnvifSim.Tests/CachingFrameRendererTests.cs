using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OnvifSim.Ptz;
using OnvifSim.Streaming;
using OnvifSim.Streaming.H264;
using OnvifSim.World;

namespace OnvifSim.Tests;

// The clock stands still unless a test moves it: the default world has someone walking in it.
public class CachingFrameRendererTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch.AddDays(20000));

    [Fact]
    public void UnchangedWorld_IsRenderedOnce_AndAnyChangeRendersAgain()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        var scene = new SceneState(store);
        var renderer = new CachingFrameRenderer(_time);
        CameraModel Cam1() => scene.Current.World.Cameras.Single(c => c.Id == "cam1");

        var first = renderer.Render(scene.Current, Cam1(), default);
        Assert.Same(first, renderer.Render(scene.Current, Cam1(), default));
        Assert.Equal(1, renderer.RenderCount);

        // A new world, even one that only changes the picture quality, is a new frame.
        store.Update(w => w.Cameras.Single(c => c.Id == "cam1").Video.Quality = 40);
        var requality = renderer.Render(scene.Current, Cam1(), default);
        Assert.NotEqual(first, requality);

        store.Update(w => w.Cameras.Single(c => c.Id == "cam1").Heading += 30);
        Assert.NotEqual(requality, renderer.Render(scene.Current, Cam1(), default));
        Assert.Equal(3, renderer.RenderCount);
    }

    [Fact]
    public void PtzPositionAndImaging_AreNewFrames()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        var scene = new SceneState(store);
        var renderer = new CachingFrameRenderer(_time);
        CameraModel Cam2() => scene.Current.World.Cameras.Single(c => c.Id == "cam2");

        var stored = renderer.Render(scene.Current, Cam2(), Cam2().Ptz!.Position);
        var turned = renderer.Render(scene.Current, Cam2(), Cam2().Ptz!.Position with { Pan = 40 });
        Assert.NotEqual(stored, turned);

        store.Update(w => w.Cameras.Single(c => c.Id == "cam2").Imaging.Brightness = 80);
        Assert.NotEqual(turned, renderer.Render(scene.Current, Cam2(), Cam2().Ptz!.Position with { Pan = 40 }));
        Assert.Equal(3, renderer.RenderCount);
    }

    [Fact]
    public void AngleGrid_IsDrawnOverThePicture_AlongTheMountDirection()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        var scene = new SceneState(store);
        var renderer = new CachingFrameRenderer(_time);
        CameraModel Cam1() => scene.Current.World.Cameras.Single(c => c.Id == "cam1");

        var plain = renderer.RenderFrame(scene.Current, Cam1(), default, 640, 360).Rgba.ToArray();
        store.Update(w => w.Cameras.Single(c => c.Id == "cam1").AngleGrid = true);
        var grid = renderer.RenderFrame(scene.Current, Cam1(), default, 640, 360).Rgba;

        // The line of pan 0 runs down the middle of a fixed camera's picture, lighter than the scene.
        static int Brightness(byte[] rgba, int x, int y) => rgba[(y * 640 + x) * 4] + rgba[(y * 640 + x) * 4 + 1] + rgba[(y * 640 + x) * 4 + 2];
        Assert.True(Brightness(grid, 320, 300) > Brightness(plain, 320, 300));
        Assert.Equal(Brightness(plain, 150, 340), Brightness(grid, 150, 340));
        Assert.Equal(2, renderer.RenderCount);
    }

    [Fact]
    public void Cameras_DoNotShareFrames()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        var scene = new SceneState(store);
        var feeds = new FeedManager(scene, new PtzSimulator(store, TimeProvider.System, NullLogger<PtzSimulator>.Instance), H264Support.Unavailable, _time, NullLoggerFactory.Instance);
        Assert.NotEqual(feeds.Snapshot("cam1"), feeds.Snapshot("cam2"));
        Assert.Same(feeds.Snapshot("cam1"), feeds.Snapshot("cam1"));
    }

    [Fact]
    public void Walkers_AreDrawnWhereTheClockPutsThem()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        store.Update(w =>
        {
            w.Walkers = [new WalkerModel { Id = "w", Route = [new(-4, 8), new(4, 8)], Speed = 2 }];
            w.Cameras.Single(c => c.Id == "cam1").Position = new Vec3(0, 1.6f, 16);
            w.Cameras.Single(c => c.Id == "cam1").Pitch = 0;
        });
        var scene = new SceneState(store);
        var renderer = new CachingFrameRenderer(_time);
        CameraModel Cam1() => scene.Current.World.Cameras.Single(c => c.Id == "cam1");

        var now = renderer.RenderFrame(scene.Current, Cam1(), default, 320, 180).Rgba.ToArray();
        Assert.Equal(now, renderer.RenderFrame(scene.Current, Cam1(), default, 320, 180).Rgba);
        Assert.Equal(1, renderer.RenderCount);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotEqual(now, renderer.RenderFrame(scene.Current, Cam1(), default, 320, 180).Rgba);
    }

    [Fact]
    public void WalkersOutOfView_DoNotMakeACameraDrawAgain()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        // cam1 looks north; the walker is behind it.
        store.Update(w => w.Walkers = [new WalkerModel { Id = "w", Route = [new(-4, 80), new(4, 80)], Speed = 2 }]);
        var scene = new SceneState(store);
        var renderer = new CachingFrameRenderer(_time);
        var cam1 = scene.Current.World.Cameras.Single(c => c.Id == "cam1");

        renderer.Render(scene.Current, cam1, default);
        _time.Advance(TimeSpan.FromSeconds(1));
        renderer.Render(scene.Current, cam1, default);
        Assert.Equal(1, renderer.RenderCount);
    }
}
