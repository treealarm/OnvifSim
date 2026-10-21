using System.Numerics;
using OnvifSim.Geometry;
using OnvifSim.Ptz;
using OnvifSim.Rendering;
using OnvifSim.World;

namespace OnvifSim.Streaming;

// Renders one camera's frames and remembers the last one. Rendering is deterministic, so while the scene,
// the camera pose, the image adjustments and the size stay the same, the previous frame is exactly what a
// new render would produce: a camera nobody moves costs a comparison per frame instead of a render (and,
// for JPEG, an encode). Walkers are the exception: they move, so a camera that sees any draws every
// frame anew; so are other cameras' PTZ heads while they turn, when the picture shows cameras. The JPEG
// stream and the snapshots of a camera share one instance; its H.264 stream has one of its own, as its
// size is set separately. heads says where every PTZ head is now; without it, heads are where the world
// stores them.
public sealed class CachingFrameRenderer(TimeProvider time, Func<CameraModel, PtzState>? heads = null)
{
    // RenderScene is compared by reference: the world store builds a new one for every change.
    // WalkTime is the moment walkers are drawn at, 0 when the camera sees none; HeadsTime the moment
    // other cameras are drawn at, 0 while none of their heads moves.
    private readonly record struct FrameKey(
        RenderScene Scene, CameraPose Pose, float Brightness, float Contrast, float ColorSaturation, bool AngleGrid, int Width, int Height,
        long WalkTime, long HeadsTime);

    private readonly Lock _gate = new();
    private readonly SoftwareRasterizer _rasterizer = new();
    private Frame? _frame;
    private FrameKey? _frameKey;
    private (FrameKey Frame, int Quality)? _jpegKey;
    private byte[] _jpeg = [];

    // How many frames were actually rendered, for tests and diagnostics.
    public long RenderCount { get; private set; }

    // The camera's JPEG at its video settings. ptz is where the camera's head points now, which may be
    // ahead of what the world stores.
    public byte[] Render(SceneState.Snapshot snapshot, CameraModel camera, PtzPosition ptz)
    {
        var video = camera.Video;
        lock (_gate)
        {
            var key = Draw(snapshot, camera, ptz, video.Width, video.Height, time.GetUtcNow());
            if (_jpegKey != (key, video.Quality))
            {
                _jpeg = JpegEncoder.Encode(_frame!, video.Quality);
                _jpegKey = (key, video.Quality);
            }
            return _jpeg;
        }
    }

    // The camera's picture as RGBA. The frame is this renderer's own buffer and changes on the next call,
    // so only a renderer with a single user may hand it out.
    public Frame RenderFrame(SceneState.Snapshot snapshot, CameraModel camera, PtzPosition ptz, int width, int height) =>
        RenderFrameAt(snapshot, camera, ptz, width, height, time.GetUtcNow());

    // The picture as it was at a moment, for a replay of the camera's recording: walkers stand where
    // they stood then. ptz is where the head pointed then, and so should the heads delegate say.
    public Frame RenderFrameAt(SceneState.Snapshot snapshot, CameraModel camera, PtzPosition ptz, int width, int height, DateTimeOffset at)
    {
        lock (_gate)
        {
            Draw(snapshot, camera, ptz, width, height, at);
            return _frame!;
        }
    }

    private FrameKey Draw(SceneState.Snapshot snapshot, CameraModel camera, PtzPosition ptz, int width, int height, DateTimeOffset now)
    {
        var imaging = camera.Imaging;
        var scene = snapshot.Scene;
        var pose = CameraPose.From(camera, scene.Terrain, ptz);
        var walkers = WalkersInView(snapshot.World.Walkers, scene, new ViewFrustum(pose, (float)width / height, scene.FogEnd), now);
        bool headsMoving = CameraMarkers.Any(scene) && snapshot.World.Cameras.Any(c =>
            c.Kind == CameraKind.Ptz && c.Ptz != null && !string.Equals(c.Id, camera.Id, StringComparison.OrdinalIgnoreCase) && IsMoving(HeadOf(c)));
        var key = new FrameKey(scene, pose, imaging.Brightness, imaging.Contrast, imaging.ColorSaturation, camera.AngleGrid, width, height,
            walkers.Count == 0 ? 0 : now.ToUnixTimeMilliseconds(), headsMoving ? now.ToUnixTimeMilliseconds() : 0);
        if (key == _frameKey) return key;
        if (_frame == null || _frame.Width != width || _frame.Height != height)
            _frame = new Frame(width, height);
        var moving = walkers.Select(w => scene.Light(SceneBuilder.WalkerChunk(w.Walker, w.Pose, scene.Terrain))).ToList();
        moving.AddRange(CameraMarkers.Build(scene, snapshot.World, camera.Id, c => HeadOf(c).Position));
        _rasterizer.Render(scene, moving, key.Pose, _frame);
        ImageAdjustments.Apply(_frame, imaging);
        if (camera.AngleGrid) AngleGrid.Draw(_frame, camera, key.Pose, ptz);
        _frameKey = key;
        RenderCount++;
        return key;
    }

    private static bool IsMoving(PtzState state) => state.PanTiltMoving || state.ZoomMoving;

    private PtzState HeadOf(CameraModel camera) => heads?.Invoke(camera) ?? new PtzState(camera.Ptz!.Position, false, false);

    // The walkers the camera can see now: only they make its picture change from one moment to the next.
    private static List<(WalkerModel Walker, WalkerPose Pose)> WalkersInView(
        List<WalkerModel> walkers, RenderScene scene, ViewFrustum frustum, DateTimeOffset now)
    {
        var inView = new List<(WalkerModel, WalkerPose)>();
        foreach (var walker in walkers)
        {
            var pose = WalkerMotion.PoseAt(walker, now);
            // A sphere round the whole figure, swinging arms included.
            var centre = new Vector3(pose.X, scene.Terrain.HeightAt(pose.X, pose.Z) + walker.Height / 2, pose.Z);
            if (frustum.Sees(centre, walker.Height * 0.6f)) inView.Add((walker, pose));
        }
        return inView;
    }
}
