using OnvifSim.Rendering;

namespace OnvifSim.World;

// The world as the running services see it: the model plus its render-ready scene, rebuilt whenever
// the store gets a new world. Readers take one snapshot and use it for a whole frame.
public sealed class SceneState
{
    public sealed record Snapshot(WorldModel World, RenderScene Scene);

    private readonly WorldStore _store;
    private readonly TerrainMaps? _maps;
    private volatile Snapshot _current;

    // maps is what draws a picture under the terrain; a host without one (tests, a bench) renders
    // the world as it always did.
    public SceneState(WorldStore store, TerrainMaps? maps = null)
    {
        _store = store;
        _maps = maps;
        _current = Build(store.Current);
        store.Changed += w => _current = Build(w);
        // A ground map arrives after its world does: the world is worth drawing without it, and worth
        // drawing again once the picture is there.
        if (maps is not null) maps.Changed += () => _current = Build(_store.Current);
    }

    public Snapshot Current => _current;

    public CameraModel? FindCamera(string id) =>
        _current.World.Cameras.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    private Snapshot Build(WorldModel world) => new(world, RenderScene.Build(world, _maps?.Get(world)));
}
