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

    public bool At(int x, int y) => _keep[Math.Clamp(y, 0, Height - 1) * Width + Math.Clamp(x, 0, Width - 1)];

    public int KeptCount => _keep.Count(kept => kept);

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
