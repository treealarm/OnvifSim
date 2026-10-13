using Microsoft.Extensions.Logging.Abstractions;
using OnvifSim.Ptz;
using OnvifSim.Streaming;
using OnvifSim.Streaming.H264;
using OnvifSim.World;

namespace OnvifSim.Tests;

public class CachingFrameRendererTests
{
    [Fact]
    public void UnchangedWorld_IsRenderedOnce_AndAnyChangeRendersAgain()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        var scene = new SceneState(store);
        var renderer = new CachingFrameRenderer();
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
        var renderer = new CachingFrameRenderer();
        CameraModel Cam2() => scene.Current.World.Cameras.Single(c => c.Id == "cam2");

        var stored = renderer.Render(scene.Current, Cam2(), Cam2().Ptz!.Position);
        var turned = renderer.Render(scene.Current, Cam2(), Cam2().Ptz!.Position with { Pan = 40 });
        Assert.NotEqual(stored, turned);

        store.Update(w => w.Cameras.Single(c => c.Id == "cam2").Imaging.Brightness = 80);
        Assert.NotEqual(turned, renderer.Render(scene.Current, Cam2(), Cam2().Ptz!.Position with { Pan = 40 }));
        Assert.Equal(3, renderer.RenderCount);
    }

    [Fact]
    public void Cameras_DoNotShareFrames()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        var scene = new SceneState(store);
        var feeds = new FeedManager(scene, new PtzSimulator(store, TimeProvider.System, NullLogger<PtzSimulator>.Instance), H264Support.Unavailable, NullLoggerFactory.Instance);
        Assert.NotEqual(feeds.Snapshot("cam1"), feeds.Snapshot("cam2"));
        Assert.Same(feeds.Snapshot("cam1"), feeds.Snapshot("cam1"));
    }
}
