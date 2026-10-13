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
    public VideoModel Video { get; set; } = new();
    public PtzModel? Ptz { get; set; }
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

public sealed class PtzModel
{
    // Physical limits in degrees relative to the mount orientation.
    public float PanMin { get; set; } = -180;
    public float PanMax { get; set; } = 180;
    public float TiltMin { get; set; } = -90;
    public float TiltMax { get; set; } = 20;
    public float FovWide { get; set; } = 60;
    public float FovTele { get; set; } = 3;
    // Current position, persisted so a restart keeps where the camera looked.
    public float Pan { get; set; }
    public float Tilt { get; set; }
    public float Zoom { get; set; }
}

public readonly record struct Vec3(float X, float Y, float Z);
