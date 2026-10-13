using OnvifSim.Rendering;
using OnvifSim.World;

namespace OnvifSim.Streaming;

// Renders one camera's frames and remembers the last one. Rendering is deterministic, so while the scene,
// the camera pose, the image adjustments and the size stay the same, the previous frame is exactly what a
// new render would produce: a camera nobody moves costs a comparison per frame instead of a render (and,
// for JPEG, an encode). The JPEG stream and the snapshots of a camera share one instance; its H.264
// stream has one of its own, as its size is set separately.
public sealed class CachingFrameRenderer
{
    // RenderScene is compared by reference: the world store builds a new one for every change.
    private readonly record struct FrameKey(
        RenderScene Scene, CameraPose Pose, float Brightness, float Contrast, float ColorSaturation, int Width, int Height);

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
            var key = Draw(snapshot, camera, ptz, video.Width, video.Height);
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
    public Frame RenderFrame(SceneState.Snapshot snapshot, CameraModel camera, PtzPosition ptz, int width, int height)
    {
        lock (_gate)
        {
            Draw(snapshot, camera, ptz, width, height);
            return _frame!;
        }
    }

    private FrameKey Draw(SceneState.Snapshot snapshot, CameraModel camera, PtzPosition ptz, int width, int height)
    {
        var imaging = camera.Imaging;
        var key = new FrameKey(snapshot.Scene, CameraPose.From(camera, snapshot.Scene.Terrain, ptz),
            imaging.Brightness, imaging.Contrast, imaging.ColorSaturation, width, height);
        if (key == _frameKey) return key;
        if (_frame == null || _frame.Width != width || _frame.Height != height)
            _frame = new Frame(width, height);
        _rasterizer.Render(snapshot.Scene, key.Pose, _frame);
        ImageAdjustments.Apply(_frame, imaging);
        _frameKey = key;
        RenderCount++;
        return key;
    }
}
