using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OnvifSim.World;

namespace OnvifSim.Tests;

// The world library over HTTP: one simulator, its data directory holding several worlds.
public class WorldLibraryTests(SimFactory factory) : IClassFixture<SimFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => ResetAsync();

    public Task DisposeAsync() => ResetAsync();

    [Fact]
    public async Task TheLibrary_ListsTheActiveWorldAndItsRecordings()
    {
        var info = await Worlds();

        Assert.Equal("default", info.Active);
        Assert.Contains("default", info.Worlds);
        Assert.True(info.Recordings.HasRecordings);
        Assert.Equal(2, info.Recordings.CameraCount);
    }

    [Fact]
    public async Task CreatingAWorld_AsksAboutRecordings_ThenOpensItEmpty()
    {
        var name = $"empty {Guid.NewGuid():N}";

        var ask = await _client.PostAsJsonAsync("/api/worlds", new { name });
        Assert.Equal(HttpStatusCode.Conflict, ask.StatusCode);
        var problem = await ask.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("recordings", problem.GetProperty("code").GetString());
        Assert.DoesNotContain(name, (await Worlds()).Worlds);

        var created = await _client.PostAsJsonAsync("/api/worlds", new { name, clearRecordings = true });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var info = (await created.Content.ReadFromJsonAsync<WorldsInfo>())!;
        Assert.Equal(name, info.Active);

        var world = (await _client.GetFromJsonAsync<WorldModel>("/api/world", WorldStore.Json))!;
        Assert.Empty(world.Cameras);
        Assert.Empty(world.Objects);
        Assert.Empty(world.Terrain.Heights);
    }

    [Fact]
    public async Task SwitchingBack_OpensTheVillageAndKeepsTheLibrary()
    {
        var name = $"back {Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/api/worlds", new { name, clearRecordings = true });

        var switched = await _client.PostAsJsonAsync("/api/worlds/default/activate", new { clearRecordings = false });
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);

        var world = (await _client.GetFromJsonAsync<WorldModel>("/api/world", WorldStore.Json))!;
        Assert.Contains(world.Cameras, c => c.Id == "cam1");
        Assert.Contains(name, (await Worlds()).Worlds);
    }

    [Fact]
    public async Task NamesWithSpacesAndCyrillic_RoundTripThroughTheUrls()
    {
        const string name = "Мой мир";
        await _client.PostAsJsonAsync("/api/worlds", new { name, clearRecordings = true });
        var escaped = Uri.EscapeDataString(name);

        var trashed = await _client.PostAsJsonAsync($"/api/worlds/{escaped}/trash", new { clearRecordings = false });
        Assert.Equal(HttpStatusCode.OK, trashed.StatusCode);
        var afterTrash = await Worlds();
        Assert.Equal("default", afterTrash.Active);
        Assert.Contains(name, afterTrash.Trash);

        var restored = await _client.PostAsJsonAsync($"/api/trash/{escaped}/restore", new { });
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Contains(name, (await Worlds()).Worlds);
    }

    [Fact]
    public async Task TrashingTheActiveWorld_OpensAnotherOne()
    {
        var first = $"A {Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/api/worlds", new { name = first, clearRecordings = true });
        // The outgoing world is empty, so no recordings question is needed here.
        var second = $"B {Guid.NewGuid():N}";
        var created = await _client.PostAsJsonAsync("/api/worlds", new { name = second });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var trashed = await _client.PostAsJsonAsync($"/api/worlds/{Uri.EscapeDataString(second)}/trash", new { clearRecordings = false });
        Assert.Equal(HttpStatusCode.OK, trashed.StatusCode);
        var info = await Worlds();
        Assert.Equal("default", info.Active);
        Assert.Contains(second, info.Trash);
    }

    [Fact]
    public async Task AWorldCanBeRestoredAndTheTrashEmptied()
    {
        var name = $"trash {Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/api/worlds", new { name, clearRecordings = true });
        await _client.PostAsJsonAsync("/api/worlds/default/activate", new { clearRecordings = false });
        var escaped = Uri.EscapeDataString(name);

        await _client.PostAsJsonAsync($"/api/worlds/{escaped}/trash", new { clearRecordings = false });
        Assert.Contains(name, (await Worlds()).Trash);

        await _client.PostAsJsonAsync($"/api/trash/{escaped}/restore", new { });
        Assert.Contains(name, (await Worlds()).Worlds);

        await _client.PostAsJsonAsync($"/api/worlds/{escaped}/trash", new { clearRecordings = false });
        await _client.DeleteAsync("/api/trash");
        Assert.Empty((await Worlds()).Trash);
    }

    [Fact]
    public async Task SwitchingBringsBackACameraThatWasOffline()
    {
        var offline = await _client.PutAsJsonAsync("/api/cameras/cam1/network", new { online = false });
        Assert.Equal(HttpStatusCode.NoContent, offline.StatusCode);
        Assert.False((await RecordingState("cam1")).GetProperty("online").GetBoolean());

        var name = $"network {Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/api/worlds", new { name, clearRecordings = true });
        await _client.PostAsJsonAsync("/api/worlds/default/activate", new { clearRecordings = false });

        Assert.True((await RecordingState("cam1")).GetProperty("online").GetBoolean());
    }

    [Fact]
    public async Task SwitchingWorlds_SendsWorldSwitchedBeforeTheNewWorld()
    {
        var name = $"sse {Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await _client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        var switching = Task.Run(async () =>
        {
            await Task.Delay(300, cts.Token);
            await _client.PostAsJsonAsync("/api/worlds", new { name, clearRecordings = true }, cts.Token);
        });

        var seen = new List<string>();
        JsonElement? switched = null;
        string? eventName = null;
        bool worldAfterSwitch = false;
        while (!cts.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cts.Token);
            if (line == null) break;
            if (line.StartsWith("event: ")) eventName = line["event: ".Length..];
            else if (line.StartsWith("data: ") && eventName == "world-switched")
            {
                seen.Add(eventName);
                switched = JsonDocument.Parse(line["data: ".Length..]).RootElement;
            }
            else if (line.StartsWith("data: ") && eventName == "world" && switched != null)
            {
                seen.Add(eventName);
                worldAfterSwitch = true;
                break;
            }
        }
        await switching;

        Assert.True(worldAfterSwitch, "the new world follows the switch");
        Assert.Equal(["world-switched", "world"], seen);
        Assert.Equal(name, switched!.Value.GetProperty("active").GetString());
        Assert.Equal("default", switched.Value.GetProperty("previous").GetString());
    }

    private async Task<WorldsInfo> Worlds() =>
        (await _client.GetFromJsonAsync<WorldsInfo>("/api/worlds"))!;

    private async Task<JsonElement> RecordingState(string cameraId) =>
        await _client.GetFromJsonAsync<JsonElement>($"/api/cameras/{cameraId}/recording");

    // Puts the simulator back on the village and removes worlds a test left behind.
    private async Task ResetAsync()
    {
        await _client.PostAsJsonAsync("/api/worlds/default/activate", new { clearRecordings = false });
        var info = await Worlds();
        foreach (var name in info.Worlds.Where(n => n != "default"))
            await _client.PostAsJsonAsync($"/api/worlds/{Uri.EscapeDataString(name)}/trash", new { clearRecordings = false });
        if ((await Worlds()).Trash.Count > 0) await _client.DeleteAsync("/api/trash");
    }

    private sealed record WorldsInfo(string Active, List<string> Worlds, List<string> Trash, RecordingsInfo Recordings);

    private sealed record RecordingsInfo(bool HasRecordings, int CameraCount);
}
