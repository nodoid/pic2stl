using System.Text;
using Pic2Stl.Core.Geometry;

namespace Pic2Stl.Core.Export;

/// <summary>Writes binary STL, the format every slicer accepts.</summary>
public static class StlWriter
{
    public const int HeaderLength = 80;

    public static void Write(Stream stream, Mesh mesh, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(mesh);

        if (mesh.Triangles.Count == 0)
            throw new InvalidOperationException("Cannot write an STL with no triangles.");

        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        var header = Encoding.ASCII.GetBytes($"Pic2Stl {title} binary STL".Trim());
        var headerBytes = new byte[HeaderLength];
        Array.Copy(header, headerBytes, Math.Min(header.Length, HeaderLength));
        writer.Write(headerBytes);

        writer.Write((uint)mesh.Triangles.Count);
        foreach (var triangle in mesh.Triangles)
        {
            var a = mesh.VertexAt(triangle.A);
            var b = mesh.VertexAt(triangle.B);
            var c = mesh.VertexAt(triangle.C);
            var normal = Vector3.Cross(b - a, c - a).Normalized();

            WriteVector(writer, normal);
            WriteVector(writer, a);
            WriteVector(writer, b);
            WriteVector(writer, c);
            writer.Write((ushort)0);
        }

        writer.Flush();
    }

    public static byte[] ToBytes(Mesh mesh, string? title = null)
    {
        using var buffer = new MemoryStream();
        Write(buffer, mesh, title);
        return buffer.ToArray();
    }

    private static void WriteVector(BinaryWriter writer, Vector3 v)
    {
        writer.Write(v.X);
        writer.Write(v.Y);
        writer.Write(v.Z);
    }
}
