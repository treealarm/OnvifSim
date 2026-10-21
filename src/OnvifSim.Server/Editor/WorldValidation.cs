using System.Text.RegularExpressions;
using OnvifSim.Geometry;
using OnvifSim.Onvif;
using OnvifSim.Onvif.Services;
using OnvifSim.World;

namespace OnvifSim.Editor;

public sealed class InvalidWorldException(string message) : Exception(message);

// Checks a world before it replaces the current one, so a change from the editor (or a hand-written
// PUT) cannot hand the renderer, the encoders or the listeners something they cannot work with. The
// video ranges are the ones the Media service holds ONVIF clients to.
public static partial class WorldValidation
{
    private const int MinVideoSide = 16;
    // RTP/JPEG carries sizes in 8-pixel blocks up to 2040 (RFC 2435); H.264 is held to the same sizes.
    private const int MaxVideoSide = 2040;
    private const float MaxObjectSize = 1000;
    private const int MaxTerrainResolution = 1024;

    // Camera ids are RTSP paths (rtsp://host:8554/{id}), so they stay URL-safe.
    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex CameraIdPattern();

    public static void Check(WorldModel world, int onvifBasePort)
    {
        CheckTerrain(world.Terrain);
        CheckEnvironment(world.Environment);
        foreach (var obj in world.Objects) CheckObject(obj);
        RequireUnique(world.Objects.Select(o => o.Id), "Object id");
        foreach (var road in world.Roads) CheckRoad(road);
        RequireUnique(world.Roads.Select(r => r.Id), "Road id");
        foreach (var walker in world.Walkers) CheckWalker(walker);
        RequireUnique(world.Walkers.Select(w => w.Id), "Walker id");
        foreach (var camera in world.Cameras) CheckCamera(camera);
        RequireUnique(world.Cameras.Select(c => c.Id), "Camera id");
        RequireUnique(
            world.Cameras.Select(c => OnvifHost.RequestedPort(world, c, onvifBasePort)).Where(p => p != 0).Select(p => p.ToString()),
            "ONVIF port");
    }

    private static void CheckTerrain(TerrainModel terrain)
    {
        Require(terrain.Size is >= 10 and <= 10000, $"Terrain size {terrain.Size} m is outside 10..10000.");
        Require(terrain.Resolution is >= 1 and <= MaxTerrainResolution, $"Terrain resolution {terrain.Resolution} is outside 1..{MaxTerrainResolution}.");
        int samples = (terrain.Resolution + 1) * (terrain.Resolution + 1);
        // No heights at all is a flat terrain.
        Require(terrain.Heights.Length is 0 || terrain.Heights.Length == samples,
            $"Terrain of resolution {terrain.Resolution} needs {samples} heights, got {terrain.Heights.Length}.");
        Require(terrain.Heights.All(float.IsFinite), "Terrain heights must be finite numbers.");
    }

    private static void CheckEnvironment(EnvironmentModel env)
    {
        Require(env.SunElevation is >= -90 and <= 90, $"Sun elevation {env.SunElevation} is outside -90..90.");
        Require(env.FogStart >= 0 && env.FogEnd > env.FogStart, "Fog must start at 0 m or further and end beyond its start.");
        Require(env.FrustumLength is >= 1 and <= 1000, $"The field of view's length {env.FrustumLength} m is outside 1..1000.");
    }

    private static void CheckObject(WorldObject obj)
    {
        Require(obj.Id.Length > 0, "An object has no id.");
        Require(SceneBuilder.ObjectTypes.Contains(obj.Type), $"Object {obj.Id}: unknown type '{obj.Type}', expected one of {string.Join(", ", SceneBuilder.ObjectTypes)}.");
        Require(IsFinite(obj.Position) && float.IsFinite(obj.RotationY), $"Object {obj.Id}: position and rotation must be finite.");
        Require(obj.Scale is { X: > 0 and <= MaxObjectSize, Y: > 0 and <= MaxObjectSize, Z: > 0 and <= MaxObjectSize },
            $"Object {obj.Id}: every size must be above 0 and at most {MaxObjectSize} m.");
    }

    private static void CheckRoad(RoadModel road)
    {
        Require(road.Id.Length > 0, "A road has no id.");
        Require(road.Points.Count >= 2, $"Road {road.Id} needs at least two points.");
        Require(road.Points.All(IsFinite), $"Road {road.Id}: points must be finite.");
        Require(road.Width is >= 0.5f and <= 50, $"Road {road.Id}: width {road.Width} m is outside 0.5..50.");
    }

    private static void CheckWalker(WalkerModel walker)
    {
        Require(walker.Id.Length > 0, "A walker has no id.");
        Require(walker.Route.Count >= 1, $"Walker {walker.Id} needs at least one point to stand on.");
        Require(walker.Route.All(IsFinite), $"Walker {walker.Id}: route points must be finite.");
        Require(walker.Speed is >= 0 and <= 10, $"Walker {walker.Id}: speed {walker.Speed} m/s is outside 0..10.");
        Require(walker.Height is >= 0.5f and <= 3, $"Walker {walker.Id}: height {walker.Height} m is outside 0.5..3.");
    }

    private static void CheckCamera(CameraModel camera)
    {
        string id = camera.Id;
        Require(CameraIdPattern().IsMatch(id), $"Camera id '{id}' must be 1..32 letters, digits, '-' or '_'.");
        Require(IsFinite(camera.Position) && float.IsFinite(camera.Heading) && float.IsFinite(camera.Pitch) && float.IsFinite(camera.Roll),
            $"Camera {id}: position and orientation must be finite.");
        Require(camera.HFov is >= 1 and <= 170, $"Camera {id}: field of view {camera.HFov} is outside 1..170.");
        Require(camera.OnvifPort is >= 0 and <= 65535, $"Camera {id}: ONVIF port {camera.OnvifPort} is outside 0..65535.");
        Require(camera.RecordingRetentionHours is >= 1 and <= 8760,
            $"Camera {id}: recording retention {camera.RecordingRetentionHours} h is outside 1..8760 (a year).");

        var video = camera.Video;
        CheckVideoSize(id, "JPEG", video.Width, video.Height);
        CheckRange(id, "JPEG frame rate", video.Fps, CameraMedia.MinFrameRate, CameraMedia.MaxFrameRate);
        CheckRange(id, "JPEG quality", video.Quality, CameraMedia.MinQuality, CameraMedia.MaxQuality);
        Require(video.Rotate is 0 or 90 or 180 or 270, $"Camera {id}: picture rotation {video.Rotate} is not 0, 90, 180 or 270.");

        var h264 = camera.H264;
        CheckVideoSize(id, "H.264", h264.Width, h264.Height);
        CheckRange(id, "H.264 frame rate", h264.Fps, CameraMedia.MinFrameRate, CameraMedia.MaxFrameRate);
        CheckRange(id, "H.264 bitrate", h264.BitrateKbps, CameraMedia.MinBitrateKbps, CameraMedia.MaxBitrateKbps);
        CheckRange(id, "H.264 GOV length", h264.GovLength, CameraMedia.MinGovLength, CameraMedia.MaxGovLength);

        var imaging = camera.Imaging;
        Require(new[] { imaging.Brightness, imaging.Contrast, imaging.ColorSaturation }.All(v => v is >= 0 and <= 100),
            $"Camera {id}: brightness, contrast and saturation must be within 0..100.");

        foreach (var user in camera.Users)
            Require(user.Username.Length > 0, $"Camera {id}: a user has no name.");
        RequireUnique(camera.Users.Select(u => u.Username), $"Camera {id}: user name");

        if (camera.Kind == CameraKind.Ptz)
        {
            Require(camera.Ptz != null, $"Camera {id} is a PTZ camera without PTZ settings.");
            CheckPtz(id, camera.Ptz!);
        }
    }

    private static void CheckPtz(string id, PtzModel ptz)
    {
        Require(ptz.PanMin < ptz.PanMax && ptz.PanMin >= -360 && ptz.PanMax <= 360, $"Camera {id}: pan limits must satisfy -360 <= min < max <= 360.");
        Require(ptz.TiltMin < ptz.TiltMax && ptz.TiltMin >= -90 && ptz.TiltMax <= 90, $"Camera {id}: tilt limits must satisfy -90 <= min < max <= 90.");
        Require(ptz.FovTele is >= 1 and <= 170 && ptz.FovWide is >= 1 and <= 170 && ptz.FovTele <= ptz.FovWide,
            $"Camera {id}: zoom fields of view must be within 1..170, tele no wider than wide.");
        Require(ptz.PanSpeed > 0 && ptz.TiltSpeed > 0 && ptz.ZoomSpeed > 0, $"Camera {id}: PTZ speeds must be above 0.");
    }

    private static void CheckVideoSize(string id, string stream, int width, int height)
    {
        Require(width is >= MinVideoSide and <= MaxVideoSide && height is >= MinVideoSide and <= MaxVideoSide && width % 8 == 0 && height % 8 == 0,
            $"Camera {id}: {stream} size {width}x{height} must be multiples of 8 within {MinVideoSide}..{MaxVideoSide}.");
    }

    private static void CheckRange(string id, string what, int value, int min, int max) =>
        Require(value >= min && value <= max, $"Camera {id}: {what} {value} is outside {min}..{max}.");

    private static void RequireUnique(IEnumerable<string> values, string what)
    {
        var duplicate = values.GroupBy(v => v, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        Require(duplicate == null, $"{what} '{duplicate?.Key}' is used more than once.");
    }

    private static bool IsFinite(Vec3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static bool IsFinite(GroundPoint p) => float.IsFinite(p.X) && float.IsFinite(p.Z);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidWorldException(message);
    }
}
