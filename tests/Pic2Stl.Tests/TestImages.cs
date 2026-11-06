using Pic2Stl.Core.Colours;
using Pic2Stl.Core.Depth;
using Pic2Stl.Core.Geometry;
using Pic2Stl.Core.Imaging;

namespace Pic2Stl.Tests;

internal static class TestImages
{
    /// <summary>Vertically split image: left half red, right half blue.</summary>
    public static ImageData TwoColourSides(int width = 32, int height = 16)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var left = x < width / 2;
                pixels[i] = left ? (byte)220 : (byte)20;
                pixels[i + 1] = 20;
                pixels[i + 2] = left ? (byte)20 : (byte)220;
                pixels[i + 3] = 255;
            }

        return new ImageData(width, height, pixels);
    }

    /// <summary>A 2x2 checkerboard of four saturated colours.</summary>
    public static ImageData FourQuadrants(int width = 40, int height = 40)
    {
        var colours = new[]
        {
            new Rgb(255, 0, 0),
            new Rgb(0, 255, 0),
            new Rgb(0, 0, 255),
            new Rgb(255, 255, 0),
        };

        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var quadrant = (y < height / 2 ? 0 : 2) + (x < width / 2 ? 0 : 1);
                var c = colours[quadrant];
                var i = (y * width + x) * 4;
                pixels[i] = c.R;
                pixels[i + 1] = c.G;
                pixels[i + 2] = c.B;
                pixels[i + 3] = 255;
            }

        return new ImageData(width, height, pixels);
    }

    /// <summary>A smooth two-axis gradient with far more distinct colours than any palette limit.</summary>
    public static ImageData Gradient(int width = 64, int height = 64)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                pixels[i] = (byte)(x * 255 / (width - 1));
                pixels[i + 1] = (byte)(y * 255 / (height - 1));
                pixels[i + 2] = (byte)(255 - x * 255 / (width - 1));
                pixels[i + 3] = 255;
            }

        return new ImageData(width, height, pixels);
    }

    /// <summary>A radial bump that rises towards the centre: a good stand-in for a subject.</summary>
    public static DepthMap Bump(int width = 24, int height = 18)
    {
        var values = new float[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var nx = x / (double)(width - 1) * 2 - 1;
                var ny = y / (double)(height - 1) * 2 - 1;
                var r = Math.Sqrt(nx * nx + ny * ny) / Math.Sqrt(2);
                values[y * width + x] = (float)Math.Clamp(1 - r, 0, 1);
            }

        return new DepthMap(width, height, values);
    }

    /// <summary>Every edge of a closed surface must be shared by exactly two triangles, in opposite directions.</summary>
    public static void AssertWatertight(Mesh mesh)
    {
        var directed = new Dictionary<(int, int), int>();
        foreach (var t in mesh.Triangles)
        {
            foreach (var edge in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
                directed[edge] = directed.GetValueOrDefault(edge) + 1;
        }

        var problems = new List<string>();
        foreach (var ((a, b), count) in directed)
        {
            if (count != 1)
                problems.Add($"edge {a}->{b} used {count} times");

            if (!directed.ContainsKey((b, a)))
                problems.Add($"edge {a}->{b} has no opposite");
        }

        Assert.That(problems, Is.Empty, $"Mesh is not watertight: {string.Join("; ", problems.Take(6))}");
    }

    /// <summary>No triangle may have a zero-area normal, and all faces must point away from the centroid on average.</summary>
    public static void AssertOutwardFacing(Mesh mesh)
    {
        var degenerates = mesh.Triangles.Count(t => mesh.FaceNormal(t).Length < 1e-6f);
        Assert.That(degenerates, Is.Zero, "Mesh contains degenerate triangles.");
        Assert.That(mesh.SignedVolume(), Is.GreaterThan(0), "Mesh volume is negative, so faces point inwards.");
    }
}
