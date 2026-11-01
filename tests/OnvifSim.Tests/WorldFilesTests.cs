using System.Text.Json;
using OnvifSim.World;

namespace OnvifSim.Tests;

public class WorldFilesTests
{
    [Fact]
    public void FreshStart_WritesTheVillageAndTheActiveMarker()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);

        Assert.Equal("default", store.ActiveName);
        Assert.Equal(["default"], store.Listing().Worlds);
        Assert.NotEmpty(store.Current.Cameras);
        Assert.True(File.Exists(Path.Combine(dir.Path, "worlds", "default.json")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "active-world.json")));
    }

    [Fact]
    public void LegacyWorldJson_IsMovedIntoTheWorldsDirectory()
    {
        using var dir = new TempDir();
        var legacy = Path.Combine(dir.Path, "world.json");
        File.WriteAllText(legacy, JsonSerializer.Serialize(new WorldModel(), WorldStore.Json));

        var store = new WorldStore(dir.Path);

        Assert.Empty(store.Current.Cameras);
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(Path.Combine(dir.Path, "worlds", "default.json")));
        Assert.Equal("default", store.ActiveName);
    }

    [Fact]
    public void AWorldNamedDefault_WinsOverTheLegacyFile()
    {
        using var dir = new TempDir();
        var worlds = Path.Combine(dir.Path, "worlds");
        Directory.CreateDirectory(worlds);
        File.WriteAllText(Path.Combine(worlds, "default.json"), JsonSerializer.Serialize(new WorldModel(), WorldStore.Json));
        var legacy = Path.Combine(dir.Path, "world.json");
        File.WriteAllText(legacy, JsonSerializer.Serialize(DefaultWorld.Create(), WorldStore.Json));

        var store = new WorldStore(dir.Path);

        Assert.Empty(store.Current.Cameras);
        Assert.True(File.Exists(legacy), "the legacy file is left where it is once worlds/ exists");
    }

    [Fact]
    public void ActiveName_PersistsAcrossStoreInstances()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        store.Create("Village");
        store.Activate("Village", clearRecordings: false);

        var second = new WorldStore(dir.Path);

        Assert.Equal("Village", second.ActiveName);
        Assert.Equal(store.Current.Cameras.Select(c => c.Id), second.Current.Cameras.Select(c => c.Id));
    }

    [Fact]
    public void Create_AddsAnEmptyWorldWithoutOpeningIt()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);

        store.Create("Empty");

        Assert.Equal("default", store.ActiveName);
        Assert.Contains("Empty", store.Listing().Worlds);
        store.Activate("Empty", clearRecordings: false);
        Assert.Empty(store.Current.Cameras);
        Assert.Empty(store.Current.Objects);
        Assert.Empty(store.Current.Terrain.Heights);
    }

    [Fact]
    public void Create_RefusesANameUsedInAnotherCase()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        store.Create("Village");

        Assert.Throws<WorldAlreadyExistsException>(() => store.Create("village"));
    }

    [Fact]
    public void Create_AcceptsCyrillicAndSpaces()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);

        store.Create("Мой мир");
        store.Activate("Мой мир", clearRecordings: false);

        Assert.Equal("Мой мир", store.ActiveName);
        Assert.Contains("Мой мир", new WorldStore(dir.Path).Listing().Worlds);
    }

    [Fact]
    public void Create_RejectsNamesThatCannotBeFiles()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        string[] bad =
        [
            "", "   ", "a/b", "a\\b", "a:b", "a*b", "a?b", "a\"b", "a<b", "a>b", "a|b",
            ".hidden", "trailing.", "CON", "com1.txt", "a\u0001b", new string('a', WorldNames.MaxLength + 1),
        ];
        foreach (var name in bad)
            Assert.Throws<InvalidWorldNameException>(() => store.Create(name));
        Assert.Throws<InvalidWorldNameException>(() => store.Create(null!));
    }

    [Fact]
    public void TrashActive_OpensTheNextWorld()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        store.Create("A");
        store.Create("B");
        store.Activate("A", clearRecordings: false);

        store.Trash("A", clearRecordings: false);

        Assert.Equal("B", store.ActiveName);
        Assert.DoesNotContain("A", store.Listing().Worlds);
        Assert.Contains("A", store.Listing().Trash);
    }

    [Fact]
    public void TrashLastWorld_CreatesAFreshEmptyDefault()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        store.Create("A");
        store.Activate("A", clearRecordings: false);
        store.Trash("default", clearRecordings: false);

        store.Trash("A", clearRecordings: false);

        Assert.Equal("default", store.ActiveName);
        Assert.Empty(store.Current.Cameras);
        Assert.Contains("A", store.Listing().Trash);
        Assert.Contains("default", store.Listing().Trash);
    }

    [Fact]
    public void Trash_KeepsEveryCopyOfARepeatedName()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        store.Create("Old");
        store.Trash("Old", clearRecordings: false);
        store.Create("Old");

        store.Trash("Old", clearRecordings: false);

        Assert.Contains("Old", store.Listing().Trash);
        Assert.Contains("Old (2)", store.Listing().Trash);
    }

    [Fact]
    public void Trash_SuffixedCopiesStillFitTheNameLimit()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        var name = new string('a', WorldNames.MaxLength);
        store.Create(name);
        store.Trash(name, clearRecordings: false);
        store.Create(name);

        store.Trash(name, clearRecordings: false);

        Assert.All(store.Listing().Trash, trashed => Assert.True(trashed.Length <= WorldNames.MaxLength));
    }

    [Fact]
    public void Restore_ReturnsTheWorldAndNeverOverwritesAnother()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        store.Create("Saved");
        store.Trash("Saved", clearRecordings: false);
        store.Create("Saved");

        Assert.Throws<WorldAlreadyExistsException>(() => store.Restore("Saved"));

        store.Trash("Saved", clearRecordings: false);
        Assert.Equal("Saved", store.Restore("Saved"));
        Assert.Contains("Saved", store.Listing().Worlds);
    }

    [Fact]
    public void EmptyTrash_DeletesEveryFile()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        store.Create("A");
        store.Create("B");
        store.Trash("A", clearRecordings: false);
        store.Trash("B", clearRecordings: false);

        Assert.Equal(2, store.EmptyTrash());
        Assert.Empty(store.Listing().Trash);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir.Path, "trash")));
    }

    [Fact]
    public void Switched_FiresBeforeChanged_WithTheSwitchDetails()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        var order = new List<string>();
        WorldSwitch? switched = null;
        store.Switched += info =>
        {
            order.Add("switched");
            switched = info;
        };
        store.Changed += _ => order.Add("changed");
        store.Create("Village");

        store.Activate("Village", clearRecordings: true);

        Assert.Equal(["switched", "changed"], order);
        Assert.NotNull(switched);
        Assert.Equal("default", switched.Previous);
        Assert.Equal("Village", switched.Current);
        Assert.True(switched.ClearRecordings);
        Assert.Equal(switched.Version, store.LastSwitch!.Version);
    }

    [Fact]
    public void Activate_RefusesAnInvalidWorldAndKeepsTheCurrentOne()
    {
        using var dir = new TempDir();
        new WorldStore(dir.Path).Create("Empty");
        var strict = new WorldStore(dir.Path, world =>
        {
            if (world.Cameras.Count == 0) throw new InvalidWorldException("No cameras.");
        });

        Assert.Throws<InvalidWorldException>(() => strict.Activate("Empty", clearRecordings: false));

        Assert.Equal("default", strict.ActiveName);
        Assert.NotEmpty(strict.Current.Cameras);
    }

    [Fact]
    public void CorruptActiveWorld_FailsWithItsFileName()
    {
        using var dir = new TempDir();
        _ = new WorldStore(dir.Path);
        File.WriteAllText(Path.Combine(dir.Path, "worlds", "default.json"), "{ not json");

        var error = Assert.Throws<InvalidDataException>(() => new WorldStore(dir.Path));

        Assert.Contains("default.json", error.Message);
    }

    [Fact]
    public void AStaleActiveMarker_FallsBackToDefaultAndIsRewritten()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        store.Create("Other");
        store.Activate("Other", clearRecordings: false);
        File.WriteAllText(Path.Combine(dir.Path, "active-world.json"), """{"version":1,"name":"ghost"}""");

        var second = new WorldStore(dir.Path);

        Assert.Equal("default", second.ActiveName);
        Assert.Contains("default", File.ReadAllText(Path.Combine(dir.Path, "active-world.json")));
    }

    [Fact]
    public void Updates_SaveToTheActiveWorldOnly()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        store.Create("Other");
        store.Activate("Other", clearRecordings: false);
        var village = File.ReadAllText(Path.Combine(dir.Path, "worlds", "default.json"));

        store.Update(world => world.Objects.Add(new WorldObject
        {
            Id = "saved-here", Type = "rock", Scale = new(1, 1, 1),
        }));

        Assert.Contains("saved-here", File.ReadAllText(Path.Combine(dir.Path, "worlds", "Other.json")));
        Assert.Equal(village, File.ReadAllText(Path.Combine(dir.Path, "worlds", "default.json")));
    }
}
