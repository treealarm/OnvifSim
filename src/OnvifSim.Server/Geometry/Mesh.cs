using System.Numerics;

namespace OnvifSim.Geometry;

// An indexed triangle list with per-vertex normal and colour, counter-clockwise front faces
// (three.js convention). Used both for rendering and for the glTF the editor loads.
public sealed class Mesh
{
    public List<Vector3> Positions { get; } = [];
    public List<Vector3> Normals { get; } = [];
    public List<Vector3> Colors { get; } = [];
    public List<int> Indices { get; } = [];

    public int VertexCount => Positions.Count;
    public int TriangleCount => Indices.Count / 3;

    public int AddVertex(Vector3 position, Vector3 normal, Vector3 color)
    {
        Positions.Add(position);
        Normals.Add(normal);
        Colors.Add(color);
        return Positions.Count - 1;
    }

    public void AddTriangle(int a, int b, int c)
    {
        Indices.Add(a);
        Indices.Add(b);
        Indices.Add(c);
    }

    // Flat-shaded triangle: own vertices so the facet normal is not smoothed with its neighbours.
    public void AddFlatTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 color)
    {
        var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        int i = AddVertex(a, n, color);
        AddVertex(b, n, color);
        AddVertex(c, n, color);
        AddTriangle(i, i + 1, i + 2);
    }

    public void AddFlatQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 color)
    {
        AddFlatTriangle(a, b, c, color);
        AddFlatTriangle(a, c, d, color);
    }

    public void Append(Mesh other, Matrix4x4 transform)
    {
        int offset = Positions.Count;
        for (int i = 0; i < other.Positions.Count; i++)
        {
            Positions.Add(Vector3.Transform(other.Positions[i], transform));
            Normals.Add(Vector3.Normalize(Vector3.TransformNormal(other.Normals[i], transform)));
            Colors.Add(other.Colors[i]);
        }
        foreach (int idx in other.Indices)
            Indices.Add(idx + offset);
    }

    public (Vector3 Center, float Radius) BoundingSphere()
    {
        if (Positions.Count == 0) return (Vector3.Zero, 0);
        var min = Positions[0];
        var max = Positions[0];
        foreach (var p in Positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        var c = (min + max) / 2;
        float r = 0;
        foreach (var p in Positions)
            r = MathF.Max(r, Vector3.DistanceSquared(p, c));
        return (c, MathF.Sqrt(r));
    }
}
