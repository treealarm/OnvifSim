using System.Net;
using System.Text.RegularExpressions;
using OnvifSim.Geometry;
using OnvifSim.Onvif;
using OnvifSim.Onvif.Services;
using OnvifSim.World;

namespace OnvifSim.Editor;

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
    private const int MaxStreamPathLength = 128;

    // Camera ids and device tokens stay URL-safe: they are RTSP paths, and appear in editor API URLs.
    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex TokenPattern();

    // Profile_1 ... Profile_8, and the legacy two.
    [GeneratedRegex("^Profile_[0-9]{1,3}$")]
    private static partial Regex StreamTokenPattern();

    // Up to four URL-safe path segments, e.g. cam1 or gate/lpr/main.
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,32}(/[A-Za-z0-9_.-]{1,32}){0,3}$")]
    private static partial Regex StreamPathPattern();

    public static void Check(WorldModel world, int onvifBasePort, int defaultRtspPort = 8554)
    {
        CheckGeo(world.Geo);
        CheckTerrain(world.Terrain);
        CheckEnvironment(world.Environment);
        foreach (var obj in world.Objects) CheckObject(obj);
        RequireUnique(world.Objects.Select(o => o.Id), "Object id");
        foreach (var road in world.Roads) CheckRoad(road);
        RequireUnique(world.Roads.Select(r => r.Id), "Road id");
        foreach (var walker in world.Walkers) CheckWalker(walker);
        RequireUnique(world.Walkers.Select(w => w.Id), "Walker id");
        foreach (var vehicle in world.Vehicles) CheckVehicle(vehicle);
        RequireUnique(world.Vehicles.Select(v => v.Id), "Vehicle id");
        foreach (var camera in world.Cameras)
        {
            CheckCamera(camera);
            if (!string.IsNullOrEmpty(camera.MountedOnVehicleId))
                Require(world.Vehicles.Any(v => string.Equals(v.Id, camera.MountedOnVehicleId, StringComparison.OrdinalIgnoreCase)),
                    $"Camera {camera.Id}: no vehicle with id '{camera.MountedOnVehicleId}'.");
        }
        RequireUnique(world.Cameras.Select(c => c.Id), "Camera id");
        CheckStreams(world);
        CheckWires(world);
        CheckEndpoints(world, onvifBasePort, defaultRtspPort);
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

    private static void CheckGeo(GeoModel geo)
    {
        Require(double.IsFinite(geo.Latitude) && geo.Latitude is >= -90 and <= 90,
            $"Latitude {geo.Latitude} is outside -90..90.");
        Require(double.IsFinite(geo.Longitude) && geo.Longitude is >= -180 and <= 180,
            $"Longitude {geo.Longitude} is outside -180..180.");
        Require(float.IsFinite(geo.Elevation), "The world's elevation must be a finite number.");
        Require(float.IsFinite(geo.North) && geo.North is >= 0 and <= 360,
            $"North {geo.North} is outside 0..360.");
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
        if (obj.Type == "car") CheckPlate($"Object {obj.Id}", obj.Plate);
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

    private static void CheckVehicle(VehicleModel vehicle)
    {
        Require(vehicle.Id.Length > 0, "A vehicle has no id.");
        Require(vehicle.Route.Count >= 1, $"Vehicle {vehicle.Id} needs at least one point to stand on.");
        Require(vehicle.Route.All(IsFinite), $"Vehicle {vehicle.Id}: route points must be finite.");
        Require(vehicle.Speed is >= 0 and <= 50, $"Vehicle {vehicle.Id}: speed {vehicle.Speed} m/s is outside 0..50.");
        Require(vehicle.Width is >= 0.5f and <= 5, $"Vehicle {vehicle.Id}: width {vehicle.Width} m is outside 0.5..5.");
        Require(vehicle.Height is >= 0.5f and <= 5, $"Vehicle {vehicle.Id}: height {vehicle.Height} m is outside 0.5..5.");
        Require(vehicle.Length is >= 1 and <= 15, $"Vehicle {vehicle.Id}: length {vehicle.Length} m is outside 1..15.");
        CheckPlate($"Vehicle {vehicle.Id}", vehicle.Plate);
    }

    // A licence plate is Latin letters, digits, spaces and hyphens, at most eight characters.
    private static void CheckPlate(string what, string? plate)
    {
        if (plate != null)
            Require(plate.Length <= 8 && plate.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-'),
                $"{what}: the plate must be at most 8 Latin letters, digits, spaces or hyphens.");
    }

    private static void CheckCamera(CameraModel camera)
    {
        string id = camera.Id;
        Require(TokenPattern().IsMatch(id), $"Camera id '{id}' must be 1..32 letters, digits, '-' or '_'.");
        CheckDevices(camera);
        Require(IsFinite(camera.Position) && float.IsFinite(camera.Heading) && float.IsFinite(camera.Pitch) && float.IsFinite(camera.Roll),
            $"Camera {id}: position and orientation must be finite.");
        Require(camera.HFov is >= 1 and <= 170, $"Camera {id}: field of view {camera.HFov} is outside 1..170.");
        Require(camera.OnvifPort is >= 0 and <= 65535, $"Camera {id}: ONVIF port {camera.OnvifPort} is outside 0..65535.");
        string? bindAddress = string.IsNullOrWhiteSpace(camera.BindAddress) ? null : camera.BindAddress.Trim();
        if (bindAddress != null)
        {
            Require(IPAddress.TryParse(bindAddress, out var address),
                $"Camera {id}: bind address '{bindAddress}' is not an IP address.");
            Require(!address!.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any),
                $"Camera {id}: leave the bind address empty to listen on every interface.");
            Require(camera.RtspPort != 0, $"Camera {id}: set an RTSP port when binding a specific address.");
        }
        Require(camera.RtspPort is >= 0 and <= 65535, $"Camera {id}: RTSP port {camera.RtspPort} is outside 0..65535.");
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

    // Relay outputs and digital inputs: tokens address them in URLs and in ONVIF requests, so they are
    // unique per camera and URL-safe; positions are metres offset from the camera.
    private static void CheckDevices(CameraModel camera)
    {
        string id = camera.Id;
        RequireUnique(camera.RelayOutputs.Select(r => r.Token), $"Camera {id}: relay output token");
        RequireUnique(camera.DigitalInputs.Select(i => i.Token), $"Camera {id}: digital input token");
        foreach (var relay in camera.RelayOutputs)
        {
            Require(TokenPattern().IsMatch(relay.Token),
                $"Camera {id}: relay output token '{relay.Token}' must be 1..32 letters, digits, '-' or '_'.");
            Require(relay.Name.Length <= 64, $"Camera {id}: relay output {relay.Token}: the name must be at most 64 characters.");
            Require(IsFinite(relay.Position), $"Camera {id}: relay output {relay.Token}: the position must be finite.");
            Require(relay.Mode == RelayModeModel.Bistable || relay.DelaySeconds is >= 0.05f and <= 3600,
                $"Camera {id}: relay output {relay.Token}: a monostable delay must be within 0.05..3600 s.");
        }
        foreach (var input in camera.DigitalInputs)
        {
            Require(TokenPattern().IsMatch(input.Token),
                $"Camera {id}: digital input token '{input.Token}' must be 1..32 letters, digits, '-' or '_'.");
            Require(input.Name.Length <= 64, $"Camera {id}: digital input {input.Token}: the name must be at most 64 characters.");
            Require(IsFinite(input.Position), $"Camera {id}: digital input {input.Token}: the position must be finite.");
        }
    }

    // A wire joins a relay output of one camera to a digital input of any camera: both must exist, and
    // an input takes at most one wire.
    private static void CheckWires(WorldModel world)
    {
        var driven = new HashSet<(string Camera, string Token)>();
        foreach (var wire in world.Wires)
        {
            string where = $"Wire {wire.RelayCameraId}/{wire.RelayToken} -> {wire.InputCameraId}/{wire.InputToken}";
            Require(DeviceIo.FindCamera(world, wire.RelayCameraId) != null, $"{where}: no camera '{wire.RelayCameraId}'.");
            Require(DeviceIo.FindRelay(world, wire.RelayCameraId, wire.RelayToken) != null,
                $"{where}: camera {wire.RelayCameraId} has no relay output '{wire.RelayToken}'.");
            Require(DeviceIo.FindCamera(world, wire.InputCameraId) != null, $"{where}: no camera '{wire.InputCameraId}'.");
            Require(DeviceIo.FindInput(world, wire.InputCameraId, wire.InputToken) != null,
                $"{where}: camera {wire.InputCameraId} has no digital input '{wire.InputToken}'.");
            Require(driven.Add((wire.InputCameraId.ToLowerInvariant(), wire.InputToken.ToLowerInvariant())),
                $"{where}: that digital input is already driven by another wire.");
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

    private static void CheckStreams(WorldModel world)
    {
        // RTSP paths are the whole address of a stream: the first camera to claim one owns it.
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var camera in world.Cameras)
        {
            var streams = CameraStreamsModel.Effective(camera);
            Require(streams.Count is >= 1 and <= CameraStreamsModel.MaxStreams,
                $"Camera {camera.Id}: a camera needs 1..{CameraStreamsModel.MaxStreams} streams, got {streams.Count}.");
            RequireUnique(streams.Select(s => s.Token), $"Camera {camera.Id}: stream token");
            foreach (var stream in streams)
            {
                CheckStream(camera, stream);
                ClaimPath(paths, stream.Path, $"Camera {camera.Id}: stream {stream.Token}");
            }
            ClaimPath(paths, CameraStreamsModel.ReplayPath(camera), $"Camera {camera.Id}: replay");
        }
    }

    private static void CheckStream(CameraModel camera, StreamModel stream)
    {
        string where = $"Camera {camera.Id}, stream '{stream.Token}'";
        Require(StreamTokenPattern().IsMatch(stream.Token), $"{where}: the token must look like Profile_1.");
        Require(stream.Name.Length is >= 1 and <= 64, $"{where}: the name must be 1..64 characters.");
        Require(stream.Path.Length <= MaxStreamPathLength && StreamPathPattern().IsMatch(stream.Path),
            $"{where}: RTSP path '{stream.Path}' must be up to four URL-safe segments, at most {MaxStreamPathLength} characters.");
        string last = stream.Path.Split('/')[^1];
        Require(!last.Equals("track1", StringComparison.OrdinalIgnoreCase) && !last.Equals("track2", StringComparison.OrdinalIgnoreCase),
            $"{where}: a stream path cannot end in track1 or track2, which name RTSP tracks.");
        switch (stream.Codec)
        {
            case StreamCodec.Jpeg:
                Require(stream.Video != null, $"{where}: a JPEG stream needs Video settings.");
                CheckVideoSize(camera.Id, $"{where} (JPEG)", stream.Video!.Width, stream.Video.Height);
                CheckRange(camera.Id, $"{where} JPEG frame rate", stream.Video.Fps, CameraMedia.MinFrameRate, CameraMedia.MaxFrameRate);
                CheckRange(camera.Id, $"{where} JPEG quality", stream.Video.Quality, CameraMedia.MinQuality, CameraMedia.MaxQuality);
                break;
            case StreamCodec.H264:
                Require(stream.H264 != null, $"{where}: an H.264 stream needs H264 settings.");
                CheckVideoSize(camera.Id, $"{where} (H.264)", stream.H264!.Width, stream.H264.Height);
                CheckRange(camera.Id, $"{where} H.264 frame rate", stream.H264.Fps, CameraMedia.MinFrameRate, CameraMedia.MaxFrameRate);
                CheckRange(camera.Id, $"{where} H.264 bitrate", stream.H264.BitrateKbps, CameraMedia.MinBitrateKbps, CameraMedia.MaxBitrateKbps);
                CheckRange(camera.Id, $"{where} H.264 GOV length", stream.H264.GovLength, CameraMedia.MinGovLength, CameraMedia.MaxGovLength);
                break;
            default:
                throw new InvalidWorldException($"{where}: unknown codec {stream.Codec}.");
        }
    }

    private static void ClaimPath(Dictionary<string, string> paths, string path, string owner)
    {
        if (paths.TryGetValue(path, out var existing))
            throw new InvalidWorldException($"{owner}: RTSP path '{path}' is already used by {existing}.");
        paths[path] = owner;
    }

    // One ONVIF listener and one RTSP listener may share a port only when both bind the same address:
    // the wildcard occupies the port, so a specific address cannot be added beside it.
    private static void CheckEndpoints(WorldModel world, int onvifBasePort, int defaultRtspPort)
    {
        const string wildcard = "*";
        string AddressOf(CameraModel camera) =>
            string.IsNullOrWhiteSpace(camera.BindAddress) ? wildcard : IPAddress.Parse(camera.BindAddress.Trim()).ToString();

        var onvif = world.Cameras
            .Select(camera => (Address: AddressOf(camera), Port: OnvifHost.RequestedPort(world, camera, onvifBasePort), camera.Id))
            .Where(endpoint => endpoint.Port != 0)
            .ToList();
        foreach (var group in onvif.GroupBy(endpoint => (endpoint.Address, endpoint.Port)).Where(group => group.Count() > 1))
            throw new InvalidWorldException(
                $"ONVIF endpoint {Describe(group.Key.Address, group.Key.Port)} is used by cameras {string.Join(", ", group.Select(e => e.Id))}.");
        foreach (var group in onvif.GroupBy(endpoint => endpoint.Port))
        {
            var addresses = group.Select(endpoint => endpoint.Address).Distinct().ToList();
            Require(addresses.Count == 1 || !addresses.Contains(wildcard),
                $"ONVIF port {group.Key} is asked for both on every interface and on a specific address.");
        }

        var rtsp = world.Cameras
            .Select(camera => (Address: AddressOf(camera), Port: CameraStreamsModel.EffectiveRtspPort(camera, defaultRtspPort)))
            .ToList();
        foreach (var group in rtsp.GroupBy(endpoint => endpoint.Port))
        {
            var addresses = group.Select(endpoint => endpoint.Address).Distinct().ToList();
            Require(addresses.Count == 1 || !addresses.Contains(wildcard),
                $"RTSP port {group.Key} is asked for both on every interface and on a specific address.");
        }
    }

    private static string Describe(string address, int port) => address == "*" ? $"*:{port}" : $"{address}:{port}";

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
