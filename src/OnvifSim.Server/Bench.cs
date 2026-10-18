using System.Diagnostics;
using OnvifSim.Rendering;
using OnvifSim.Streaming;
using OnvifSim.World;

namespace OnvifSim;

// `OnvifSim --bench`: how long one frame takes per camera, rendering and JPEG separately, and what
// drawing the other cameras costs on top (their bodies, then their fields of view as well, each built
// for the frame as a stream builds them). Used to pick default resolutions and frame rates.
public static class Bench
{
    public static void Run(WorldModel world)
    {
        var sw = Stopwatch.StartNew();
        var env = world.Environment;
        var scene = RenderScene.Build(world);
        int tris = scene.Chunks.Sum(c => c.Indices.Length / 3);
        Console.WriteLine($"Scene: {scene.Chunks.Count} chunks, {tris} triangles, built in {sw.ElapsedMilliseconds} ms");
        (bool Cameras, bool Frustums)[] markers = [(false, false), (true, false), (true, true)];
        var scenes = markers.Select(m =>
        {
            world.Environment = new EnvironmentModel
            {
                SunAzimuth = env.SunAzimuth, SunElevation = env.SunElevation, SkyZenith = env.SkyZenith, SkyHorizon = env.SkyHorizon,
                FogStart = env.FogStart, FogEnd = env.FogEnd, FrustumLength = env.FrustumLength, ShowCameras = m.Cameras, ShowFrustums = m.Frustums,
            };
            return RenderScene.Build(world);
        }).ToList();
        world.Environment = env;
        var renderer = new SoftwareRasterizer();
        foreach (var cam in world.Cameras)
            foreach (var (w, h) in new[] { (640, 360), (1280, 720), (1920, 1080) })
            {
                var frame = new Frame(w, h);
                var pose = CameraPose.From(cam, scene.Terrain);
                const int n = 30;
                var times = scenes.Select(s =>
                {
                    List<RenderScene.Chunk> Moving() => CameraMarkers.Build(s, world, cam.Id, c => c.Ptz!.Position);
                    for (int i = 0; i < 5; i++) renderer.Render(s, Moving(), pose, frame);
                    sw.Restart();
                    for (int i = 0; i < n; i++) renderer.Render(s, Moving(), pose, frame);
                    return sw.Elapsed.TotalMilliseconds / n;
                }).ToList();
                sw.Restart();
                int size = 0;
                for (int i = 0; i < n; i++) size = JpegEncoder.Encode(frame, cam.Video.Quality).Length;
                double jpeg = sw.Elapsed.TotalMilliseconds / n;
                Console.WriteLine($"{cam.Id,-6} {w}x{h,-5} render {times[0],6:F1} ms  +cameras {times[1],6:F1} ms  +fields of view {times[2],6:F1} ms  jpeg {jpeg,6:F1} ms  {size / 1024,4} KiB");
            }
    }
}
