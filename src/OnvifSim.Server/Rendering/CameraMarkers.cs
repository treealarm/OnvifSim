using System.Numerics;
using OnvifSim.Geometry;
using OnvifSim.World;

namespace OnvifSim.Rendering;

// The other cameras as one camera's picture shows them, built for every frame since PTZ heads move:
// their bodies (ShowCameras) and where they look (ShowFrustums), as a wire frame from the eye to the
// ground with a grid over the patch of ground covered. A camera never draws itself.
public static class CameraMarkers
{
    private const float WireAlpha = 0.75f;
    // Above the ground and the roads (RoadLift is 0.12 m), so the grid is not lost under either.
    private const float WireLift = 0.2f;

    public static bool Any(RenderScene scene) => scene.ShowCameras || scene.ShowFrustums;

    // headOf gives where a PTZ camera's head is now; at is the moment the picture is drawn at, so a
    // camera on a car stands where the car is.
    public static List<RenderScene.Chunk> Build(RenderScene scene, WorldModel world, string viewerId, Func<CameraModel, PtzPosition> headOf, DateTimeOffset at)
    {
        var chunks = new List<RenderScene.Chunk>();
        if (!Any(scene)) return chunks;
        foreach (var camera in world.Cameras)
        {
            if (string.Equals(camera.Id, viewerId, StringComparison.OrdinalIgnoreCase)) continue;
            bool ptz = camera.Kind == CameraKind.Ptz && camera.Ptz != null;
            var head = ptz ? headOf(camera) : default;
            var pose = CameraPose.From(camera, world, at, head);
            var color = CameraMesh.ColorOf(camera);
            var baseHeading = BaseHeading(camera, world, at);
            if (scene.ShowCameras)
            {
                var look = ptz ? CameraPose.HeadOrientation(camera, head.Pan, head.Tilt, baseHeading) : CameraPose.Mount(camera, baseHeading);
                // A camera on a car hangs on the car, not on a mast from the ground.
                var ground = string.IsNullOrEmpty(camera.MountedOnVehicleId) ? scene.Terrain.HeightAt(pose.Position.X, pose.Position.Z) : pose.Position.Y;
                var mesh = CameraMesh.Build(color, pose.Position, ground, look);
                var (center, radius) = mesh.BoundingSphere();
                chunks.Add(scene.Light(new SceneChunk { Name = camera.Id, Mesh = mesh, Center = center, Radius = radius }));
            }
            if (scene.ShowFrustums)
            {
                var grid = ViewFootprint.Cast(pose, (float)camera.Video.Width / camera.Video.Height, scene.Terrain, scene.FogEnd, scene.FrustumLength);
                chunks.Add(Frustum(grid, color));
            }
        }
        return chunks;
    }

    private static float BaseHeading(CameraModel camera, WorldModel world, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(camera.MountedOnVehicleId)) return 0;
        var vehicle = world.Vehicles.FirstOrDefault(v => string.Equals(v.Id, camera.MountedOnVehicleId, StringComparison.OrdinalIgnoreCase));
        return vehicle == null ? 0 : VehicleMotion.PoseAt(vehicle, at).Heading;
    }

    // A wire frame, cheap to draw: the four edges from the eye, the rim of the picture where it ends,
    // and the grid of rays over the ground they reach, which is the patch of ground the camera covers.
    private static RenderScene.Chunk Frustum(ViewFootprint.Grid grid, Vector3 color)
    {
        int columns = ViewFootprint.Columns, rows = ViewFootprint.Rows;
        var wire = new Mesh();
        for (int i = 0; i < columns; i++)
            for (int j = 0; j < rows; j++)
                wire.AddVertex(grid.OnGround[i, j] ? grid.Points[i, j] + new Vector3(0, WireLift, 0) : grid.Points[i, j], Vector3.UnitY, color);
        int eye = wire.AddVertex(grid.Eye, Vector3.UnitY, color);
        int V(int i, int j) => i * rows + j;
        bool Ground(int i, int j) => grid.OnGround[i, j];

        var lines = new List<int>();
        foreach (var (i, j) in new[] { (0, 0), (columns - 1, 0), (columns - 1, rows - 1), (0, rows - 1) })
            lines.AddRange([eye, V(i, j)]);
        for (int i = 0; i < columns; i++)
            for (int j = 0; j < rows; j++)
            {
                // The rim always; inside it, only what lies on the ground.
                bool topOrBottom = j == 0 || j == rows - 1, leftOrRight = i == 0 || i == columns - 1;
                if (i + 1 < columns && (topOrBottom || Ground(i, j) && Ground(i + 1, j))) lines.AddRange([V(i, j), V(i + 1, j)]);
                if (j + 1 < rows && (leftOrRight || Ground(i, j) && Ground(i, j + 1))) lines.AddRange([V(i, j), V(i, j + 1)]);
            }
        return RenderScene.Unlit(wire, WireAlpha, [.. lines]);
    }
}
