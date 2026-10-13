using System.Diagnostics;
using OnvifSim.Rendering;
using OnvifSim.Streaming;
using OnvifSim.World;

namespace OnvifSim;

// `OnvifSim --bench`: how long one frame takes per camera, rendering and JPEG separately. Used to pick
// default resolutions and frame rates.
public static class Bench
{
    public static void Run(WorldModel world)
    {
        var sw = Stopwatch.StartNew();
        var scene = RenderScene.Build(world);
        int tris = scene.Chunks.Sum(c => c.Indices.Length / 3);
        Console.WriteLine($"Scene: {scene.Chunks.Count} chunks, {tris} triangles, built in {sw.ElapsedMilliseconds} ms");
        var renderer = new SoftwareRasterizer();
        foreach (var cam in world.Cameras)
            foreach (var (w, h) in new[] { (640, 360), (1280, 720), (1920, 1080) })
            {
                var frame = new Frame(w, h);
                var pose = CameraPose.From(cam, scene.Terrain);
                for (int i = 0; i < 5; i++) renderer.Render(scene, pose, frame);
                const int n = 30;
                sw.Restart();
                for (int i = 0; i < n; i++) renderer.Render(scene, pose, frame);
                double render = sw.Elapsed.TotalMilliseconds / n;
                sw.Restart();
                int size = 0;
                for (int i = 0; i < n; i++) size = JpegEncoder.Encode(frame, cam.Video.Quality).Length;
                double jpeg = sw.Elapsed.TotalMilliseconds / n;
                Console.WriteLine($"{cam.Id,-6} {w}x{h,-5} render {render,6:F1} ms  jpeg {jpeg,6:F1} ms  {size / 1024,4} KiB");
            }
    }
}
