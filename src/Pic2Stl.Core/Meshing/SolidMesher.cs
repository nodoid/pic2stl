using Pic2Stl.Core.Colours;
using Pic2Stl.Core.Depth;
using Pic2Stl.Core.Geometry;

namespace Pic2Stl.Core.Meshing;

/// <summary>Physical dimensions and shape tuning for the generated solid.</summary>
public sealed class SolidMeshOptions
{
    /// <summary>Width of the model in millimetres; height follows the photo's aspect ratio.</summary>
    public float WidthMm { get; init; } = 80f;

    /// <summary>Peak height of the front relief above the base slab.</summary>
    public float ReliefMm { get; init; } = 16f;

    /// <summary>How much of the relief is repeated on the guessed back, as a fraction of the front.</summary>
    public float BackRatio { get; init; } = 0.45f;

    /// <summary>Thickness of the solid where the photo has no subject, so it always prints as one piece.</summary>
    public float MinThicknessMm { get; init; } = 2f;
}

/// <summary>
/// Turns a depth field into a closed solid: a relief surface on the front, a guessed mirrored surface on the
/// back (so the object is printable from any side rather than a flat relief), and rim walls joining them.
/// </summary>
public static class SolidMesher
{
    public static Mesh Build(DepthMap depth, MaterialMap? materials, SolidMeshOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(depth);
        options ??= new SolidMeshOptions();

        if (depth.Width < 2 || depth.Height < 2)
            throw new ArgumentException("Depth map must be at least 2x2 to form a solid.", nameof(depth));

        if (options.WidthMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Width must be greater than zero.");

        if (options.ReliefMm < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Relief must not be negative.");

        if (options.BackRatio < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Back ratio must not be negative.");

        if (options.MinThicknessMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Minimum thickness must be greater than zero.");

        if (materials is not null && (materials.Width != depth.Width || materials.Height != depth.Height))
            throw new ArgumentException("Material map must have the same dimensions as the depth map.", nameof(materials));

        int width = depth.Width, height = depth.Height;
        var heightMm = options.WidthMm * height / width;
        var backBase = options.MinThicknessMm * 0.6f;

        var mesh = new Mesh();
        var frontZ = new float[width * height];
        var backZ = new float[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var d = Math.Clamp(depth.At(x, y), 0f, 1f);
                frontZ[y * width + x] = options.MinThicknessMm + d * options.ReliefMm;
                backZ[y * width + x] = -(backBase + d * options.ReliefMm * options.BackRatio);
            }
        }

        float X(int x) => (float)(x / (double)(width - 1) - 0.5) * options.WidthMm;
        float Y(int y) => (float)(0.5 - y / (double)(height - 1)) * heightMm;

        // Front vertices first, then back vertices, so the index offset is a constant.
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                mesh.AddVertex(new Vector3(X(x), Y(y), frontZ[y * width + x]));

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                mesh.AddVertex(new Vector3(X(x), Y(y), backZ[y * width + x]));

        var backOffset = width * height;
        byte MaterialAt(int x, int y) => materials?.At(x, y) ?? 0;

        // Front faces, counter-clockwise seen from +Z.
        for (var y = 0; y < height - 1; y++)
        {
            for (var x = 0; x < width - 1; x++)
            {
                int n = y * width + x;
                int right = n + 1;
                int below = n + width;
                int belowRight = below + 1;
                var material = MaterialAt(x, y);

                mesh.AddTriangle(n, below, right, material);
                mesh.AddTriangle(right, below, belowRight, material);
            }
        }

        // Back faces, reversed winding so their normals point away from the object.
        for (var y = 0; y < height - 1; y++)
        {
            for (var x = 0; x < width - 1; x++)
            {
                int n = backOffset + y * width + x;
                int right = n + 1;
                int below = n + width;
                int belowRight = below + 1;
                var material = MaterialAt(x, y);

                mesh.AddTriangle(n, right, below, material);
                mesh.AddTriangle(right, belowRight, below, material);
            }
        }

        // Rim walls along the perimeter, walked clockwise in grid space so normals face outwards.
        var perimeter = Perimeter(width, height);
        for (var i = 0; i < perimeter.Count; i++)
        {
            var (ax, ay) = perimeter[i];
            var (bx, by) = perimeter[(i + 1) % perimeter.Count];

            int aFront = ay * width + ax;
            int bFront = by * width + bx;
            int aBack = backOffset + aFront;
            int bBack = backOffset + bFront;
            var material = MaterialAt(ax, ay);

            mesh.AddTriangle(aFront, bFront, aBack, material);
            mesh.AddTriangle(bFront, bBack, aBack, material);
        }

        return mesh;
    }

    /// <summary>Boundary grid points in clockwise order: top left→right, right top→bottom, etc.</summary>
    public static List<(int X, int Y)> Perimeter(int width, int height)
    {
        if (width < 2 || height < 2)
            throw new ArgumentOutOfRangeException(nameof(width), "A solid needs a grid of at least 2x2.");

        var points = new List<(int X, int Y)>();
        for (var x = 0; x < width - 1; x++)
            points.Add((x, 0));
        for (var y = 0; y < height - 1; y++)
            points.Add((width - 1, y));
        for (var x = width - 1; x > 0; x--)
            points.Add((x, height - 1));
        for (var y = height - 1; y > 0; y--)
            points.Add((0, y));

        return points;
    }
}
