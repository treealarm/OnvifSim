using OnvifSim.World;

namespace OnvifSim.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("onvifsim-test-").FullName;
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

public class WorldStoreTests
{
    [Fact]
    public void FirstStart_WritesTheDefaultWorld_AndReloadsItIdentically()
    {
        using var dir = new TempDir();
        var first = new WorldStore(dir.Path).Current;
        Assert.True(File.Exists(System.IO.Path.Combine(dir.Path, "world.json")));
        var second = new WorldStore(dir.Path).Current;
        Assert.Equal(first.Terrain.Heights, second.Terrain.Heights);
        Assert.Equal(first.Objects.Count, second.Objects.Count);
        Assert.Equal(first.Cameras.Select(c => c.Id), second.Cameras.Select(c => c.Id));
        Assert.Equal(CameraKind.Ptz, second.Cameras.Single(c => c.Id == "cam2").Kind);
    }

    [Fact]
    public void Replace_PersistsAndNotifies()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        WorldModel? seen = null;
        store.Changed += w => seen = w;
        var world = store.Current;
        world.Cameras.RemoveAll(c => c.Id == "cam1");
        store.Replace(world);
        Assert.Same(world, seen);
        Assert.DoesNotContain(new WorldStore(dir.Path).Current.Cameras, c => c.Id == "cam1");
    }

    [Fact]
    public void Heights_AcceptPlainArraysToo()
    {
        var json = """{"terrain":{"size":10,"resolution":1,"heights":[0,1,2,3]}}""";
        var world = System.Text.Json.JsonSerializer.Deserialize<WorldModel>(json, WorldStore.Json)!;
        Assert.Equal([0f, 1, 2, 3], world.Terrain.Heights);
        Assert.Equal(1.5f, new Terrain(world.Terrain).HeightAt(0, 0), 3);
    }
}
