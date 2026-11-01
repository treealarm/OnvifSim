using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnvifSim.World;

// Owns the active world and the worlds kept beside it in the data directory: worlds/<name>.json, the
// trash/ folder and active-world.json, which says which one a restart opens. Replacing the active world,
// switching to another one and editing it all go through here, so every service sees one order of events.
public sealed class WorldStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _worldsDirectory;
    private readonly string _trashDirectory;
    private readonly string _activePath;
    // Supplied by the host: activation validates a world exactly as the editor API does, while the world
    // layer stays free of the editor and its options. Tests may run without one.
    private readonly Action<WorldModel>? _validate;
    private readonly Lock _gate = new();

    private WorldModel _world;
    private string _activeName;
    private WorldSwitch? _lastSwitch;
    private long _switchVersion;

    public event Action<WorldModel>? Changed;
    // A switch, after the store already points at the new world and before Changed tells the rest.
    // Runtime state that belongs to the world being left (head motion, SD cards, offline cameras) resets
    // here.
    public event Action<WorldSwitch>? Switched;

    public WorldStore(string dataDirectory, Action<WorldModel>? validate = null)
    {
        _validate = validate;
        Directory.CreateDirectory(dataDirectory);
        _worldsDirectory = Path.Combine(dataDirectory, "worlds");
        _trashDirectory = Path.Combine(dataDirectory, "trash");
        _activePath = Path.Combine(dataDirectory, "active-world.json");
        Directory.CreateDirectory(_worldsDirectory);
        Directory.CreateDirectory(_trashDirectory);
        MigrateLegacyWorld(dataDirectory, _worldsDirectory);

        var startup = ResolveStartupWorld();
        _activeName = startup.Name;
        _world = startup.World;
    }

    public WorldModel Current
    {
        get { lock (_gate) return _world; }
    }

    public string ActiveName
    {
        get { lock (_gate) return _activeName; }
    }

    public WorldSwitch? LastSwitch
    {
        get { lock (_gate) return _lastSwitch; }
    }

    // One read of everything the event stream needs, so a switch cannot be seen half-done.
    public (WorldModel World, string ActiveName, WorldSwitch? LastSwitch) Snapshot()
    {
        lock (_gate) return (_world, _activeName, _lastSwitch);
    }

    public WorldListing Listing()
    {
        lock (_gate) return new WorldListing(_activeName, ListWorldsLocked(), ListTrashLocked());
    }

    public bool Exists(string name)
    {
        var normalized = WorldNames.Normalize(name);
        lock (_gate) return FindWorldLocked(normalized) != null;
    }

    // Writes an empty world; it does not become active, so a create that is later cancelled leaves a
    // world behind in the list rather than nothing.
    public void Create(string name)
    {
        var normalized = WorldNames.Normalize(name);
        lock (_gate)
        {
            if (FindWorldLocked(normalized) != null)
                throw new WorldAlreadyExistsException($"A world named '{normalized}' already exists.");
            var world = DefaultWorld.CreateEmpty();
            _validate?.Invoke(world);
            SaveWorld(normalized, world);
        }
    }

    // Makes another world current. The world is read and validated first; a broken file changes nothing.
    public void Activate(string name, bool clearRecordings)
    {
        var normalized = WorldNames.Normalize(name);
        lock (_gate) ActivateLocked(normalized, clearRecordings);
    }

    // Moves a world out of the active list. Trashing the active one opens the next usable world, or a
    // fresh empty default when none is left.
    public void Trash(string name, bool clearRecordings)
    {
        var normalized = WorldNames.Normalize(name);
        lock (_gate)
        {
            var actual = FindWorldLocked(normalized)
                         ?? throw new WorldNotFoundException($"No world named '{normalized}'.");
            bool wasActive = WorldNames.Same(actual, _activeName);
            MoveToTrashLocked(actual);
            if (!wasActive) return;
            ActivateNextWorldLocked(actual, clearRecordings);
        }
    }

    // Puts a trashed world back in the list, optionally under another name. It does not open it.
    public string Restore(string trashName, string? destinationName = null)
    {
        var source = WorldNames.Normalize(trashName);
        var destination = destinationName == null ? source : WorldNames.Normalize(destinationName);
        lock (_gate)
        {
            var trashed = FindTrashLocked(source)
                          ?? throw new WorldNotFoundException($"No trashed world named '{source}'.");
            if (FindWorldLocked(destination) != null)
                throw new WorldAlreadyExistsException($"A world named '{destination}' already exists.");
            File.Move(TrashPath(trashed), WorldPath(destination));
            return destination;
        }
    }

    public int EmptyTrash()
    {
        lock (_gate)
        {
            int removed = 0;
            foreach (var file in Directory.EnumerateFiles(_trashDirectory))
            {
                File.Delete(file);
                removed++;
            }
            return removed;
        }
    }

    // Subscribers are told inside the lock, so they see changes in the order they were made.
    public void Replace(WorldModel world)
    {
        lock (_gate)
        {
            SaveWorld(_activeName, world);
            _world = world;
            Changed?.Invoke(world);
        }
    }

    // Applies a change to a copy of the current world and makes the copy current, atomically, so two
    // writers (ONVIF clients, the editor) never lose each other's edits. The world in hand stays
    // untouched for everyone still reading it.
    public void Update(Action<WorldModel> change)
    {
        lock (_gate)
        {
            var copy = JsonSerializer.Deserialize<WorldModel>(JsonSerializer.Serialize(_world, Json), Json)!;
            change(copy);
            Replace(copy);
        }
    }

    private void ActivateLocked(string name, bool clearRecordings)
    {
        var actual = FindWorldLocked(name) ?? throw new WorldNotFoundException($"No world named '{name}'.");
        if (WorldNames.Same(actual, _activeName)) return;
        var world = LoadWorld(actual);
        _validate?.Invoke(world);
        ActivateWorldLocked(actual, world, clearRecordings);
    }

    private void ActivateWorldLocked(string name, WorldModel world, bool clearRecordings)
    {
        WriteActiveMarker(name);
        var previous = _activeName;
        _world = world;
        _activeName = name;
        var switched = new WorldSwitch(world, previous, name, clearRecordings, ++_switchVersion);
        _lastSwitch = switched;
        Switched?.Invoke(switched);
        Changed?.Invoke(world);
    }

    // Finds a world that can actually be opened: a corrupt or invalid file must not block the switch
    // that trashed the active world. The last resort is a fresh empty default.
    private void ActivateNextWorldLocked(string trashedName, bool clearRecordings)
    {
        var names = ListWorldsLocked();
        var ordered = names
            .Where(n => StringComparer.OrdinalIgnoreCase.Compare(n, trashedName) > 0)
            .Concat(names.Where(n => StringComparer.OrdinalIgnoreCase.Compare(n, trashedName) <= 0));
        foreach (var candidate in ordered)
        {
            WorldModel world;
            try
            {
                world = LoadWorld(candidate);
                _validate?.Invoke(world);
            }
            catch (Exception ex) when (ex is InvalidWorldException or InvalidDataException or IOException or JsonException)
            {
                // Leave the file where it is: the user can repair or trash it. Try the next world.
                continue;
            }
            ActivateWorldLocked(candidate, world, clearRecordings);
            return;
        }

        if (FindWorldLocked("default") is { } broken)
        {
            MoveToTrashLocked(broken);
        }
        var empty = DefaultWorld.CreateEmpty();
        _validate?.Invoke(empty);
        SaveWorld("default", empty);
        ActivateWorldLocked("default", empty, clearRecordings);
    }

    private void MoveToTrashLocked(string name)
    {
        var target = WorldNames.Unique(name, TrashExistsLocked);
        File.Move(WorldPath(name), TrashPath(target));
    }

    private StartupWorld ResolveStartupWorld()
    {
        var names = ListWorldsLocked();
        if (names.Count == 0)
        {
            var world = DefaultWorld.Create();
            _validate?.Invoke(world);
            SaveWorld("default", world);
            WriteActiveMarker("default");
            return new StartupWorld("default", world);
        }

        var saved = ReadActiveMarker();
        var chosen = (saved == null ? null : FindWorldLocked(saved))
                     ?? FindWorldLocked("default")
                     ?? names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).First();
        if (saved == null || !WorldNames.Same(chosen, saved)) WriteActiveMarker(chosen);
        return new StartupWorld(chosen, LoadWorld(chosen));
    }

    private WorldModel LoadWorld(string name)
    {
        var path = WorldPath(name);
        try
        {
            var world = JsonSerializer.Deserialize<WorldModel>(File.ReadAllText(path), Json);
            return world ?? throw new InvalidDataException($"{path} is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} cannot be read: {ex.Message}", ex);
        }
    }

    private void SaveWorld(string name, WorldModel world) =>
        WriteAtomic(WorldPath(name), JsonSerializer.Serialize(world, Json));

    private void WriteActiveMarker(string name) =>
        WriteAtomic(_activePath, JsonSerializer.Serialize(new ActiveWorld(1, name), Json));

    private string? ReadActiveMarker()
    {
        try
        {
            return JsonSerializer.Deserialize<ActiveWorld>(File.ReadAllText(_activePath), Json)?.Name;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A missing or unreadable marker is resolved from the worlds on disk.
            return null;
        }
    }

    private List<string> ListWorldsLocked() => ListStems(_worldsDirectory);

    private List<string> ListTrashLocked() => ListStems(_trashDirectory);

    private static List<string> ListStems(string directory) =>
        Directory.EnumerateFiles(directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private string? FindWorldLocked(string name) =>
        ListWorldsLocked().FirstOrDefault(n => WorldNames.Same(n, name));

    private string? FindTrashLocked(string name) =>
        ListTrashLocked().FirstOrDefault(n => WorldNames.Same(n, name));

    private bool TrashExistsLocked(string name) => FindTrashLocked(name) != null;

    private string WorldPath(string name) => Path.Combine(_worldsDirectory, name + ".json");

    private string TrashPath(string name) => Path.Combine(_trashDirectory, name + ".json");

    private static void WriteAtomic(string path, string contents)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }

    // Older versions kept one world.json. It becomes worlds/default.json on the first start; a world
    // already named default wins and the legacy file is left untouched.
    private static void MigrateLegacyWorld(string dataDirectory, string worldsDirectory)
    {
        var legacy = Path.Combine(dataDirectory, "world.json");
        var target = Path.Combine(worldsDirectory, "default.json");
        if (!File.Exists(legacy) || File.Exists(target)) return;
        File.Move(legacy, target);
    }

    private sealed record ActiveWorld(int Version, string? Name);

    private sealed record StartupWorld(string Name, WorldModel World);
}

// What the world library holds now.
public sealed record WorldListing(string Active, IReadOnlyList<string> Worlds, IReadOnlyList<string> Trash);

// A switch, with what the runtime services need to reset or keep. Version grows with every switch, so
// the event stream can tell a switch from an ordinary edit.
public sealed record WorldSwitch(WorldModel World, string Previous, string Current, bool ClearRecordings, long Version);
