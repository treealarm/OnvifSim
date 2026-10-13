using OnvifSim.Rendering;
using OnvifSim.World;

namespace OnvifSim.Streaming;

// Renders one camera's frames to JPEG and remembers the last one. Rendering is deterministic, so while
// the scene, the camera pose and the video settings stay the same, the previous JPEG is exactly what a
// new render would produce: a camera nobody moves costs a comparison per frame instead of a render and
// an encode. The stream and the snapshot of a camera share one instance.
public sealed class CachingFrameRenderer
{
    // RenderScene is compared by reference: the world store builds a new one for every change.
    private readonly record struct FrameKey(RenderScene Scene, CameraPose Pose, int Width, int Height, int Quality);

    private readonly Lock _gate = new();
    private readonly SoftwareRasterizer _rasterizer = new();
    private Frame? _frame;
    private FrameKey? _lastKey;
    private byte[] _lastJpeg = [];

    // How many frames were actually rendered, for tests and diagnostics.
    public long RenderCount { get; private set; }

    public byte[] Render(SceneState.Snapshot snapshot, CameraModel camera)
    {
        var video = camera.Video;
        var key = new FrameKey(snapshot.Scene, CameraPose.From(camera, snapshot.Scene.Terrain), video.Width, video.Height, video.Quality);
        lock (_gate)
        {
            if (key == _lastKey) return _lastJpeg;
            if (_frame == null || _frame.Width != video.Width || _frame.Height != video.Height)
                _frame = new Frame(video.Width, video.Height);
            _rasterizer.Render(snapshot.Scene, key.Pose, _frame);
            _lastJpeg = JpegEncoder.Encode(_frame, video.Quality);
            _lastKey = key;
            RenderCount++;
            return _lastJpeg;
        }
    }
}
