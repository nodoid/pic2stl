namespace Pic2Stl.Core.Imaging;

/// <summary>The object found inside a drawn box: its own outline plus the tight crop that contains it.</summary>
public sealed record ObjectSelection(SelectionRect Bounds, SelectionMask Mask);

/// <summary>
/// One touch of the refinement brush: a disc in photo fractions that is either added to the object or cut from
/// it. The radius is a fraction of the photo's width, so the same stroke means the same thing whether the
/// selection is being previewed small or applied at full resolution.
/// </summary>
public readonly record struct SelectionStroke(float X, float Y, float Radius, bool Keep);

/// <summary>
/// Turns the box the user dragged into the shape of whatever is inside it. The colours along the edge of the
/// box are taken to be background, everything connected to that edge within the tolerance is removed, and the
/// largest patch that is left becomes the selection: the object's own outline rather than the box it was found
/// in. Holes inside the object are filled so the subject is one solid piece; if the photo has no usable edge
/// the whole box is kept, which is exactly what the user drew.
/// </summary>
public static class ObjectSelector
{
    /// <summary>Largest possible RGB distance (0,0,0) to (255,255,255), used to turn a 0..1 tolerance into a distance.</summary>
    private const float MaxColourDistance = 441.673f;

    /// <summary>How many of the border's dominant colours count as background; more than this matches anything.</summary>
    private const int MaxBorderColours = 32;

    public static ObjectSelection Select(ImageData source, SelectionRect box, float tolerance = 0.2f)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (tolerance is <= 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be greater than 0 and at most 1.");

        box = box.Clamped(source.Width, source.Height);
        if (box.Width < 2 || box.Height < 2)
            throw new ArgumentException("Draw a box at least 2x2 pixels to select an object.", nameof(box));

        var threshold = tolerance * MaxColourDistance;
        var squaredThreshold = threshold * threshold;

        // Colours that look like the middle of the box are the subject, not background, so a tight box around
        // a plain subject does not end up treating the subject itself as the thing to remove.
        var subject = CentreColour(source, box);
        var references = BorderColours(source, box)
            .Where(colour => SquaredDistance(colour, subject) > squaredThreshold)
            .ToArray();

        if (references.Length == 0)
            return WholeBox(box);

        var background = BackgroundFlood(source, box, references, squaredThreshold);

        // The middle of the box is where the object is expected to be. If the fill reached it, it treated the
        // subject as background, so the honest answer is the box the user drew.
        var middle = (box.Y + box.Height / 2) * source.Width + box.X + box.Width / 2;
        if (background[middle])
            return WholeBox(box);

        var (patch, bounds) = LargestPatch(background, source.Width, box);
        var minimum = Math.Max(16, box.Width * box.Height / 20);
        if (patch.Count < minimum || bounds.Width < 2 || bounds.Height < 2)
            return WholeBox(box);

        var mask = new bool[bounds.Width * bounds.Height];
        foreach (var index in patch)
        {
            var x = index % source.Width;
            var y = index / source.Width;
            mask[(y - bounds.Y) * bounds.Width + x - bounds.X] = true;
        }

        FillHoles(mask, bounds.Width, bounds.Height);
        return new ObjectSelection(bounds, new SelectionMask(bounds.Width, bounds.Height, mask));
    }

    /// <summary>
    /// Works out an object's bounds from a single tap: the colour under the finger is taken to be the object's,
    /// and everything connected to the tap that matches it becomes the selection, so the user does not have to
    /// draw a box first. The result is the tight crop around that region plus its own outline. A tap on a large
    /// uniform area (a plain backdrop, or a photo with no separate object) keeps the whole picture, which is
    /// what a user who tapped anywhere on it would expect to see.
    /// </summary>
    public static ObjectSelection SelectAt(ImageData source, int x, int y, float tolerance = 0.2f)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (tolerance is <= 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be greater than 0 and at most 1.");

        x = Math.Clamp(x, 0, source.Width - 1);
        y = Math.Clamp(y, 0, source.Height - 1);

        var threshold = tolerance * MaxColourDistance;
        var squaredThreshold = threshold * threshold;
        var seed = PixelColour(source, x, y);

        var width = source.Width;
        var member = new bool[width * source.Height];
        var queue = new Queue<int>();
        var patch = new List<int>();
        int minX = x, maxX = x, minY = y, maxY = y;

        var start = y * width + x;
        member[start] = true;
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            patch.Add(index);

            var px = index % width;
            var py = index / width;
            minX = Math.Min(minX, px);
            maxX = Math.Max(maxX, px);
            minY = Math.Min(minY, py);
            maxY = Math.Max(maxY, py);

            void Visit(int nx, int ny)
            {
                var next = ny * width + nx;
                if (member[next])
                    return;

                var dr = source.GetChannel(nx, ny, 0) - seed.R;
                var dg = source.GetChannel(nx, ny, 1) - seed.G;
                var db = source.GetChannel(nx, ny, 2) - seed.B;
                if (dr * dr + dg * dg + db * db > squaredThreshold)
                    return;

                member[next] = true;
                queue.Enqueue(next);
            }

            if (px > 0)
                Visit(px - 1, py);
            if (px < width - 1)
                Visit(px + 1, py);
            if (py > 0)
                Visit(px, py - 1);
            if (py < source.Height - 1)
                Visit(px, py + 1);
        }

        var bounds = new SelectionRect(minX, minY, maxX - minX + 1, maxY - minY + 1);

        // The tap matched the whole photo, so there is no separate object in it to crop around. Keeping everything
        // leaves the photo as it is rather than producing a meaningless 1-pixel selection.
        if (bounds.Width == source.Width && bounds.Height == source.Height && patch.Count == width * source.Height)
            return new ObjectSelection(bounds, SelectionMask.All(bounds.Width, bounds.Height));

        var mask = new bool[bounds.Width * bounds.Height];
        foreach (var index in patch)
        {
            var px = index % width;
            var py = index / width;
            mask[(py - bounds.Y) * bounds.Width + px - bounds.X] = true;
        }

        FillHoles(mask, bounds.Width, bounds.Height);
        return new ObjectSelection(bounds, new SelectionMask(bounds.Width, bounds.Height, mask));
    }

    /// <summary>The colour of one pixel, used as the reference a tap is matched against.</summary>
    private static (float R, float G, float B) PixelColour(ImageData source, int x, int y) =>
        (source.GetChannel(x, y, 0), source.GetChannel(x, y, 1), source.GetChannel(x, y, 2));

    /// <summary>A selection that keeps the user's box exactly, used when the photo gives no clear edge.</summary>
    private static ObjectSelection WholeBox(SelectionRect box) =>
        new(box, SelectionMask.All(box.Width, box.Height));

    /// <summary>
    /// Paints brush strokes over a found object, which is how the user nudges the outline the colour match
    /// produced. Strokes can reach outside the original box, so the crop grows to hold them; the result is
    /// tightened back to the pixels that are still kept. Cutting every pixel away leaves the selection alone.
    /// </summary>
    public static ObjectSelection Refine(
        ObjectSelection selection, int imageWidth, int imageHeight, IReadOnlyList<SelectionStroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(strokes);

        if (imageWidth < 1 || imageHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Image must be at least 1x1.");

        if (strokes.Count == 0)
            return selection;

        var bounds = selection.Bounds;
        int left = bounds.X, top = bounds.Y, right = bounds.Right, bottom = bounds.Bottom;

        foreach (var stroke in strokes)
        {
            var radius = RadiusInPixels(stroke, imageWidth);
            var x = stroke.X * imageWidth;
            var y = stroke.Y * imageHeight;

            left = Math.Min(left, (int)MathF.Floor(x - radius));
            right = Math.Max(right, (int)MathF.Ceiling(x + radius));
            top = Math.Min(top, (int)MathF.Floor(y - radius));
            bottom = Math.Max(bottom, (int)MathF.Ceiling(y + radius));
        }

        left = Math.Clamp(left, 0, imageWidth - 1);
        right = Math.Clamp(right, 0, imageWidth - 1);
        top = Math.Clamp(top, 0, imageHeight - 1);
        bottom = Math.Clamp(bottom, 0, imageHeight - 1);

        var width = right - left + 1;
        var height = bottom - top + 1;
        var keep = new bool[width * height];

        for (var y = 0; y < bounds.Height; y++)
            for (var x = 0; x < bounds.Width; x++)
            {
                if (!selection.Mask.At(x, y))
                    continue;

                var px = bounds.X + x - left;
                var py = bounds.Y + y - top;
                if (px >= 0 && px < width && py >= 0 && py < height)
                    keep[py * width + px] = true;
            }

        foreach (var stroke in strokes)
        {
            var radius = RadiusInPixels(stroke, imageWidth);
            var centreX = stroke.X * imageWidth;
            var centreY = stroke.Y * imageHeight;
            var squaredRadius = radius * radius;

            var fromX = Math.Max(left, (int)MathF.Floor(centreX - radius));
            var toX = Math.Min(right, (int)MathF.Ceiling(centreX + radius));
            var fromY = Math.Max(top, (int)MathF.Floor(centreY - radius));
            var toY = Math.Min(bottom, (int)MathF.Ceiling(centreY + radius));

            for (var y = fromY; y <= toY; y++)
                for (var x = fromX; x <= toX; x++)
                {
                    var dx = x + 0.5f - centreX;
                    var dy = y + 0.5f - centreY;
                    if (dx * dx + dy * dy > squaredRadius)
                        continue;

                    keep[(y - top) * width + (x - left)] = stroke.Keep;
                }
        }

        // Tighten back to the pixels that are still part of the object.
        int keptLeft = int.MaxValue, keptTop = int.MaxValue, keptRight = -1, keptBottom = -1;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (!keep[y * width + x])
                    continue;

                keptLeft = Math.Min(keptLeft, x);
                keptRight = Math.Max(keptRight, x);
                keptTop = Math.Min(keptTop, y);
                keptBottom = Math.Max(keptBottom, y);
            }

        if (keptRight < 0)
            return selection;

        var maskWidth = keptRight - keptLeft + 1;
        var maskHeight = keptBottom - keptTop + 1;
        var mask = new bool[maskWidth * maskHeight];

        for (var y = 0; y < maskHeight; y++)
            for (var x = 0; x < maskWidth; x++)
                mask[y * maskWidth + x] = keep[(keptTop + y) * width + keptLeft + x];

        return new ObjectSelection(
            new SelectionRect(left + keptLeft, top + keptTop, maskWidth, maskHeight),
            new SelectionMask(maskWidth, maskHeight, mask));
    }

    private static float RadiusInPixels(SelectionStroke stroke, int imageWidth) =>
        Math.Max(1f, Math.Abs(stroke.Radius) * imageWidth);

    /// <summary>
    /// Average colour of a small patch in the middle of the box, where the subject is most likely to be. Only
    /// the middle fifth is sampled: a wide sample would mix in the background around a small subject.
    /// </summary>
    private static (float R, float G, float B) CentreColour(ImageData source, SelectionRect box)
    {
        var insetX = Math.Max(1, box.Width * 2 / 5);
        var insetY = Math.Max(1, box.Height * 2 / 5);
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

    /// <summary>
    /// The dominant colours around the edge of the box, which is where the background is. Samples are grouped
    /// into coarse buckets first so JPEG noise does not turn one wall into a hundred slightly different colours.
    /// </summary>
    private static (float R, float G, float B)[] BorderColours(ImageData source, SelectionRect box)
    {
        var buckets = new Dictionary<int, (float R, float G, float B, int Count)>();

        void Sample(int x, int y)
        {
            if (source.GetChannel(x, y, 3) < 8)
                return;

            float r = source.GetChannel(x, y, 0);
            float g = source.GetChannel(x, y, 1);
            float b = source.GetChannel(x, y, 2);
            var key = ((int)r >> 3) << 10 | ((int)g >> 3) << 5 | ((int)b >> 3);

            buckets[key] = buckets.TryGetValue(key, out var bucket)
                ? (bucket.R + r, bucket.G + g, bucket.B + b, bucket.Count + 1)
                : (r, g, b, 1);
        }

        var stepX = Math.Max(1, box.Width / 64);
        var stepY = Math.Max(1, box.Height / 64);

        for (var x = box.X; x <= box.Right; x += stepX)
        {
            Sample(x, box.Y);
            Sample(x, box.Bottom);
        }

        Sample(box.Right, box.Y);
        Sample(box.Right, box.Bottom);

        for (var y = box.Y; y <= box.Bottom; y += stepY)
        {
            Sample(box.X, y);
            Sample(box.Right, y);
        }

        Sample(box.X, box.Bottom);
        Sample(box.Right, box.Bottom);

        return buckets.Values
            .OrderByDescending(bucket => bucket.Count)
            .Take(MaxBorderColours)
            .Select(bucket => (bucket.R / bucket.Count, bucket.G / bucket.Count, bucket.B / bucket.Count))
            .ToArray();
    }

    /// <summary>
    /// Marks the background: pixels that match one of the border colours and are connected to the edge of the
    /// box through other background pixels. Anything enclosed by the subject stays put.
    /// </summary>
    private static bool[] BackgroundFlood(
        ImageData source, SelectionRect box, (float R, float G, float B)[] references, float squaredThreshold)
    {
        var width = source.Width;
        var background = new bool[width * source.Height];
        var queue = new Queue<int>();

        void Visit(int x, int y)
        {
            var index = y * width + x;
            if (background[index])
                return;

            if (SquaredDistance(source, x, y, references) > squaredThreshold)
                return;

            background[index] = true;
            queue.Enqueue(index);
        }

        var stepX = Math.Max(1, box.Width / 64);
        var stepY = Math.Max(1, box.Height / 64);

        for (var x = box.X; x <= box.Right; x += stepX)
        {
            Visit(x, box.Y);
            Visit(x, box.Bottom);
        }

        Visit(box.Right, box.Y);
        Visit(box.Right, box.Bottom);

        for (var y = box.Y; y <= box.Bottom; y += stepY)
        {
            Visit(box.X, y);
            Visit(box.Right, y);
        }

        Visit(box.X, box.Bottom);
        Visit(box.Right, box.Bottom);

        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            var x = index % width;
            var y = index / width;

            if (x > box.X)
                Visit(x - 1, y);
            if (x < box.Right)
                Visit(x + 1, y);
            if (y > box.Y)
                Visit(x, y - 1);
            if (y < box.Bottom)
                Visit(x, y + 1);
        }

        return background;
    }

    /// <summary>The largest patch of non-background pixels inside the box, which is the subject.</summary>
    private static (List<int> Patch, SelectionRect Bounds) LargestPatch(
        bool[] background, int width, SelectionRect box)
    {
        var seen = new bool[background.Length];
        var queue = new Queue<int>();
        var best = new List<int>();
        var current = new List<int>();
        var bounds = new SelectionRect(box.X, box.Y, 0, 0);

        for (var y = box.Y; y <= box.Bottom; y++)
            for (var x = box.X; x <= box.Right; x++)
            {
                var start = y * width + x;
                if (background[start] || seen[start])
                    continue;

                current.Clear();
                int minX = x, maxX = x, minY = y, maxY = y;
                seen[start] = true;
                queue.Enqueue(start);

                while (queue.Count > 0)
                {
                    var index = queue.Dequeue();
                    current.Add(index);

                    var px = index % width;
                    var py = index / width;
                    minX = Math.Min(minX, px);
                    maxX = Math.Max(maxX, px);
                    minY = Math.Min(minY, py);
                    maxY = Math.Max(maxY, py);

                    for (var side = 0; side < 4; side++)
                    {
                        var nx = px + (side == 0 ? -1 : side == 1 ? 1 : 0);
                        var ny = py + (side == 2 ? -1 : side == 3 ? 1 : 0);

                        if (nx < box.X || nx > box.Right || ny < box.Y || ny > box.Bottom)
                            continue;

                        var next = ny * width + nx;
                        if (background[next] || seen[next])
                            continue;

                        seen[next] = true;
                        queue.Enqueue(next);
                    }
                }

                if (current.Count <= best.Count)
                    continue;

                (best, current) = (current, best);
                bounds = new SelectionRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
            }

        return (best, bounds);
    }

    /// <summary>
    /// Fills gaps that are completely enclosed by the subject, such as eyes or a pattern, so they are part of
    /// the object rather than being flattened to the base. Pixels still open to the edge of the patch stay out.
    /// </summary>
    private static void FillHoles(bool[] mask, int width, int height)
    {
        var outside = new bool[mask.Length];
        var queue = new Queue<int>();

        void Visit(int x, int y)
        {
            var index = y * width + x;
            if (mask[index] || outside[index])
                return;

            outside[index] = true;
            queue.Enqueue(index);
        }

        for (var x = 0; x < width; x++)
        {
            Visit(x, 0);
            Visit(x, height - 1);
        }

        for (var y = 0; y < height; y++)
        {
            Visit(0, y);
            Visit(width - 1, y);
        }

        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            var x = index % width;
            var y = index / width;

            if (x > 0)
                Visit(x - 1, y);
            if (x < width - 1)
                Visit(x + 1, y);
            if (y > 0)
                Visit(x, y - 1);
            if (y < height - 1)
                Visit(x, y + 1);
        }

        for (var i = 0; i < mask.Length; i++)
            if (!mask[i] && !outside[i])
                mask[i] = true;
    }

    private static float SquaredDistance(
        ImageData source, int x, int y, (float R, float G, float B)[] references)
    {
        float r = source.GetChannel(x, y, 0);
        float g = source.GetChannel(x, y, 1);
        float b = source.GetChannel(x, y, 2);
        var nearest = float.MaxValue;

        foreach (var reference in references)
        {
            var dr = r - reference.R;
            var dg = g - reference.G;
            var db = b - reference.B;
            var distance = dr * dr + dg * dg + db * db;
            if (distance < nearest)
                nearest = distance;
        }

        return nearest;
    }

    private static float SquaredDistance(
        (float R, float G, float B) colour, (float R, float G, float B) reference)
    {
        var dr = colour.R - reference.R;
        var dg = colour.G - reference.G;
        var db = colour.B - reference.B;
        return dr * dr + dg * dg + db * db;
    }
}
