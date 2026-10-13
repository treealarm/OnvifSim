using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnvifSim.World;

// Owns the current world: loads world.json from the data directory (or creates the default one),
// persists replacements atomically and tells subscribers that the world changed.
public sealed class WorldStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly Lock _gate = new();
    private WorldModel _world;

    public event Action<WorldModel>? Changed;

    public WorldStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "world.json");
        if (File.Exists(_path))
            _world = JsonSerializer.Deserialize<WorldModel>(File.ReadAllText(_path), Json)
                     ?? throw new InvalidDataException($"{_path} is empty.");
        else
        {
            _world = DefaultWorld.Create();
            Save(_world);
        }
    }

    public WorldModel Current
    {
        get { lock (_gate) return _world; }
    }

    public void Replace(WorldModel world)
    {
        lock (_gate)
        {
            Save(world);
            _world = world;
        }
        Changed?.Invoke(world);
    }

    private void Save(WorldModel world)
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(world, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
