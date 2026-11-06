namespace Pic2Stl.Core.Depth;

/// <summary>
/// A normalised depth field. 1.0 is the point nearest the camera, 0.0 is the background.
/// </summary>
public sealed class DepthMap
{
    private readonly float[] _values;

    public DepthMap(int width, int height, float[] values)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "Depth map must be at least 1x1.");

        if (values.Length != width * height)
            throw new ArgumentException("Depth buffer must be width * height values.", nameof(values));

        Width = width;
        Height = height;
        _values = values;
    }

    public int Width { get; }

    public int Height { get; }

    public float[] Values => _values;

    public static DepthMap Constant(int width, int height, float value = 0f)
    {
        var values = new float[width * height];
        Array.Fill(values, Math.Clamp(value, 0f, 1f));
        return new DepthMap(width, height, values);
    }

    public float At(int x, int y) =>
        _values[Math.Clamp(y, 0, Height - 1) * Width + Math.Clamp(x, 0, Width - 1)];

    public float Min() => _values.Min();

    public float Max() => _values.Max();

    /// <summary>Stretches the value range between two quantiles so outliers do not flatten the relief.</summary>
    public void NormalizePercentiles(float lowQuantile = 0.02f, float highQuantile = 0.98f)
    {
        if (lowQuantile < 0 || highQuantile > 1 || lowQuantile >= highQuantile)
            throw new ArgumentOutOfRangeException(nameof(lowQuantile), "Quantiles must satisfy 0 <= low < high <= 1.");

        var sorted = (float[])_values.Clone();
        Array.Sort(sorted);
        var low = sorted[(int)Math.Clamp(lowQuantile * (sorted.Length - 1), 0, sorted.Length - 1)];
        var high = sorted[(int)Math.Clamp(highQuantile * (sorted.Length - 1), 0, sorted.Length - 1)];
        var range = high - low;

        for (var i = 0; i < _values.Length; i++)
            _values[i] = range > 1e-6f ? Math.Clamp((_values[i] - low) / range, 0f, 1f) : 0f;
    }

    /// <summary>Bilinear resample; used to match the depth grid to the model grid.</summary>
    public DepthMap ResampleTo(int width, int height)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "Target size must be at least 1x1.");

        var target = new float[width * height];
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

                var top = At(x0, y0) + (At(x0 + 1, y0) - At(x0, y0)) * fx;
                var bottom = At(x0, y0 + 1) + (At(x0 + 1, y0 + 1) - At(x0, y0 + 1)) * fx;
                target[y * width + x] = Math.Clamp(top + (bottom - top) * fy, 0f, 1f);
            }
        }

        return new DepthMap(width, height, target);
    }

    /// <summary>Separable Gaussian blur, which removes the staircase edges of predicted depth.</summary>
    public DepthMap Blurred(float sigma)
    {
        if (sigma <= 0)
            return new DepthMap(Width, Height, (float[])_values.Clone());

        var radius = Math.Max(1, (int)MathF.Ceiling(sigma * 3));
        var kernel = new float[radius * 2 + 1];
        var total = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            var weight = MathF.Exp(-(i * i) / (2 * sigma * sigma));
            kernel[i + radius] = weight;
            total += weight;
        }

        for (var i = 0; i < kernel.Length; i++)
            kernel[i] /= total;

        var horizontal = new float[_values.Length];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var sum = 0f;
                for (var k = -radius; k <= radius; k++)
                    sum += At(x + k, y) * kernel[k + radius];
                horizontal[y * Width + x] = sum;
            }

        var result = new float[_values.Length];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var sum = 0f;
                for (var k = -radius; k <= radius; k++)
                    sum += horizontal[Math.Clamp(y + k, 0, Height - 1) * Width + x] * kernel[k + radius];
                result[y * Width + x] = Math.Clamp(sum, 0f, 1f);
            }

        return new DepthMap(Width, Height, result);
    }
}
