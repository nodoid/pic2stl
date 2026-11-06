namespace Pic2Stl.Core.Geometry;

/// <summary>A triangle referencing three vertices, tagged with a palette material index.</summary>
public readonly record struct Triangle(int A, int B, int C, byte Material);

/// <summary>An indexed triangle mesh in millimetres.</summary>
public sealed class Mesh
{
    private readonly List<Vector3> _vertices = [];
    private readonly List<Triangle> _triangles = [];

    public IReadOnlyList<Vector3> Vertices => _vertices;

    public IReadOnlyList<Triangle> Triangles => _triangles;

    public int AddVertex(Vector3 vertex)
    {
        _vertices.Add(vertex);
        return _vertices.Count - 1;
    }

    public void AddTriangle(int a, int b, int c, byte material = 0)
    {
        if (a == b || b == c || a == c)
            throw new ArgumentException("Degenerate triangle: repeated vertex index.");

        if (a < 0 || b < 0 || c < 0 || a >= _vertices.Count || b >= _vertices.Count || c >= _vertices.Count)
            throw new ArgumentOutOfRangeException(nameof(a), "Triangle references a vertex that does not exist.");

        _triangles.Add(new Triangle(a, b, c, material));
    }

    public Vector3 VertexAt(int index) => _vertices[index];

    /// <summary>Unnormalised face normal (counter-clockwise winding gives the outward direction).</summary>
    public Vector3 FaceNormal(Triangle t) => Vector3.Cross(
        _vertices[t.B] - _vertices[t.A],
        _vertices[t.C] - _vertices[t.A]);

    /// <summary>Signed volume via the divergence theorem. Positive for outward-facing, closed meshes.</summary>
    public double SignedVolume()
    {
        double total = 0;
        foreach (var t in _triangles)
        {
            var a = _vertices[t.A];
            var b = _vertices[t.B];
            var c = _vertices[t.C];
            total += Vector3.Dot(a, Vector3.Cross(b, c));
        }

        return total / 6.0;
    }

    public double SurfaceArea()
    {
        double total = 0;
        foreach (var t in _triangles)
            total += 0.5 * FaceNormal(t).Length;

        return total;
    }

    public (Vector3 Min, Vector3 Max) Bounds()
    {
        if (_vertices.Count == 0)
            return (new Vector3(0, 0, 0), new Vector3(0, 0, 0));

        var min = _vertices[0];
        var max = _vertices[0];
        foreach (var v in _vertices)
        {
            min = new Vector3(MathF.Min(min.X, v.X), MathF.Min(min.Y, v.Y), MathF.Min(min.Z, v.Z));
            max = new Vector3(MathF.Max(max.X, v.X), MathF.Max(max.Y, v.Y), MathF.Max(max.Z, v.Z));
        }

        return (min, max);
    }

    /// <summary>Number of distinct triangles that use each material index.</summary>
    public IReadOnlyDictionary<byte, int> TrianglesPerMaterial()
    {
        var counts = new Dictionary<byte, int>();
        foreach (var t in _triangles)
            counts[t.Material] = counts.GetValueOrDefault(t.Material) + 1;

        return counts;
    }
}
