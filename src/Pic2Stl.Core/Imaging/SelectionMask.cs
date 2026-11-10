using Pic2Stl.Core.Colours;

namespace Pic2Stl.Core.Imaging;

/// <summary>
/// Which pixels of a photo belong to the selected object. Everything else is background: it is flattened to
/// the base when the model is built, so the object stands proud on the print.
/// </summary>
public sealed class SelectionMask
{
    private readonly bool[] _keep;

    public SelectionMask(int width, int height, bool[] keep)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "Mask must be at least 1x1.");

        if (keep.Length != width * height)
            throw new ArgumentException("Mask must have width * height entries.", nameof(keep));

        Width = width;
        Height = height;
        _keep = keep;
    }

    public int Width { get; }

    public int Height { get; }

    public bool[] Values => _keep;

    public static SelectionMask All(int width, int height) => new(width, height, Create(width * height));

    private static bool[] Create(int length)
    {
        var keep = new bool[length];
        Array.Fill(keep, true);
        return keep;
    }

    /// <summary>The kept value at a pixel; points outside the mask count as background, so the border is an edge.</summary>
    public bool At(int x, int y) =>
        x >= 0 && x < Width && y >= 0 && y < Height && _keep[y * Width + x];

    public int KeptCount => _keep.Count(kept => kept);

    /// <summary>
    /// The edge of the kept region as line segments in mask pixel coordinates, so the editor can draw the
    /// object's actual outline instead of the rectangle it was found in. Neighbouring pixel edges are merged
    /// into runs, so a straight side is one segment rather than hundreds.
    /// </summary>
    public IReadOnlyList<(float X0, float Y0, float X1, float Y1)> Outline()
    {
        var segments = new List<(float X0, float Y0, float X1, float Y1)>();

        for (var y = 0; y < Height; y++)
        {
            AddHorizontalRuns(segments, y, -1);
            AddHorizontalRuns(segments, y, 1);
        }

        for (var x = 0; x < Width; x++)
        {
            AddVerticalRuns(segments, x, -1);
            AddVerticalRuns(segments, x, 1);
        }

        return segments;
    }

    /// <summary>Adds the runs of row <paramref name="y"/> whose neighbour <paramref name="dy"/> is missing.</summary>
    private void AddHorizontalRuns(
        List<(float X0, float Y0, float X1, float Y1)> segments, int y, int dy)
    {
        var line = y + (dy > 0 ? 1 : 0);
        var x = 0;

        while (x < Width)
        {
            if (!IsEdge(x, y, 0, dy))
            {
                x++;
                continue;
            }

            var start = x;
            while (x < Width && IsEdge(x, y, 0, dy))
                x++;

            segments.Add((start, line, x, line));
        }
    }

    /// <summary>Adds the runs of column <paramref name="x"/> whose neighbour <paramref name="dx"/> is missing.</summary>
    private void AddVerticalRuns(
        List<(float X0, float Y0, float X1, float Y1)> segments, int x, int dx)
    {
        var line = x + (dx > 0 ? 1 : 0);
        var y = 0;

        while (y < Height)
        {
            if (!IsEdge(x, y, dx, 0))
            {
                y++;
                continue;
            }

            var start = y;
            while (y < Height && IsEdge(x, y, dx, 0))
                y++;

            segments.Add((line, start, line, y));
        }
    }

    /// <summary>True when the pixel is kept and its neighbour in that direction is not, i.e. the edge is here.</summary>
    private bool IsEdge(int x, int y, int dx, int dy) =>
        At(x, y) && !At(x + dx, y + dy);

    /// <summary>
    /// The mask as an overlay image: the given colour where the object is kept, fully transparent elsewhere.
    /// The editor draws it over the photo so the user can see the outline before committing to it.
    /// </summary>
    public ImageData Tint(Rgb colour, byte alpha = 90)
    {
        var pixels = new byte[Width * Height * 4];

        for (var i = 0; i < _keep.Length; i++)
        {
            if (!_keep[i])
                continue;

            pixels[i * 4] = colour.R;
            pixels[i * 4 + 1] = colour.G;
            pixels[i * 4 + 2] = colour.B;
            pixels[i * 4 + 3] = alpha;
        }

        return new ImageData(Width, Height, pixels);
    }

    public bool IsEverything => KeptCount == _keep.Length;

    /// <summary>
    /// Rounds the staircase left when the photo's mask is sampled onto the model grid, by filling the concave
    /// corners of its outline: a gap with five or more kept neighbours becomes part of the object. Kept pixels
    /// are never removed, so thin detail the user selected is not eaten away.
    /// </summary>
    public SelectionMask Smoothed()
    {
        var smoothed = new bool[_keep.Length];

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var index = y * Width + x;
                if (_keep[index])
                {
                    smoothed[index] = true;
                    continue;
                }

                var neighbours = 0;

                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                        if ((dx != 0 || dy != 0) && At(x + dx, y + dy))
                            neighbours++;

                smoothed[index] = neighbours >= 5;
            }
        }

        return new SelectionMask(Width, Height, smoothed);
    }

    /// <summary>Nearest-neighbour resample, so the outline stays a clean edge on any model grid.</summary>
    public SelectionMask ResampleTo(int width, int height)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "Target size must be at least 1x1.");

        if (width == Width && height == Height)
            return this;

        var target = new bool[width * height];
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Clamp((int)((y + 0.5) * Height / height), 0, Height - 1);
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Clamp((int)((x + 0.5) * Width / width), 0, Width - 1);
                target[y * width + x] = _keep[sourceY * Width + sourceX];
            }
        }

        return new SelectionMask(width, height, target);
    }
}
