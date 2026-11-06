using Pic2Stl.Core.Imaging;

namespace Pic2Stl.Core.Colours;

/// <summary>An ordered list of print colours.</summary>
public sealed class Palette
{
    public Palette(IReadOnlyList<Rgb> colours)
    {
        if (colours.Count == 0)
            throw new ArgumentException("A palette needs at least one colour.", nameof(colours));

        Colours = colours;
    }

    public IReadOnlyList<Rgb> Colours { get; }

    public int Count => Colours.Count;
}

/// <summary>A palette index for every pixel of an image.</summary>
public sealed class MaterialMap
{
    private readonly byte[] _indices;

    public MaterialMap(int width, int height, byte[] indices)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "Material map must be at least 1x1.");

        if (indices.Length != width * height)
            throw new ArgumentException("Material map must have width * height entries.", nameof(indices));

        Width = width;
        Height = height;
        _indices = indices;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Indices => _indices;

    public byte At(int x, int y) => _indices[Math.Clamp(y, 0, Height - 1) * Width + Math.Clamp(x, 0, Width - 1)];
}

/// <summary>A palette plus the per-pixel mapping that produced it.</summary>
public sealed record PaletteResult(Palette Palette, MaterialMap Map);

/// <summary>
/// Median-cut colour quantiser. Printing filaments come in a limited number of spools, so the relief's
/// surface colours are reduced to at most four (or sixteen) entries.
/// </summary>
public static class PaletteBuilder
{
    private const int BitsPerChannel = 5;
    private const int BinsPerChannel = 1 << BitsPerChannel;
    private const int BinCount = BinsPerChannel * BinsPerChannel * BinsPerChannel;

    public static PaletteResult Build(ImageData image, int maxColours)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (maxColours < 1 || maxColours > 255)
            throw new ArgumentOutOfRangeException(nameof(maxColours), "Colour count must be between 1 and 255.");

        var counts = new int[BinCount];
        var sumR = new long[BinCount];
        var sumG = new long[BinCount];
        var sumB = new long[BinCount];

        var pixels = image.Pixels;
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            if (pixels[i * 4 + 3] < 8)
                continue;

            var r = pixels[i * 4];
            var g = pixels[i * 4 + 1];
            var b = pixels[i * 4 + 2];
            var bin = ((r >> (8 - BitsPerChannel)) << (BitsPerChannel * 2))
                      | ((g >> (8 - BitsPerChannel)) << BitsPerChannel)
                      | (b >> (8 - BitsPerChannel));

            counts[bin]++;
            sumR[bin] += r;
            sumG[bin] += g;
            sumB[bin] += b;
        }

        var occupied = new List<int>();
        for (var bin = 0; bin < BinCount; bin++)
            if (counts[bin] > 0)
                occupied.Add(bin);

        if (occupied.Count == 0)
        {
            // Fully transparent image: fall back to a single black spool.
            var fallback = new Palette(new[] { new Rgb(0, 0, 0) });
            return new PaletteResult(fallback, new MaterialMap(image.Width, image.Height, new byte[image.Width * image.Height]));
        }

        var bins = occupied.ToArray();
        var boxes = new List<(int Start, int End)> { (0, bins.Length) };

        while (boxes.Count < maxColours)
        {
            var bestIndex = -1;
            var bestScore = 0.0;
            for (var i = 0; i < boxes.Count; i++)
            {
                var (start, end) = boxes[i];
                if (end - start < 2)
                    continue;

                var score = BoxScore(bins, start, end, counts);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
                break;

            var (boxStart, boxEnd) = boxes[bestIndex];
            var channel = DominantChannel(bins, boxStart, boxEnd, sumR, sumG, sumB, counts);
            Array.Sort(bins, boxStart, boxEnd - boxStart, new BinComparer(channel));

            long total = 0;
            for (var i = boxStart; i < boxEnd; i++)
                total += counts[bins[i]];

            long running = 0;
            var split = boxStart + 1;
            for (var i = boxStart; i < boxEnd - 1; i++)
            {
                running += counts[bins[i]];
                split = i + 1;
                if (running * 2 >= total)
                    break;
            }

            boxes.RemoveAt(bestIndex);
            boxes.Insert(bestIndex, (boxStart, split));
            boxes.Insert(bestIndex + 1, (split, boxEnd));
        }

        var colours = new List<Rgb>();
        foreach (var (start, end) in boxes)
        {
            long weight = 0, r = 0, g = 0, b = 0;
            for (var i = start; i < end; i++)
            {
                var bin = bins[i];
                weight += counts[bin];
                r += sumR[bin];
                g += sumG[bin];
                b += sumB[bin];
            }

            if (weight == 0)
                continue;

            var colour = new Rgb(
                (byte)Math.Clamp((int)Math.Round((double)r / weight), 0, 255),
                (byte)Math.Clamp((int)Math.Round((double)g / weight), 0, 255),
                (byte)Math.Clamp((int)Math.Round((double)b / weight), 0, 255));

            if (!colours.Contains(colour))
                colours.Add(colour);
        }

        if (colours.Count == 0)
            colours.Add(new Rgb(0, 0, 0));

        var palette = new Palette(colours);
        return new PaletteResult(palette, Map(image, palette));
    }

    /// <summary>Maps every pixel of <paramref name="image"/> onto its nearest palette colour.</summary>
    public static MaterialMap Map(ImageData image, Palette palette)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(palette);

        var indices = new byte[image.Width * image.Height];
        var colours = palette.Colours;

        for (var i = 0; i < indices.Length; i++)
        {
            var r = image.Pixels[i * 4];
            var g = image.Pixels[i * 4 + 1];
            var b = image.Pixels[i * 4 + 2];

            var best = 0;
            var bestDistance = int.MaxValue;
            for (var c = 0; c < colours.Count; c++)
            {
                var dr = r - colours[c].R;
                var dg = g - colours[c].G;
                var db = b - colours[c].B;
                var distance = dr * dr + dg * dg + db * db;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = c;
                }
            }

            indices[i] = (byte)best;
        }

        return new MaterialMap(image.Width, image.Height, indices);
    }

    private static double BoxScore(int[] bins, int start, int end, int[] counts)
    {
        long population = 0;
        int minR = 31, maxR = 0, minG = 31, maxG = 0, minB = 31, maxB = 0;
        for (var i = start; i < end; i++)
        {
            var bin = bins[i];
            population += counts[bin];
            var r = bin >> (BitsPerChannel * 2);
            var g = (bin >> BitsPerChannel) & (BinsPerChannel - 1);
            var b = bin & (BinsPerChannel - 1);
            minR = Math.Min(minR, r);
            maxR = Math.Max(maxR, r);
            minG = Math.Min(minG, g);
            maxG = Math.Max(maxG, g);
            minB = Math.Min(minB, b);
            maxB = Math.Max(maxB, b);
        }

        var extent = Math.Max(maxR - minR, Math.Max(maxG - minG, maxB - minB));
        return (extent + 1.0) * Math.Sqrt(population);
    }

    private static int DominantChannel(int[] bins, int start, int end, long[] sumR, long[] sumG, long[] sumB, int[] counts)
    {
        double weightR = 0, weightG = 0, weightB = 0;
        long total = 0;
        for (var i = start; i < end; i++)
        {
            var bin = bins[i];
            var count = counts[bin];
            weightR += count * (double)sumR[bin] / count;
            weightG += count * (double)sumG[bin] / count;
            weightB += count * (double)sumB[bin] / count;
            total += count;
        }

        if (total == 0)
            return 0;

        var meanR = weightR / total;
        var meanG = weightG / total;
        var meanB = weightB / total;

        double varianceR = 0, varianceG = 0, varianceB = 0;
        for (var i = start; i < end; i++)
        {
            var bin = bins[i];
            var count = counts[bin];
            var r = (double)sumR[bin] / count;
            var g = (double)sumG[bin] / count;
            var b = (double)sumB[bin] / count;
            varianceR += count * (r - meanR) * (r - meanR);
            varianceG += count * (g - meanG) * (g - meanG);
            varianceB += count * (b - meanB) * (b - meanB);
        }

        if (varianceR >= varianceG && varianceR >= varianceB)
            return 0;
        return varianceG >= varianceB ? 1 : 2;
    }

    private sealed class BinComparer(int channel) : IComparer<int>
    {
        public int Compare(int x, int y) => ChannelOf(x).CompareTo(ChannelOf(y));

        private int ChannelOf(int bin) => channel switch
        {
            0 => bin >> (BitsPerChannel * 2),
            1 => (bin >> BitsPerChannel) & (BinsPerChannel - 1),
            _ => bin & (BinsPerChannel - 1),
        };
    }
}
