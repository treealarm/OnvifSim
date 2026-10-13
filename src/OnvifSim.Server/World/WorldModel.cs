using System.Text.Json.Serialization;

namespace OnvifSim.World;

// The whole simulated world as stored in world.json. Coordinates follow three.js so the editor needs
// no conversion: X east, Y up, Z south (north is -Z), metres. Object and camera Y is the height above
// the terrain surface, not an absolute altitude, so sculpting the ground carries everything with it.
public sealed class WorldModel
{
    public int Version { get; set; } = 1;
    public TerrainModel Terrain { get; set; } = new();
    public EnvironmentModel Environment { get; set; } = new();
    public List<WorldObject> Objects { get; set; } = [];
    public List<RoadModel> Roads { get; set; } = [];
    public List<WalkerModel> Walkers { get; set; } = [];
    public List<CameraModel> Cameras { get; set; } = [];
}

public sealed class TerrainModel
{
    // Side of the square terrain in metres, centred on the origin.
    public float Size { get; set; } = 400;
    // Cells per side; the heightmap holds (Resolution + 1)^2 samples, row-major from -X,-Z.
    public int Resolution { get; set; } = 128;
    [JsonConverter(typeof(FloatArrayBase64Converter))]
    public float[] Heights { get; set; } = [];
}

public sealed class EnvironmentModel
{
    // Compass direction the sunlight comes from (degrees clockwise from north) and its elevation.
    public float SunAzimuth { get; set; } = 135;
    public float SunElevation { get; set; } = 45;
    public string SkyZenith { get; set; } = "#4a7fc1";
    public string SkyHorizon { get; set; } = "#c9dcef";
    // Distances (m) where fog starts and where it fully hides geometry.
    public float FogStart { get; set; } = 150;
    public float FogEnd { get; set; } = 600;
}

public sealed class WorldObject
{
    public string Id { get; set; } = "";
    // tree.pine, tree.oak, house, rock, box
    public string Type { get; set; } = "";
    public Vec3 Position { get; set; }
    // Rotation around the vertical axis, degrees clockwise seen from above.
    public float RotationY { get; set; }
    // Overall size; its meaning depends on the type (for a tree Y is the height, for a house the box).
    public Vec3 Scale { get; set; } = new(1, 1, 1);
    public string? Color { get; set; }
    public string? Color2 { get; set; }
    public int Seed { get; set; }
}

// A road or a footpath: a strip of the given width laid on the ground along a line of points.
public sealed class RoadModel
{
    public string Id { get; set; } = "";
    public List<GroundPoint> Points { get; set; } = [];
    public float Width { get; set; } = 4;
    public string? Color { get; set; }
}

// A person walking a route over and over, at a steady pace. Where they are follows from the clock, so
// every camera and every client sees them at the same spot.
public sealed class WalkerModel
{
    public string Id { get; set; } = "";
    // One point is a person standing still.
    public List<GroundPoint> Route { get; set; } = [];
    // Metres per second; 1.4 is an ordinary walk.
    public float Speed { get; set; } = 1.4f;
    // Back from the last point to the first and round again, or there and back the same way.
    public WalkerReturn Return { get; set; } = WalkerReturn.Loop;
    public float Height { get; set; } = 1.75f;
    // Clothes: the top and the trousers.
    public string? Color { get; set; }
    public string? Color2 { get; set; }
}

public enum WalkerReturn { Loop, Reverse }

public enum CameraKind { Fixed, Ptz }

public sealed class CameraModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public CameraKind Kind { get; set; }
    public Vec3 Position { get; set; }
    // Mount orientation: compass heading (deg clockwise from north), pitch (deg, up positive), roll.
    // For a PTZ camera these are the orientation at pan = tilt = 0.
    public float Heading { get; set; }
    public float Pitch { get; set; }
    public float Roll { get; set; }
    // Horizontal field of view of a fixed camera; a PTZ camera takes it from Ptz.FovWide/FovTele.
    public float HFov { get; set; } = 70;
    // Draws a pan/tilt degree grid over the picture (relative to the mount), for checking where the
    // camera points.
    public bool AngleGrid { get; set; }
    public VideoModel Video { get; set; } = new();
    // The H.264 stream, served when ffmpeg is available; its settings are independent of the JPEG ones.
    public H264Model H264 { get; set; } = new();
    public ImagingModel Imaging { get; set; } = new();
    public PtzModel? Ptz { get; set; }
    // Port of the camera's ONVIF service; 0 means automatic: 8001 for the first camera, 8002 for the
    // second and so on (--onvif-base-port moves the start).
    public int OnvifPort { get; set; }
    // Accounts the camera accepts on RTSP (and on ONVIF). No users: the camera asks for no credentials.
    public List<UserModel> Users { get; set; } = [];
}

public sealed class UserModel
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class VideoModel
{
    public int Width { get; set; } = 640;
    public int Height { get; set; } = 360;
    // 10 fps: ffmpeg-based players (OnvifLib.Gui, NVRs) probe ~20 frames before the first picture,
    // so lower rates make them start slowly; one camera still costs only ~5% of a core.
    public int Fps { get; set; } = 10;
    // JPEG quality 1..100.
    public int Quality { get; set; } = 70;
}

// A record: two settings are the same when all their values are, which is how a feed notices a change.
public sealed record H264Model
{
    public int Width { get; set; } = 640;
    public int Height { get; set; } = 360;
    public int Fps { get; set; } = 10;
    public int BitrateKbps { get; set; } = 1024;
    // Frames from one key frame to the next; a client joining the stream waits for the next one.
    public int GovLength { get; set; } = 10;
}

// Image adjustments of the ONVIF Imaging service, 0..100 with 50 leaving the picture as rendered.
public sealed class ImagingModel
{
    public float Brightness { get; set; } = 50;
    public float Contrast { get; set; } = 50;
    public float ColorSaturation { get; set; } = 50;
}

public sealed class PtzModel
{
    // Physical limits in degrees relative to the mount orientation. A pan range of 360 degrees or more
    // is an endless pan: the head turns round and round instead of stopping at a limit.
    public float PanMin { get; set; } = -180;
    public float PanMax { get; set; } = 180;
    public float TiltMin { get; set; } = -90;
    public float TiltMax { get; set; } = 20;
    public float FovWide { get; set; } = 60;
    public float FovTele { get; set; } = 3;
    // Top speeds: degrees per second for pan and tilt, the whole zoom range (0..1) per second for zoom.
    public float PanSpeed { get; set; } = 90;
    public float TiltSpeed { get; set; } = 60;
    public float ZoomSpeed { get; set; } = 0.5f;
    // Current position, persisted when a move ends so a restart keeps where the camera looked.
    public float Pan { get; set; }
    public float Tilt { get; set; }
    public float Zoom { get; set; }
    public PtzPosition Home { get; set; }
    public List<PtzPresetModel> Presets { get; set; } = [];

    [JsonIgnore]
    public bool EndlessPan => PanMax - PanMin >= 360;

    [JsonIgnore]
    public PtzPosition Position
    {
        get => new(Pan, Tilt, Zoom);
        set => (Pan, Tilt, Zoom) = (value.Pan, value.Tilt, value.Zoom);
    }
}

public sealed class PtzPresetModel
{
    public string Token { get; set; } = "";
    public string Name { get; set; } = "";
    public PtzPosition Position { get; set; }
}

// Pan and tilt in degrees relative to the mount, zoom 0 (wide) .. 1 (tele).
public readonly record struct PtzPosition(float Pan, float Tilt, float Zoom);

public readonly record struct Vec3(float X, float Y, float Z);

// A spot on the ground: X east, Z south; the height comes from the terrain.
public readonly record struct GroundPoint(float X, float Z);
