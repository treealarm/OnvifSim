using OnvifSim.Streaming;
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

        var first = renderer.Render(scene.Current, Cam1());
        Assert.Same(first, renderer.Render(scene.Current, Cam1()));
        Assert.Equal(1, renderer.RenderCount);

        // A new world, even one that only changes the picture quality, is a new frame.
        store.Update(w => w.Cameras.Single(c => c.Id == "cam1").Video.Quality = 40);
        var requality = renderer.Render(scene.Current, Cam1());
        Assert.NotEqual(first, requality);

        store.Update(w => w.Cameras.Single(c => c.Id == "cam1").Heading += 30);
        Assert.NotEqual(requality, renderer.Render(scene.Current, Cam1()));
        Assert.Equal(3, renderer.RenderCount);
    }

    [Fact]
    public void Cameras_DoNotShareFrames()
    {
        using var dir = new TempDir();
        var scene = new SceneState(new WorldStore(dir.Path));
        var feeds = new FeedManager(scene, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        Assert.NotEqual(feeds.Snapshot("cam1"), feeds.Snapshot("cam2"));
        Assert.Same(feeds.Snapshot("cam1"), feeds.Snapshot("cam1"));
    }
}
