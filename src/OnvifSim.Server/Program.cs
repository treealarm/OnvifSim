using System.Text.Json;
using OnvifSim;
using OnvifSim.Streaming;
using OnvifSim.World;

var options = SimOptions.Parse(args);
var store = new WorldStore(options.DataDirectory);

if (options.Bench)
{
    Bench.Run(store.Current);
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(options.WebPort));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<SceneState>();
builder.Services.AddSingleton<FeedManager>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    foreach (var c in WorldStore.Json.Converters) o.SerializerOptions.Converters.Add(c);
});

var app = builder.Build();

app.MapGet("/api/world", (WorldStore s) => Results.Json(s.Current, WorldStore.Json));
app.MapPut("/api/world", async (HttpRequest req, WorldStore s) =>
{
    var world = await JsonSerializer.DeserializeAsync<WorldModel>(req.Body, WorldStore.Json);
    if (world == null) return Results.BadRequest();
    s.Replace(world);
    return Results.NoContent();
});

app.MapGet("/api/cameras", (SceneState s) =>
    s.Current.World.Cameras.Select(c => new { c.Id, c.Name, c.Kind, c.Video }));

app.MapGet("/api/cameras/{id}/snapshot.jpg", (string id, FeedManager feeds) =>
    feeds.Snapshot(id) is { } jpeg ? Results.Bytes(jpeg, "image/jpeg") : Results.NotFound());

// Motion JPEG over HTTP: what the editor's camera preview shows, and any browser can open directly.
app.MapGet("/api/cameras/{id}/mjpeg", async (string id, HttpContext ctx, FeedManager feeds) =>
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
    try
    {
        await foreach (var frame in sub.Frames.ReadAllAsync(ctx.RequestAborted))
        {
            var header = System.Text.Encoding.ASCII.GetBytes(
                $"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Jpeg.Length}\r\n\r\n");
            await body.WriteAsync(header, ctx.RequestAborted);
            await body.WriteAsync(frame.Jpeg, ctx.RequestAborted);
            await body.WriteAsync("\r\n"u8.ToArray(), ctx.RequestAborted);
            await body.FlushAsync(ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { }
});

app.Logger.LogInformation("OnvifSim: data in {Data}, editor at http://localhost:{Port}/", options.DataDirectory, options.WebPort);
app.Run();

public partial class Program;
