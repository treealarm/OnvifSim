using System.Net.ServerSentEvents;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OnvifSim.Analytics;
using OnvifSim.Geometry;
using OnvifSim.Onvif;
using OnvifSim.Ptz;
using OnvifSim.Rendering;
using OnvifSim.Streaming;
using OnvifSim.Rtsp;
using OnvifSim.World;

namespace OnvifSim.Editor;

// The HTTP API under /api: what the browser editor reads and edits, and the camera previews any browser
// can open. Edits go to one part of the world at a time (an object, a camera, the terrain), each applied
// to the current world atomically, so the editor never overwrites what ONVIF clients changed elsewhere
// meanwhile. Every change is validated first; an invalid one is refused with 400 and the reason.
public static class EditorApi
{
    private static readonly JsonSerializerOptions CompactJson = new(WorldStore.Json) { WriteIndented = false };
    // How often the event stream looks for changes: often enough for a moving PTZ head to look smooth.
    private static readonly TimeSpan EventInterval = TimeSpan.FromMilliseconds(100);
    // A joystick move lasts this long unless the editor renews it, so a camera stops soon after a tab
    // closes mid-move.
    private static readonly TimeSpan JoystickTimeout = TimeSpan.FromSeconds(1);

    public static void MapEditorApi(this WebApplication app)
    {
        MapWorld(app);
        MapCameraStreams(app);
        MapPtz(app);
        MapAnalytics(app);
        MapGroundTruth(app);

        app.MapPost("/api/meshes", (List<WorldObject> objects) =>
            objects.DistinctBy(o => o.Id).ToDictionary(o => o.Id, o => MeshData.From(SceneBuilder.ObjectMesh(o))));

        // The response enumerates the events with the request's own cancellation; shutdown ends them too.
        app.MapGet("/api/events", (WorldStore store, PtzSimulator ptz, OnvifHost onvif, RtspServer rtsp, FeedManager feeds,
                IHostApplicationLifetime lifetime) =>
            TypedResults.ServerSentEvents(Events(store, ptz, onvif, rtsp, feeds, lifetime.ApplicationStopping)));
    }

    private static void MapWorld(WebApplication app)
    {
        app.MapGet("/api/world", (WorldStore store) => Results.Json(store.Current, WorldStore.Json));

        app.MapPut("/api/world", (WorldModel world, WorldStore store, SimOptions options) =>
        {
            try
            {
                WorldValidation.Check(world, options.OnvifBasePort);
            }
            catch (InvalidWorldException ex)
            {
                return Invalid(ex);
            }
            store.Replace(world);
            return Results.NoContent();
        });

        app.MapPut("/api/terrain", (TerrainModel terrain, WorldStore store, SimOptions options) =>
            Change(store, options, world => world.Terrain = terrain));

        app.MapPut("/api/environment", (EnvironmentModel environment, WorldStore store, SimOptions options) =>
            Change(store, options, world => world.Environment = environment));

        // PUT adds the object (road, walker, camera) or replaces the one with that id.
        app.MapPut("/api/objects/{id}", (string id, WorldObject obj, WorldStore store, SimOptions options) =>
        {
            obj.Id = id;
            return Change(store, options, world => Upsert(world.Objects, obj, o => o.Id == id));
        });

        app.MapDelete("/api/objects/{id}", (string id, WorldStore store, SimOptions options) =>
            Remove(store, options, world => world.Objects, o => o.Id == id));

        app.MapPut("/api/roads/{id}", (string id, RoadModel road, WorldStore store, SimOptions options) =>
        {
            road.Id = id;
            return Change(store, options, world => Upsert(world.Roads, road, r => r.Id == id));
        });

        app.MapDelete("/api/roads/{id}", (string id, WorldStore store, SimOptions options) =>
            Remove(store, options, world => world.Roads, r => r.Id == id));

        app.MapPut("/api/walkers/{id}", (string id, WalkerModel walker, WorldStore store, SimOptions options) =>
        {
            walker.Id = id;
            return Change(store, options, world => Upsert(world.Walkers, walker, w => w.Id == id));
        });

        app.MapDelete("/api/walkers/{id}", (string id, WorldStore store, SimOptions options) =>
            Remove(store, options, world => world.Walkers, w => w.Id == id));

        app.MapPut("/api/cameras/{id}", (string id, CameraModel camera, WorldStore store, SimOptions options) =>
        {
            camera.Id = id;
            return Change(store, options, world =>
            {
                // Where the head points, its home and its presets belong to the PTZ simulator and ONVIF
                // clients; the editor turns the head with PTZ commands, so its copy may be out of date.
                var stored = world.Cameras.FirstOrDefault(c => SameId(c.Id, id))?.Ptz;
                if (stored != null && camera.Ptz != null)
                {
                    camera.Ptz.Position = stored.Position;
                    camera.Ptz.Home = stored.Home;
                    camera.Ptz.Presets = stored.Presets;
                }
                Upsert(world.Cameras, camera, c => SameId(c.Id, id));
            });
        });

        app.MapDelete("/api/cameras/{id}", (string id, WorldStore store, SimOptions options) =>
            Remove(store, options, world => world.Cameras, c => SameId(c.Id, id)));
    }

    private static void MapCameraStreams(WebApplication app)
    {
        app.MapGet("/api/cameras", (SceneState s) =>
            s.Current.World.Cameras.Select(c => new { c.Id, c.Name, c.Kind, c.Video }));

        app.MapGet("/api/cameras/{id}/snapshot.jpg", (string id, FeedManager feeds) =>
            feeds.Snapshot(id) is { } jpeg ? Results.Bytes(jpeg, "image/jpeg") : Results.NotFound());

        // Motion JPEG over HTTP: what the editor's camera preview shows, and any browser can open directly.
        app.MapGet("/api/cameras/{id}/mjpeg", async (string id, HttpContext ctx, FeedManager feeds, IHostApplicationLifetime lifetime) =>
        {
            var feed = feeds.Get(id);
            if (feed == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            const string boundary = "onvifsimframe";
            ctx.Response.ContentType = $"multipart/x-mixed-replace; boundary={boundary}";
            ctx.Response.Headers.CacheControl = "no-store";
            using var sub = feed.Subscribe();
            var body = ctx.Response.Body;
            // The stream never ends by itself, so end it on shutdown too; otherwise the host waits for it.
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, lifetime.ApplicationStopping);
            try
            {
                await foreach (var frame in sub.Frames.ReadAllAsync(stop.Token))
                {
                    var header = Encoding.ASCII.GetBytes(
                        $"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Jpeg.Length}\r\n\r\n");
                    await body.WriteAsync(header, stop.Token);
                    await body.WriteAsync(frame.Jpeg, stop.Token);
                    await body.WriteAsync("\r\n"u8.ToArray(), stop.Token);
                    await body.FlushAsync(stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    // What the camera's analytics sees, look by look: the boxes and object ids its metadata stream sends,
    // for the editor to draw over the camera's picture. Each box also says where on the ground the walker
    // stands (x, z) and which way they face, which the metadata stream does not: ground truth for
    // checking what a client made of the box.
    private static void MapAnalytics(WebApplication app) =>
        app.MapGet("/api/cameras/{id}/analytics", (string id, AnalyticsManager analytics, IHostApplicationLifetime lifetime, CancellationToken requestAborted) =>
            analytics.Get(id) is { } camera
                ? (IResult)TypedResults.ServerSentEvents(Looks(camera, lifetime.ApplicationStopping, requestAborted))
                : Results.NotFound());

    private static async IAsyncEnumerable<SseItem<string>> Looks(
        CameraAnalytics camera, CancellationToken stopping, [EnumeratorCancellation] CancellationToken requestAborted = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, stopping);
        using var looks = camera.Subscribe();
        while (await WaitToReadAsync(looks.Frames, stop.Token))
            while (looks.Frames.TryRead(out var frame))
                yield return new SseItem<string>(JsonSerializer.Serialize(
                    frame.Objects.Select(o => new
                    {
                        id = o.ObjectId, walker = o.WalkerId, o.Box.Left, o.Box.Top, o.Box.Right, o.Box.Bottom,
                        x = o.Walk.X, z = o.Walk.Z, heading = o.Walk.Heading,
                    }),
                    CompactJson), "objects");
    }

    private static async Task<bool> WaitToReadAsync<T>(ChannelReader<T> reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.WaitToReadAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // Ground truth for tests outside the simulator: where a camera stands and looks, and where a walker is,
    // at this moment. A client that calibrates a camera or aims one at a person can be checked against
    // these; the editor does not use them. A camera's position is absolute (the terrain under it plus its
    // own height), its heading and pitch include the PTZ head, and hfov follows the zoom. forward, right
    // and up are the picture's own unit vectors in world space (rotation and all): with them a client
    // projects a point into the picture without knowing how the camera is mounted.
    private static void MapGroundTruth(WebApplication app)
    {
        app.MapGet("/api/cameras/{id}/pose", (string id, SceneState scene, PtzSimulator ptz, TimeProvider time) =>
        {
            var camera = scene.FindCamera(id);
            if (camera == null) return Results.NotFound();
            var head = camera.Kind == CameraKind.Ptz && camera.Ptz != null ? ptz.StateOf(camera) : (PtzState?)null;
            var pose = CameraPose.From(camera, scene.Current.Scene.Terrain, head?.Position ?? default);
            return Results.Json(new
            {
                utcTime = time.GetUtcNow(),
                position = new { x = pose.Position.X, y = pose.Position.Y, z = pose.Position.Z },
                heading = pose.Heading, pitch = pose.Pitch, roll = pose.Roll, hfov = pose.HFov,
                forward = Xyz(pose.Forward), right = Xyz(pose.Right), up = Xyz(pose.Up),
                pan = head?.Position.Pan, tilt = head?.Position.Tilt, zoom = head?.Position.Zoom,
                moving = head is { } h ? h.PanTiltMoving || h.ZoomMoving : (bool?)null,
            }, CompactJson);
        });

        // Turns a PTZ head until its picture is centred on a point of the world (absolute coordinates, as
        // pose reports them): an operator with a joystick putting a landmark in the crosshair. Answers
        // the pan and tilt it goes to, in degrees relative to the mount; a point the head cannot reach
        // within its limits is refused with 409.
        app.MapPost("/api/cameras/{id}/ptz/aim", (string id, Vec3 target, SceneState scene, PtzSimulator ptz) =>
        {
            var camera = scene.FindCamera(id);
            if (camera == null) return Results.NotFound();
            if (camera.Kind != CameraKind.Ptz || camera.Ptz is not { } limits)
                return Results.Problem($"Camera {camera.Id} has no PTZ head.", statusCode: StatusCodes.Status409Conflict);
            var eye = CameraPose.From(camera, scene.Current.Scene.Terrain, default).Position;
            var direction = new Vector3(target.X, target.Y, target.Z) - eye;
            if (direction.LengthSquared() < 1e-6f)
                return Results.Problem("The point is where the camera is.", statusCode: StatusCodes.Status409Conflict);
            var (pan, tilt) = CameraPose.PanTiltTowards(camera, direction);
            if (!limits.EndlessPan)
            {
                // The same direction one turn either way may be the one inside the limits.
                pan = new[] { pan, pan - 360, pan + 360 }.FirstOrDefault(p => p >= limits.PanMin && p <= limits.PanMax, float.NaN);
                if (float.IsNaN(pan))
                    return Results.Problem($"Pan to that point is outside {limits.PanMin}..{limits.PanMax}.", statusCode: StatusCodes.Status409Conflict);
            }
            if (tilt < limits.TiltMin || tilt > limits.TiltMax)
                return Results.Problem($"Tilt {tilt:0.##} to that point is outside {limits.TiltMin}..{limits.TiltMax}.", statusCode: StatusCodes.Status409Conflict);
            ptz.MoveTo(camera, new PtzAxes(pan, tilt, null), default);
            return Results.Json(new { pan, tilt }, CompactJson);
        });

        app.MapGet("/api/walkers/{id}/pose", (string id, SceneState scene, TimeProvider time) =>
        {
            var snapshot = scene.Current;
            var walker = snapshot.World.Walkers.FirstOrDefault(w => SameId(w.Id, id));
            if (walker == null) return Results.NotFound();
            var now = time.GetUtcNow();
            var walk = WalkerMotion.PoseAt(walker, now);
            return Results.Json(new
            {
                utcTime = now, x = walk.X, y = snapshot.Scene.Terrain.HeightAt(walk.X, walk.Z), z = walk.Z, heading = walk.Heading,
            }, CompactJson);
        });
    }

    // The editor's joystick, sliders and home button drive the same simulator as ONVIF clients do.
    // Velocities are fractions of the top speed (-1..1), positions are degrees and zoom 0..1.
    private static void MapPtz(WebApplication app)
    {
        app.MapPost("/api/cameras/{id}/ptz/move", (string id, PtzAxes velocity, SceneState scene, PtzSimulator ptz) =>
            WithHead(scene, id, camera => ptz.MoveContinuously(camera, velocity, JoystickTimeout)));

        app.MapPost("/api/cameras/{id}/ptz/goto", (string id, PtzAxes position, SceneState scene, PtzSimulator ptz) =>
            WithHead(scene, id, camera => ptz.MoveTo(camera, position, default)));

        app.MapPost("/api/cameras/{id}/ptz/home", (string id, SceneState scene, PtzSimulator ptz) =>
            WithHead(scene, id, camera =>
            {
                var home = camera.Ptz!.Home;
                ptz.MoveTo(camera, new PtzAxes(home.Pan, home.Tilt, home.Zoom), default);
            }));

        app.MapPost("/api/cameras/{id}/ptz/stop", (string id, SceneState scene, PtzSimulator ptz) =>
            WithHead(scene, id, camera => ptz.Stop(camera, panTilt: true, zoom: true)));
    }

    private static object Xyz(Vector3 v) => new { x = v.X, y = v.Y, z = v.Z };

    private static IResult WithHead(SceneState scene, string id, Action<CameraModel> command)
    {
        var camera = scene.FindCamera(id);
        if (camera == null) return Results.NotFound();
        if (camera.Kind != CameraKind.Ptz || camera.Ptz == null)
            return Results.Problem($"Camera {camera.Id} has no PTZ head.", statusCode: StatusCodes.Status409Conflict);
        command(camera);
        return Results.NoContent();
    }

    private static IResult Change(WorldStore store, SimOptions options, Action<WorldModel> change)
    {
        try
        {
            store.Update(world =>
            {
                change(world);
                WorldValidation.Check(world, options.OnvifBasePort);
            });
        }
        catch (InvalidWorldException ex)
        {
            return Invalid(ex);
        }
        return Results.NoContent();
    }

    // Deleting what is not there is a 404 that leaves world.json alone.
    private static IResult Remove<T>(WorldStore store, SimOptions options, Func<WorldModel, List<T>> items, Predicate<T> match) =>
        items(store.Current).Exists(match)
            ? Change(store, options, world => items(world).RemoveAll(match))
            : Results.NotFound();

    private static IResult Invalid(InvalidWorldException ex) =>
        Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "The change was refused");

    private static void Upsert<T>(List<T> items, T item, Predicate<T> isSame)
    {
        int index = items.FindIndex(isSame);
        if (index < 0) items.Add(item);
        else items[index] = item;
    }

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // What the editor keeps in sync with: the world whenever it changes ("world"), the ONVIF ports and
    // streams on offer ("status") and where the PTZ heads point while they move ("ptz"). Each comes
    // first on connecting, then only when it changed.
    private static async IAsyncEnumerable<SseItem<string>> Events(WorldStore store, PtzSimulator ptz, OnvifHost onvif, RtspServer rtsp,
        FeedManager feeds, CancellationToken stopping, [EnumeratorCancellation] CancellationToken requestAborted = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, stopping);
        using var timer = new PeriodicTimer(EventInterval);
        WorldModel? sentWorld = null;
        string? sentStatus = null, sentHeads = null;
        do
        {
            var world = store.Current;
            if (!ReferenceEquals(world, sentWorld))
            {
                sentWorld = world;
                yield return new SseItem<string>(JsonSerializer.Serialize(world, CompactJson), "world");
            }
            var status = JsonSerializer.Serialize(new
            {
                rtspPort = rtsp.Port,
                h264 = feeds.H264Available,
                onvifPorts = world.Cameras.ToDictionary(c => c.Id, c => onvif.PortOf(c.Id)),
            }, CompactJson);
            if (status != sentStatus)
            {
                sentStatus = status;
                yield return new SseItem<string>(status, "status");
            }
            var heads = JsonSerializer.Serialize(
                world.Cameras.Where(c => c.Kind == CameraKind.Ptz && c.Ptz != null).ToDictionary(c => c.Id, c => HeadEvent.Of(ptz.StateOf(c))),
                CompactJson);
            if (heads != sentHeads)
            {
                sentHeads = heads;
                yield return new SseItem<string>(heads, "ptz");
            }
        }
        while (await NextTickAsync(timer, stop.Token));
    }

    private static async Task<bool> NextTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private sealed record HeadEvent(float Pan, float Tilt, float Zoom, bool Moving)
    {
        public static HeadEvent Of(PtzState state) =>
            new(state.Position.Pan, state.Position.Tilt, state.Position.Zoom, state.PanTiltMoving || state.ZoomMoving);
    }

    // An object's mesh in its own coordinates, as base64 of little-endian float32 xyz triples (rgb 0..1
    // for colours) and uint32 triangle indices: what a three.js BufferGeometry takes as it is.
    public sealed record MeshData(string Positions, string Normals, string Colors, string Indices)
    {
        public static MeshData From(Mesh mesh) => new(
            Base64(mesh.Positions), Base64(mesh.Normals), Base64(mesh.Colors),
            Convert.ToBase64String(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(mesh.Indices))));

        // Vector3 is three consecutive floats, and every platform .NET runs the simulator on is little-endian.
        private static string Base64(List<Vector3> values) =>
            Convert.ToBase64String(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(values)));
    }
}
