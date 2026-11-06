namespace Pic2Stl.Core.Imaging;

/// <summary>The object found inside a drawn box: its own outline plus the tight crop that contains it.</summary>
public sealed record ObjectSelection(SelectionRect Bounds, SelectionMask Mask);

/// <summary>
/// Grows a selection from the middle of a drawn box outwards while the colours still match, which follows the
/// subject's outline far better than the box itself does. A camera photo's subject is usually one connected
/// patch of similar colour, so a flood fill finds it; if the fill escapes too far to be meaningful the whole
/// box is used instead, which is what the user drew anyway.
/// </summary>
public static class ObjectSelector
{
    /// <summary>Largest possible RGB distance (0,0,0) to (255,255,255), used to turn a 0..1 tolerance into a distance.</summary>
    private const float MaxColourDistance = 441.673f;

    public static ObjectSelection Select(ImageData source, SelectionRect box, float tolerance = 0.2f)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (tolerance is <= 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be greater than 0 and at most 1.");

        box = box.Clamped(source.Width, source.Height);
        if (box.Width < 2 || box.Height < 2)
            throw new ArgumentException("Draw a box at least 2x2 pixels to select an object.", nameof(box));

        var seed = SeedColour(source, box);
        var keep = FloodFill(source, box, seed, tolerance * MaxColourDistance);
        var (count, bounds) = LargestPatch(keep, source.Width, box);

        var minimum = Math.Max(16, box.Width * box.Height / 20);
        if (count < minimum)
            return new ObjectSelection(box, SelectionMask.All(box.Width, box.Height));

        var mask = new bool[bounds.Width * bounds.Height];
        for (var y = 0; y < bounds.Height; y++)
            for (var x = 0; x < bounds.Width; x++)
                mask[y * bounds.Width + x] = keep[(bounds.Y + y) * source.Width + bounds.X + x];

        return new ObjectSelection(bounds, new SelectionMask(bounds.Width, bounds.Height, mask));
    }

    /// <summary>Average colour of the middle of the box, where the subject is most likely to be.</summary>
    private static (float R, float G, float B) SeedColour(ImageData source, SelectionRect box)
    {
        var insetX = Math.Max(0, box.Width / 4);
        var insetY = Math.Max(0, box.Height / 4);
        var left = box.X + insetX;
        var right = Math.Max(left, box.Right - insetX);
        var top = box.Y + insetY;
        var bottom = Math.Max(top, box.Bottom - insetY);

        float r = 0, g = 0, b = 0;
        var sampled = 0;

        for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++)
            {
                if (source.GetChannel(x, y, 3) < 8)
                    continue;

                r += source.GetChannel(x, y, 0);
                g += source.GetChannel(x, y, 1);
                b += source.GetChannel(x, y, 2);
                sampled++;
            }

        return sampled == 0 ? (0, 0, 0) : (r / sampled, g / sampled, b / sampled);
    }

    /// <summary>Four-connected fill from the box centre, refusing pixels that differ from the seed colour.</summary>
    private static bool[] FloodFill(
        ImageData source, SelectionRect box, (float R, float G, float B) seed, float threshold)
    {
        var width = source.Width;
        var keep = new bool[width * source.Height];
        var stack = new Stack<(int X, int Y)>();

        var insetX = Math.Max(0, box.Width / 4);
        var insetY = Math.Max(0, box.Height / 4);
        var left = box.X + insetX;
        var right = Math.Max(left, box.Right - insetX);
        var top = box.Y + insetY;
        var bottom = Math.Max(top, box.Bottom - insetY);

        for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++)
                if (Matches(source, x, y, seed, threshold))
                    stack.Push((x, y));

        var squaredThreshold = threshold * threshold;

        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            var index = y * width + x;
            if (keep[index])
                continue;

            keep[index] = true;

            if (x > box.X)
                TryPush(x - 1, y);
            if (x < box.Right)
                TryPush(x + 1, y);
            if (y > box.Y)
                TryPush(x, y - 1);
            if (y < box.Bottom)
                TryPush(x, y + 1);
        }

        return keep;

        void TryPush(int x, int y)
        {
            if (keep[y * width + x])
                return;

            if (SquaredDistance(source, x, y, seed) <= squaredThreshold)
                stack.Push((x, y));
        }
    }

    private static bool Matches(ImageData source, int x, int y, (float R, float G, float B) seed, float threshold) =>
        SquaredDistance(source, x, y, seed) <= threshold * threshold;

    private static float SquaredDistance(ImageData source, int x, int y, (float R, float G, float B) seed)
    {
        var dr = source.GetChannel(x, y, 0) - seed.R;
        var dg = source.GetChannel(x, y, 1) - seed.G;
        var db = source.GetChannel(x, y, 2) - seed.B;
        return dr * dr + dg * dg + db * db;
    }

    /// <summary>The biggest filled patch, which is the subject; stray specks are ignored.</summary>
    private static (int Count, SelectionRect Bounds) LargestPatch(bool[] keep, int width, SelectionRect box)
    {
        var seen = new bool[keep.Length];
        var bestCount = 0;
        var bestBounds = new SelectionRect(box.X, box.Y, 0, 0);

        for (var y = box.Y; y <= box.Bottom; y++)
            for (var x = box.X; x <= box.Right; x++)
            {
                var start = y * width + x;
                if (!keep[start] || seen[start])
                    continue;

                int count = 0, minX = x, maxX = x, minY = y, maxY = y;
                var stack = new Stack<(int X, int Y)>();
                stack.Push((x, y));
                seen[start] = true;

                while (stack.Count > 0)
                {
                    var (px, py) = stack.Pop();
                    count++;
                    minX = Math.Min(minX, px);
                    maxX = Math.Max(maxX, px);
                    minY = Math.Min(minY, py);
                    maxY = Math.Max(maxY, py);

                    foreach (var (nx, ny) in new[] { (px - 1, py), (px + 1, py), (px, py - 1), (px, py + 1) })
                    {
                        if (nx < box.X || nx > box.Right || ny < box.Y || ny > box.Bottom)
                            continue;

                        var index = ny * width + nx;
                        if (!keep[index] || seen[index])
                            continue;

                        seen[index] = true;
                        stack.Push((nx, ny));
                    }
                }

                if (count <= bestCount)
                    continue;

                bestCount = count;
                bestBounds = new SelectionRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
            }

        return (bestCount, bestBounds);
    }
}
