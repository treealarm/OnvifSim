using OnvifSim.Recording;
using OnvifSim.World;

namespace OnvifSim.Editor;

// The worlds in the data directory: list, create, open, trash and restore. One world is active; the
// editor switches between them while the simulator keeps running.
public static class WorldLibraryApi
{
    public sealed record WorldsResponse(
        string Active,
        IReadOnlyList<string> Worlds,
        IReadOnlyList<string> Trash,
        RecordingsSummary Recordings);

    public sealed record RecordingsSummary(bool HasRecordings, int CameraCount);

    public sealed record CreateWorld(string? Name, bool? ClearRecordings);
    public sealed record SwitchWorld(bool? ClearRecordings);
    public sealed record RestoreWorld(string? Name);

    // The same shape answers GET /api/worlds and the "worlds" event of the stream.
    public static WorldsResponse ListingOf(WorldStore store, RecordingLog recordings)
    {
        var listing = store.Listing();
        var summary = recordings.Summary();
        return new WorldsResponse(listing.Active, listing.Worlds, listing.Trash, new RecordingsSummary(summary.HasRecordings, summary.CameraCount));
    }

    public static void MapWorldLibrary(this WebApplication app)
    {
        app.MapGet("/api/worlds", (WorldStore store, RecordingLog recordings) =>
            Listing(store, recordings));

        // Creates an empty world and opens it. The recordings question is asked before anything is
        // written, so cancelling leaves no half-created world behind.
        app.MapPost("/api/worlds", (CreateWorld request, WorldStore store, RecordingLog recordings) =>
        {
            try
            {
                var name = WorldNames.Normalize(request.Name);
                if (store.Exists(name))
                    throw new WorldAlreadyExistsException($"A world named '{name}' already exists.");
                if (request.ClearRecordings is null && recordings.Summary().HasRecordings)
                    return RecordingsChoice(store, recordings, name);
                store.Create(name);
                store.Activate(name, request.ClearRecordings ?? false);
                return Listing(store, recordings);
            }
            catch (Exception ex) when (IsRequestError(ex))
            {
                return Error(ex);
            }
        });

        app.MapPost("/api/worlds/{name}/activate", (string name, SwitchWorld request, WorldStore store, RecordingLog recordings) =>
        {
            try
            {
                var normalized = WorldNames.Normalize(name);
                if (!store.Exists(normalized)) throw new WorldNotFoundException($"No world named '{normalized}'.");
                if (!WorldNames.Same(normalized, store.ActiveName)
                    && request.ClearRecordings is null && recordings.Summary().HasRecordings)
                    return RecordingsChoice(store, recordings, normalized);
                store.Activate(normalized, request.ClearRecordings ?? false);
                return Listing(store, recordings);
            }
            catch (Exception ex) when (IsRequestError(ex))
            {
                return Error(ex);
            }
        });

        app.MapPost("/api/worlds/{name}/trash", (string name, SwitchWorld request, WorldStore store, RecordingLog recordings) =>
        {
            try
            {
                var normalized = WorldNames.Normalize(name);
                if (!store.Exists(normalized)) throw new WorldNotFoundException($"No world named '{normalized}'.");
                if (WorldNames.Same(normalized, store.ActiveName)
                    && request.ClearRecordings is null && recordings.Summary().HasRecordings)
                    return RecordingsChoice(store, recordings, normalized);
                store.Trash(normalized, request.ClearRecordings ?? false);
                return Listing(store, recordings);
            }
            catch (Exception ex) when (IsRequestError(ex))
            {
                return Error(ex);
            }
        });

        app.MapPost("/api/trash/{name}/restore", (string name, RestoreWorld request, WorldStore store, RecordingLog recordings) =>
        {
            try
            {
                store.Restore(name, request.Name);
                return Listing(store, recordings);
            }
            catch (Exception ex) when (IsRequestError(ex))
            {
                return Error(ex);
            }
        });

        app.MapDelete("/api/trash", (WorldStore store, RecordingLog recordings) =>
        {
            store.EmptyTrash();
            return Listing(store, recordings);
        });
    }

    private static IResult Listing(WorldStore store, RecordingLog recordings) =>
        Results.Json(ListingOf(store, recordings), EditorApi.CompactJson);

    private static IResult RecordingsChoice(WorldStore store, RecordingLog recordings, string target)
    {
        var summary = recordings.Summary();
        string cameras = summary.CameraCount == 1 ? "1 camera" : $"{summary.CameraCount} cameras";
        return Results.Problem(
            detail: $"The cameras in '{store.ActiveName}' have recordings ({cameras}). Switch and clear them, or keep them?",
            statusCode: StatusCodes.Status409Conflict,
            title: "The world has recordings",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "recordings",
                ["active"] = store.ActiveName,
                ["target"] = target,
                ["cameraCount"] = summary.CameraCount,
            });
    }

    private static bool IsRequestError(Exception ex) =>
        ex is InvalidWorldNameException or WorldAlreadyExistsException or WorldNotFoundException
            or InvalidWorldException or InvalidDataException or IOException;

    private static IResult Error(Exception ex) => ex switch
    {
        InvalidWorldNameException e => Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest,
            title: "The name is not usable"),
        WorldAlreadyExistsException e => Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict,
            title: "A world with that name exists"),
        WorldNotFoundException e => Results.Problem(e.Message, statusCode: StatusCodes.Status404NotFound,
            title: "No such world"),
        InvalidWorldException e => Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest,
            title: "The world cannot be used"),
        InvalidDataException e => Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest,
            title: "The world cannot be loaded"),
        IOException e => Results.Problem(e.Message, statusCode: StatusCodes.Status500InternalServerError,
            title: "The world cannot be saved"),
        _ => throw new InvalidOperationException("Not a request error.", ex),
    };
}
