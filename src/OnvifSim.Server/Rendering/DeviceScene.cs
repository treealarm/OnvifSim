using System.Numerics;
using OnvifSim.Geometry;
using OnvifSim.World;

namespace OnvifSim.Rendering;

// The lamps of the world: actuators a relay drives, standing where they were placed. A camera's own relay
// outputs and digital inputs are settings of the camera — a client reads and writes them over ONVIF — not
// things standing in the world, so nothing is drawn for them and nothing moves with a car.
public static class DeviceScene
{
    public static List<SceneChunk> Static(WorldModel world)
    {
        var terrain = new Terrain(world.Terrain);
        var chunks = new List<SceneChunk>();
        foreach (var lamp in world.Lamps)
            chunks.Add(Placed($"lamp/{lamp.Id}", DeviceMesh.Lamp(DeviceIo.LampLit(world, lamp.Id), lamp.Color),
                new Vector3(lamp.Position.X, terrain.HeightAt(lamp.Position.X, lamp.Position.Z) + lamp.Position.Y, lamp.Position.Z)));
        return chunks;
    }

    private static SceneChunk Placed(string name, Mesh mesh, Vector3 at)
    {
        var placed = new Mesh();
        placed.Append(mesh, Matrix4x4.CreateTranslation(at));
        var (center, radius) = placed.BoundingSphere();
        return new SceneChunk { Name = name, Mesh = placed, Center = center, Radius = radius };
    }
}
