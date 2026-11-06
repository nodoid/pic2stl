using Pic2Stl.Core.Colours;

namespace Pic2Stl.Core.Imaging;

/// <summary>A plain RGBA8 bitmap. Kept free of platform imaging so it can be tested without native libraries.</summary>
public sealed class ImageData
{
    private readonly byte[] _pixels;

    public ImageData(int width, int height, byte[] rgbaPixels)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "Image must be at least 1x1.");

        if (rgbaPixels.Length != width * height * 4)
            throw new ArgumentException("Pixel buffer must be width * height * 4 bytes of RGBA.", nameof(rgbaPixels));

        Width = width;
        Height = height;
        _pixels = rgbaPixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels => _pixels;

    public static ImageData Create(int width, int height, Rgb colour, byte alpha = 255)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            pixels[i * 4] = colour.R;
            pixels[i * 4 + 1] = colour.G;
            pixels[i * 4 + 2] = colour.B;
            pixels[i * 4 + 3] = alpha;
        }

        return new ImageData(width, height, pixels);
    }

    public byte GetChannel(int x, int y, int channel)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        return _pixels[(y * Width + x) * 4 + channel];
    }

    public Rgb GetColor(int x, int y) =>
        new(GetChannel(x, y, 0), GetChannel(x, y, 1), GetChannel(x, y, 2));

    /// <summary>Bilinear resample to a new size.</summary>
    public ImageData ResampleTo(int width, int height)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "Target size must be at least 1x1.");

        var target = new byte[width * height * 4];
        var scaleX = (double)Width / width;
        var scaleY = (double)Height / height;

        for (var y = 0; y < height; y++)
        {
            var sy = (y + 0.5) * scaleY - 0.5;
            var y0 = (int)Math.Floor(sy);
            var fy = (float)(sy - y0);

            for (var x = 0; x < width; x++)
            {
                var sx = (x + 0.5) * scaleX - 0.5;
                var x0 = (int)Math.Floor(sx);
                var fx = (float)(sx - x0);

                for (var c = 0; c < 4; c++)
                {
                    var v00 = GetChannel(x0, y0, c);
                    var v10 = GetChannel(x0 + 1, y0, c);
                    var v01 = GetChannel(x0, y0 + 1, c);
                    var v11 = GetChannel(x0 + 1, y0 + 1, c);

                    var top = v00 + (v10 - v00) * fx;
                    var bottom = v01 + (v11 - v01) * fx;
                    target[(y * width + x) * 4 + c] = (byte)Math.Clamp((int)MathF.Round(top + (bottom - top) * fy), 0, 255);
                }
            }
        }

        return new ImageData(width, height, target);
    }
}
