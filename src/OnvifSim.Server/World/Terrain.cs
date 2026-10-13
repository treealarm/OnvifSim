namespace OnvifSim.World;

// Read-only view over a TerrainModel heightmap: sampling and normals in world coordinates.
public sealed class Terrain
{
    private readonly float[] _h;
    public int Resolution { get; }
    public float Size { get; }
    public float CellSize { get; }
    private float Half => Size / 2;

    public Terrain(TerrainModel model)
    {
        Resolution = Math.Max(1, model.Resolution);
        Size = model.Size;
        CellSize = Size / Resolution;
        int n = (Resolution + 1) * (Resolution + 1);
        _h = model.Heights.Length == n ? model.Heights : new float[n];
    }

    public float this[int ix, int iz] => _h[Math.Clamp(iz, 0, Resolution) * (Resolution + 1) + Math.Clamp(ix, 0, Resolution)];

    public float VertexX(int ix) => ix * CellSize - Half;
    public float VertexZ(int iz) => iz * CellSize - Half;

    // Height of the surface under (x, z), bilinear over the cell; outside the terrain it clamps to the edge.
    public float HeightAt(float x, float z)
    {
        float fx = Math.Clamp((x + Half) / CellSize, 0, Resolution);
        float fz = Math.Clamp((z + Half) / CellSize, 0, Resolution);
        int ix = Math.Min((int)fx, Resolution - 1), iz = Math.Min((int)fz, Resolution - 1);
        float tx = fx - ix, tz = fz - iz;
        float h0 = this[ix, iz] + (this[ix + 1, iz] - this[ix, iz]) * tx;
        float h1 = this[ix, iz + 1] + (this[ix + 1, iz + 1] - this[ix, iz + 1]) * tx;
        return h0 + (h1 - h0) * tz;
    }

    public System.Numerics.Vector3 NormalAt(int ix, int iz)
    {
        float dx = this[ix + 1, iz] - this[ix - 1, iz];
        float dz = this[ix, iz + 1] - this[ix, iz - 1];
        return System.Numerics.Vector3.Normalize(new(-dx, 2 * CellSize, -dz));
    }
}
