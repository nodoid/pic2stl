using Pic2Stl.Core.Colours;
using Pic2Stl.Core.Depth;
using Pic2Stl.Core.Geometry;
using Pic2Stl.Core.Imaging;

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

    /// <summary>
    /// How far the relief fades towards the base at the object's outline, as a fraction of its longest side.
    /// The back of a subject in a photo is never seen, so the model is closed with a short bevel and a guessed
    /// mirrored back rather than a sheer wall behind a flat panel. Zero keeps a hard edge.
    /// </summary>
    public float EdgeFade { get; init; } = 0.03f;

    /// <summary>
    /// Walls the seams between print colours, so each colour on its own is a closed solid a slicer can give a
    /// spool to. The shared seam walls make the mesh as a whole non-manifold, so only the colour-by-colour 3MF
    /// is built this way; the STL and the preview keep the single closed solid.
    /// </summary>
    public bool SealColours { get; init; }

    /// <summary>The same solid, built so that every print colour closes on its own.</summary>
    public SolidMeshOptions SealingColours() => new()
    {
        WidthMm = WidthMm,
        ReliefMm = ReliefMm,
        BackRatio = BackRatio,
        MinThicknessMm = MinThicknessMm,
        EdgeFade = EdgeFade,
        SealColours = true,
    };
}

/// <summary>
/// Turns a depth field into a closed solid: a relief surface on the front, a guessed mirrored surface on the
/// back (so the object is printable from any side rather than a flat relief), and rim walls joining them.
/// Given an object outline, the solid is only built inside it, so the print is the object itself rather than a
/// rectangular plate with the object standing on it, and the requested width describes the object.
/// </summary>
public static class SolidMesher
{
    public static Mesh Build(
        DepthMap depth,
        MaterialMap? materials,
        SolidMeshOptions? options = null,
        SelectionMask? outline = null)
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

        if (options.EdgeFade is < 0f or > 0.5f)
            throw new ArgumentOutOfRangeException(nameof(options), "Edge fade must be between 0 and 0.5.");

        if (materials is not null && (materials.Width != depth.Width || materials.Height != depth.Height))
            throw new ArgumentException("Material map must have the same dimensions as the depth map.", nameof(materials));

        if (outline is not null && (outline.Width != depth.Width || outline.Height != depth.Height))
            throw new ArgumentException("Object outline must have the same dimensions as the depth map.", nameof(outline));

        // Clean the ragged edge left by sampling the photo's mask onto the model grid, so the rim follows the
        // subject rather than the grid. A fallback keeps a mask that is too small to survive the clean-up.
        if (outline is not null)
        {
            var smoothed = outline.Smoothed();
            if (smoothed.KeptCount > 0)
                outline = smoothed;
        }

        // The depth grid covers the photo, but only the outline is printed. Narrowing the grid to the outline's
        // own bounds removes the plate around the subject and makes WidthMm describe the object itself.
        var region = CropToOutline(outline, depth.Width, depth.Height);
        int originX = region.OriginX, originY = region.OriginY;
        int width = region.Width, height = region.Height;
        var inside = region.Inside;

        var heightMm = options.WidthMm * height / width;
        var backBase = options.MinThicknessMm * 0.6f;

        var mesh = new Mesh();
        var frontZ = new float[width * height];
        var backZ = new float[width * height];
        var fade = inside is null ? null : EdgeRamp(inside, width, height, options.EdgeFade);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                var d = Math.Clamp(depth.At(x + originX, y + originY), 0f, 1f);

                // Fading the relief to the base at the outline closes the object smoothly: the height the photo
                // cannot show behind its silhouette is guessed by the mirrored back, not by a vertical cliff.
                d *= fade?[i] ?? 1f;

                frontZ[i] = options.MinThicknessMm + d * options.ReliefMm;
                backZ[i] = -(backBase + d * options.ReliefMm * options.BackRatio);
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
        byte MaterialAt(int x, int y) => materials?.At(x + originX, y + originY) ?? 0;

        // A cell is part of the object when all four of its corners are, which keeps the walls on the outline
        // itself. Without an outline every cell is printed, as before.
        var cellWidth = width - 1;
        var cellHeight = height - 1;
        var cells = new bool[cellWidth * cellHeight];

        if (inside is null)
        {
            Array.Fill(cells, true);
        }
        else
        {
            for (var y = 0; y < cellHeight; y++)
                for (var x = 0; x < cellWidth; x++)
                    cells[y * cellWidth + x] =
                        inside[y * width + x] && inside[y * width + x + 1] &&
                        inside[(y + 1) * width + x] && inside[(y + 1) * width + x + 1];

            // A stray patch would print as a loose fragment, and a diagonal touch would pinch the surface.
            KeepLargestRegion(cells, cellWidth, cellHeight);

            if (!cells.Contains(true))
                throw new ArgumentException("The object outline is too small to form a solid.", nameof(outline));
        }

        bool IsCell(int x, int y) =>
            x >= 0 && y >= 0 && x < cellWidth && y < cellHeight && cells[y * cellWidth + x];

        for (var y = 0; y < cellHeight; y++)
        {
            for (var x = 0; x < cellWidth; x++)
            {
                if (!cells[y * cellWidth + x])
                    continue;

                int n = y * width + x;
                int right = n + 1;
                int below = n + width;
                int belowRight = below + 1;
                var material = MaterialAt(x, y);

                // Front faces, counter-clockwise seen from +Z.
                mesh.AddTriangle(n, below, right, material);
                mesh.AddTriangle(right, below, belowRight, material);

                // Back faces, reversed winding so their normals point away from the object.
                int b = backOffset + n;
                int bRight = b + 1;
                int bBelow = b + width;
                int bBelowRight = bBelow + 1;
                mesh.AddTriangle(b, bRight, bBelow, material);
                mesh.AddTriangle(bRight, bBelowRight, bBelow, material);
            }
        }

        // Rim walls wherever a printed cell meets the background, walked so the normals face outwards. A
        // neighbouring cell of another colour gets the same wall at the shared edge, which closes every colour
        // as a solid of its own: a slicer can then hand each colour its own spool, where an open shell is
        // rejected as non-manifold.
        for (var y = 0; y < cellHeight; y++)
        {
            for (var x = 0; x < cellWidth; x++)
            {
                if (!cells[y * cellWidth + x])
                    continue;

                var material = MaterialAt(x, y);

                if (NeedsWall(x, y - 1, material))
                    AddWall(mesh, width, backOffset, x, y, x + 1, y, material);

                if (NeedsWall(x + 1, y, material))
                    AddWall(mesh, width, backOffset, x + 1, y, x + 1, y + 1, material);

                if (NeedsWall(x, y + 1, material))
                    AddWall(mesh, width, backOffset, x + 1, y + 1, x, y + 1, material);

                if (NeedsWall(x - 1, y, material))
                    AddWall(mesh, width, backOffset, x, y + 1, x, y, material);
            }
        }

        return mesh;

        bool NeedsWall(int x, int y, byte material) =>
            !IsCell(x, y) || (options.SealColours && MaterialAt(x, y) != material);
    }

    /// <summary>One quad of rim wall: two triangles joining the front edge to the back edge.</summary>
    private static void AddWall(
        Mesh mesh, int width, int backOffset, int ax, int ay, int bx, int by, byte material)
    {
        int aFront = ay * width + ax;
        int bFront = by * width + bx;
        int aBack = backOffset + aFront;
        int bBack = backOffset + bFront;

        mesh.AddTriangle(aFront, bFront, aBack, material);
        mesh.AddTriangle(bFront, bBack, aBack, material);
    }

    /// <summary>The grid to mesh: the whole depth map, or the outline's tight bounds and its pixels.</summary>
    private static (int OriginX, int OriginY, int Width, int Height, bool[]? Inside) CropToOutline(
        SelectionMask? outline, int depthWidth, int depthHeight)
    {
        if (outline is null)
            return (0, 0, depthWidth, depthHeight, null);

        var values = outline.Values;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;

        for (var y = 0; y < outline.Height; y++)
            for (var x = 0; x < outline.Width; x++)
            {
                if (!values[y * outline.Width + x])
                    continue;

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }

        if (maxX < 0)
            throw new ArgumentException("The object outline keeps nothing, so there is no shape to print.", nameof(outline));

        // An outline that keeps everything is the same as no outline: print the whole grid, with no trim.
        if (minX == 0 && minY == 0 && maxX == outline.Width - 1 && maxY == outline.Height - 1 &&
            outline.KeptCount == values.Length)
            return (0, 0, outline.Width, outline.Height, null);

        // A one-pixel sliver would leave the wall with no extent, so grow it by its neighbours.
        if (maxX - minX < 1)
        {
            if (minX > 0)
                minX--;
            else
                maxX++;
        }

        if (maxY - minY < 1)
        {
            if (minY > 0)
                minY--;
            else
                maxY++;
        }

        var width = maxX - minX + 1;
        var height = maxY - minY + 1;
        var inside = new bool[width * height];

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                inside[y * width + x] = values[(y + minY) * outline.Width + x + minX];

        return (minX, minY, width, height, inside);
    }

    /// <summary>
    /// How far each point is from the background, turned into a 0..1 ramp that fades the relief over
    /// <paramref name="fade"/> of the object's longest side.
    /// </summary>
    private static float[] EdgeRamp(bool[] inside, int width, int height, float fade)
    {
        var distance = new int[inside.Length];
        var queue = new Queue<int>();

        for (var i = 0; i < inside.Length; i++)
        {
            if (inside[i])
            {
                distance[i] = -1;
            }
            else
            {
                distance[i] = 0;
                queue.Enqueue(i);
            }
        }

        // A kept point on the grid's edge also borders background: the outline is open there.
        for (var x = 0; x < width; x++)
        {
            Seed(x, 0);
            Seed(x, height - 1);
        }

        for (var y = 0; y < height; y++)
        {
            Seed(0, y);
            Seed(width - 1, y);
        }

        void Seed(int x, int y)
        {
            var index = y * width + x;
            if (inside[index] && distance[index] < 1)
            {
                distance[index] = 1;
                queue.Enqueue(index);
            }
        }

        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            var step = distance[index] + 1;
            var x = index % width;
            var y = index / width;

            if (x > 0)
                Visit(index - 1, step);
            if (x < width - 1)
                Visit(index + 1, step);
            if (y > 0)
                Visit(index - width, step);
            if (y < height - 1)
                Visit(index + width, step);
        }

        void Visit(int index, int step)
        {
            if (!inside[index] || distance[index] >= 0)
                return;

            distance[index] = step;
            queue.Enqueue(index);
        }

        var taper = Math.Max(1, (int)MathF.Round(fade * Math.Max(width, height)));
        var ramp = new float[inside.Length];

        for (var i = 0; i < inside.Length; i++)
            ramp[i] = inside[i] ? Math.Clamp((distance[i] - 1) / (float)taper, 0f, 1f) : 0f;

        return ramp;
    }

    /// <summary>Keeps only the biggest 4-connected group of cells, so no fragment or pinched corner is printed.</summary>
    private static void KeepLargestRegion(bool[] cells, int width, int height)
    {
        var seen = new bool[cells.Length];
        var queue = new Queue<int>();
        var largest = new List<int>();
        var current = new List<int>();

        for (var start = 0; start < cells.Length; start++)
        {
            if (!cells[start] || seen[start])
                continue;

            current.Clear();
            seen[start] = true;
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                current.Add(index);
                var x = index % width;
                var y = index / width;

                if (x > 0)
                    Visit(index - 1);
                if (x < width - 1)
                    Visit(index + 1);
                if (y > 0)
                    Visit(index - width);
                if (y < height - 1)
                    Visit(index + width);
            }

            if (current.Count > largest.Count)
            {
                largest.Clear();
                largest.AddRange(current);
            }
        }

        if (largest.Count == 0)
            return;

        Array.Fill(cells, false);
        foreach (var index in largest)
            cells[index] = true;

        void Visit(int index)
        {
            if (!cells[index] || seen[index])
                return;

            seen[index] = true;
            queue.Enqueue(index);
        }
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
