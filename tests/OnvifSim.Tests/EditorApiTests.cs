using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OnvifSim.Editor;
using OnvifSim.Onvif;
using OnvifSim.World;

namespace OnvifSim.Tests;

// The API the browser editor works with. Each test uses its own objects and cameras, or puts back
// what it changed, as the class shares one simulator.
public class EditorApiTests(SimFactory factory) : IClassFixture<SimFactory>
{
    private WorldStore Store => factory.Services.GetRequiredService<WorldStore>();

    private static JsonContent Json(object value) => JsonContent.Create(value, options: WorldStore.Json);

    private static async Task<string> ReasonOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString()!;

    [Fact]
    public async Task Objects_AreAddedReplacedAndDeleted_ByTheirId()
    {
        var client = factory.CreateClient();
        var added = await client.PutAsync("/api/objects/test-pine", Json(new { type = "tree.pine", position = new { x = 5, y = 0, z = 5 }, scale = new { x = 4, y = 12, z = 4 } }));
        Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);
        Assert.Equal("tree.pine", Store.Current.Objects.Single(o => o.Id == "test-pine").Type);

        await client.PutAsync("/api/objects/test-pine", Json(new { type = "rock", scale = new { x = 2, y = 2, z = 2 } }));
        Assert.Equal("rock", Assert.Single(Store.Current.Objects, o => o.Id == "test-pine").Type);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/objects/test-pine")).StatusCode);
        Assert.DoesNotContain(Store.Current.Objects, o => o.Id == "test-pine");
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/objects/test-pine")).StatusCode);
    }

    [Fact]
    public async Task RoadsAndWalkers_AreAddedAndDeleted_ByTheirId()
    {
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync("/api/roads/test-road", Json(new { points = new[] { new { x = 0, z = 0 }, new { x = 20, z = 5 } }, width = 3 }))).StatusCode);
        Assert.Equal(3, Store.Current.Roads.Single(r => r.Id == "test-road").Width);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync("/api/walkers/test-walker", Json(new { route = new[] { new { x = 1, z = 2 } }, @return = "reverse" }))).StatusCode);
        Assert.Equal(WalkerReturn.Reverse, Store.Current.Walkers.Single(w => w.Id == "test-walker").Return);

        // A road of one point is no road.
        var single = await client.PutAsync("/api/roads/test-road", Json(new { points = new[] { new { x = 0, z = 0 } } }));
        Assert.Equal(HttpStatusCode.BadRequest, single.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/roads/test-road")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/walkers/test-walker")).StatusCode);
        Assert.DoesNotContain(Store.Current.Roads, r => r.Id == "test-road");
        Assert.DoesNotContain(Store.Current.Walkers, w => w.Id == "test-walker");
    }

    [Fact]
    public async Task AnInvalidChange_IsRefusedWithTheReason_AndChangesNothing()
    {
        var client = factory.CreateClient();
        var before = Store.Current;

        var unknownType = await client.PutAsync("/api/objects/test-ufo", Json(new { type = "ufo", scale = new { x = 1, y = 1, z = 1 } }));
        Assert.Equal(HttpStatusCode.BadRequest, unknownType.StatusCode);
        Assert.Contains("unknown type 'ufo'", await ReasonOf(unknownType));

        var camera = JsonSerializer.Deserialize<WorldModel>(await client.GetStringAsync("/api/world"), WorldStore.Json)!.Cameras.Single(c => c.Id == "cam1");
        camera.Video.Width = 641;
        var oddSize = await client.PutAsync("/api/cameras/cam1", Json(camera));
        Assert.Equal(HttpStatusCode.BadRequest, oddSize.StatusCode);
        Assert.Contains("641x360", await ReasonOf(oddSize));

        // Camera ids are RTSP paths.
        var badId = await client.PutAsync("/api/cameras/no.dots", Json(new { kind = "fixed" }));
        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);

        Assert.Same(before, Store.Current);
    }

    [Fact]
    public async Task EditingACamera_KeepsWhereItsPtzHeadPoints()
    {
        var client = factory.CreateClient();
        var stale = JsonSerializer.Deserialize<WorldModel>(await client.GetStringAsync("/api/world"), WorldStore.Json)!.Cameras.Single(c => c.Id == "cam2");
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/cameras/cam2/ptz/goto", Json(new { pan = 20 }))).StatusCode);
            for (int i = 0; i < 50 && Store.Current.Cameras.Single(c => c.Id == "cam2").Ptz!.Pan != 20; i++) await Task.Delay(100);

            // The editor's copy still says pan 0; only the name is meant to change.
            stale.Name = "Renamed tower";
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync("/api/cameras/cam2", Json(stale))).StatusCode);

            var saved = Store.Current.Cameras.Single(c => c.Id == "cam2");
            Assert.Equal(("Renamed tower", 20f), (saved.Name, saved.Ptz!.Pan));
        }
        finally
        {
            Store.Update(w =>
            {
                var cam2 = w.Cameras.Single(c => c.Id == "cam2");
                cam2.Name = "Tower PTZ";
                cam2.Ptz!.Position = new PtzPosition(0, -10, 0);
            });
        }
    }

    [Fact]
    public async Task Ptz_DrivesOnlyCamerasWithAHead()
    {
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/cameras/cam1/ptz/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/cameras/nope/ptz/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/cameras/cam2/ptz/stop", null)).StatusCode);
    }

    [Fact]
    public async Task Meshes_AreIndexedTriangles_KeyedByTheIdsSent()
    {
        var response = await factory.CreateClient().PostAsync("/api/meshes",
            Json(new[] { new { id = "a", type = "box", scale = new { x = 2, y = 2, z = 2 } }, new { id = "b", type = "house", scale = new { x = 8, y = 5, z = 10 } } }));
        var meshes = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        var box = meshes.GetProperty("a");
        int vertices = Convert.FromBase64String(box.GetProperty("positions").GetString()!).Length / 12;
        var indices = Convert.FromBase64String(box.GetProperty("indices").GetString()!);
        // A flat-shaded box: 6 faces of 2 triangles, each with vertices of its own.
        Assert.Equal((36, 36 * 4), (vertices, indices.Length));
        Assert.Equal(vertices * 12, Convert.FromBase64String(box.GetProperty("colors").GetString()!).Length);
        Assert.True(meshes.TryGetProperty("b", out _));
    }

    private static async Task<JsonElement> GetJson(HttpClient client, string url) =>
        JsonDocument.Parse(await client.GetStringAsync(url)).RootElement;

    [Fact]
    public async Task CameraPose_IsTheMountPlusTheHeadOnTopOfTheTerrain()
    {
        var client = factory.CreateClient();
        var cam1 = await GetJson(client, "/api/cameras/cam1/pose");
        var ground = new Terrain(Store.Current.Terrain).HeightAt(0, 60);
        Assert.Equal(6 + ground, cam1.GetProperty("position").GetProperty("y").GetSingle(), 3);
        Assert.Equal((0f, -8f, 75f), (cam1.GetProperty("heading").GetSingle(), cam1.GetProperty("pitch").GetSingle(), cam1.GetProperty("hfov").GetSingle()));
        Assert.Equal(JsonValueKind.Null, cam1.GetProperty("pan").ValueKind);

        await client.PutAsync("/api/cameras/test-pose", Json(new { name = "pose", kind = "ptz", position = new { x = 10, y = 5, z = 10 }, heading = 100, pitch = 0, ptz = new { } }));
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/cameras/test-pose/ptz/goto", Json(new { pan = 20, tilt = -10, zoom = 0 }))).StatusCode);
            JsonElement pose;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            do
            {
                await Task.Delay(50);
                pose = await GetJson(client, "/api/cameras/test-pose/pose");
            }
            while (pose.GetProperty("moving").GetBoolean() && DateTime.UtcNow < deadline);

            Assert.False(pose.GetProperty("moving").GetBoolean());
            Assert.Equal(20f, pose.GetProperty("pan").GetSingle(), 3);
            Assert.Equal(120f, pose.GetProperty("heading").GetSingle(), 3);
            Assert.Equal(-10f, pose.GetProperty("pitch").GetSingle(), 3);
            Assert.Equal(60f, pose.GetProperty("hfov").GetSingle(), 3);
            // The picture's own axes, for a client that projects without knowing the mount.
            var forward = pose.GetProperty("forward");
            float h = 120 * MathF.PI / 180, p = -10 * MathF.PI / 180;
            Assert.Equal(MathF.Sin(h) * MathF.Cos(p), forward.GetProperty("x").GetSingle(), 4);
            Assert.Equal(MathF.Sin(p), forward.GetProperty("y").GetSingle(), 4);
            Assert.Equal(-MathF.Cos(h) * MathF.Cos(p), forward.GetProperty("z").GetSingle(), 4);
            Assert.Equal(0f, pose.GetProperty("right").GetProperty("y").GetSingle(), 4);
        }
        finally
        {
            await client.DeleteAsync("/api/cameras/test-pose");
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/cameras/test-pose/pose")).StatusCode);
    }

    [Fact]
    public async Task Aim_CentresThePointWhateverTheMount_AndRefusesWhatTheHeadCannotReach()
    {
        var client = factory.CreateClient();
        // On its side ("on a fence"): pan sweeps up and down, tilt across.
        await client.PutAsync("/api/cameras/test-aim", Json(new
        {
            name = "aim", kind = "ptz", position = new { x = -20, y = 4, z = 30 }, heading = 30, pitch = 0, roll = 90,
            ptz = new { tiltMin = -90, tiltMax = 90 },
        }));
        try
        {
            var ground = new Terrain(Store.Current.Terrain);
            var target = new System.Numerics.Vector3(-8, ground.HeightAt(-8, 12), 12);
            var aimed = await client.PostAsync("/api/cameras/test-aim/ptz/aim", Json(new { x = target.X, y = target.Y, z = target.Z }));
            Assert.Equal(HttpStatusCode.OK, aimed.StatusCode);
            JsonElement pose;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            do
            {
                await Task.Delay(50);
                pose = await GetJson(client, "/api/cameras/test-aim/pose");
            }
            while (pose.GetProperty("moving").GetBoolean() && DateTime.UtcNow < deadline);

            var eye = pose.GetProperty("position");
            var toTarget = System.Numerics.Vector3.Normalize(target - new System.Numerics.Vector3(
                eye.GetProperty("x").GetSingle(), eye.GetProperty("y").GetSingle(), eye.GetProperty("z").GetSingle()));
            var forward = pose.GetProperty("forward");
            var axis = new System.Numerics.Vector3(forward.GetProperty("x").GetSingle(), forward.GetProperty("y").GetSingle(), forward.GetProperty("z").GetSingle());
            double miss = Math.Acos(Math.Clamp(System.Numerics.Vector3.Dot(System.Numerics.Vector3.Normalize(axis), toTarget), -1, 1)) * 180 / Math.PI;
            Assert.True(miss < 0.05, $"the head looks {miss:0.###} degrees off the point");

            // Straight up is beyond a head that tilts at most 20 degrees above its mount's horizon.
            await client.PutAsync("/api/cameras/test-aim", Json(new
            {
                name = "aim", kind = "ptz", position = new { x = -20, y = 4, z = 30 }, heading = 30, ptz = new { },
            }));
            var refused = await client.PostAsync("/api/cameras/test-aim/ptz/aim", Json(new { x = -20, y = 500, z = 30 }));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }
        finally
        {
            await client.DeleteAsync("/api/cameras/test-aim");
        }
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/cameras/cam1/ptz/aim", Json(new { x = 0, y = 0, z = 0 }))).StatusCode);
    }

    [Fact]
    public async Task WalkerPose_IsWhereTheRouteSaysAtThisMoment()
    {
        var client = factory.CreateClient();
        await client.PutAsync("/api/walkers/test-standing", Json(new { route = new[] { new { x = 3, z = -4 } } }));
        try
        {
            var pose = await GetJson(client, "/api/walkers/test-standing/pose");
            Assert.Equal((3f, -4f), (pose.GetProperty("x").GetSingle(), pose.GetProperty("z").GetSingle()));
            Assert.Equal(new Terrain(Store.Current.Terrain).HeightAt(3, -4), pose.GetProperty("y").GetSingle(), 3);
        }
        finally
        {
            await client.DeleteAsync("/api/walkers/test-standing");
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/walkers/test-standing/pose")).StatusCode);
    }

    [Fact]
    public async Task AnalyticsBoxes_SayWhereTheWalkerStands()
    {
        var client = factory.CreateClient();
        await client.PutAsync("/api/walkers/test-seen", Json(new { route = new[] { new { x = 2, z = 45 } } }));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await client.GetAsync("/api/cameras/cam1/analytics", HttpCompletionOption.ResponseHeadersRead, cts.Token);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
            JsonElement? seen = null;
            while (seen == null && await reader.ReadLineAsync(cts.Token) is { } line)
            {
                if (!line.StartsWith("data: ")) continue;
                foreach (var o in JsonDocument.Parse(line["data: ".Length..]).RootElement.EnumerateArray())
                    if (o.GetProperty("walker").GetString() == "test-seen") seen = o;
            }
            Assert.NotNull(seen);
            Assert.Equal((2f, 45f), (seen.Value.GetProperty("x").GetSingle(), seen.Value.GetProperty("z").GetSingle()));
        }
        finally
        {
            await client.DeleteAsync("/api/walkers/test-seen");
        }
    }

    [Fact]
    public async Task Events_StartWithTheWorld_TheServicesAndThePtzHeads()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await factory.CreateClient().GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));

        var events = new Dictionary<string, JsonElement>();
        string? name = null;
        while (events.Count < 3 && await reader.ReadLineAsync(cts.Token) is { } line)
        {
            if (line.StartsWith("event: ")) name = line["event: ".Length..];
            else if (line.StartsWith("data: ") && name != null) events[name] = JsonDocument.Parse(line["data: ".Length..]).RootElement;
        }

        Assert.Equal(Store.Current.Cameras.Count, events["world"].GetProperty("cameras").GetArrayLength());
        Assert.Equal(factory.Services.GetRequiredService<OnvifHost>().PortOf("cam1"), events["status"].GetProperty("onvifPorts").GetProperty("cam1").GetInt32());
        Assert.True(events["ptz"].TryGetProperty("cam2", out var head));
        Assert.False(head.GetProperty("moving").GetBoolean());
    }
}

public class WorldValidationTests
{
    [Fact]
    public void TheDefaultWorld_IsValid()
    {
        WorldValidation.Check(DefaultWorld.Create(), onvifBasePort: 8001);
    }

    [Fact]
    public void TwoCamerasOnOnePort_AreRefused_EvenWhenOneOfThemGetsItAutomatically()
    {
        var world = DefaultWorld.Create();
        // cam2 is second, so it gets 8002 automatically.
        world.Cameras.Single(c => c.Id == "cam1").OnvifPort = 8002;
        var error = Assert.Throws<InvalidWorldException>(() => WorldValidation.Check(world, onvifBasePort: 8001));
        Assert.Contains("8002", error.Message);
    }

    [Fact]
    public void APtzCamera_NeedsPtzSettings()
    {
        var world = DefaultWorld.Create();
        world.Cameras.Single(c => c.Id == "cam2").Ptz = null;
        Assert.Throws<InvalidWorldException>(() => WorldValidation.Check(world, onvifBasePort: 8001));
    }
}
