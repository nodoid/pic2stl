namespace Pic2Stl.Core.Geometry;

/// <summary>A point or direction in model space, in millimetres.</summary>
public readonly record struct Vector3(float X, float Y, float Z)
{
    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3 operator *(Vector3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);

    public static Vector3 Cross(Vector3 a, Vector3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    public static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public float Length => MathF.Sqrt(X * X + Y * Y + Z * Z);

    public Vector3 Normalized()
    {
        var len = Length;
        return len > 1e-6f ? this * (1f / len) : new Vector3(0, 0, 0);
    }

    public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
}
