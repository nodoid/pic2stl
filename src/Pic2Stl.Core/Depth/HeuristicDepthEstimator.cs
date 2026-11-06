using Pic2Stl.Core.Imaging;

namespace Pic2Stl.Core.Depth;

/// <summary>
/// Offline fallback used when the neural model is unavailable (no network on first run, or loading error).
/// It fuses luminance, edge strength and a centre bias into a plausible relief. Deterministic, so it is testable.
/// </summary>
public sealed class HeuristicDepthEstimator : IDepthEstimator
{
    private readonly int _longestSide;

    public HeuristicDepthEstimator(int longestSide = 384)
    {
        if (longestSide < 16)
            throw new ArgumentOutOfRangeException(nameof(longestSide), "Longest side must be at least 16 pixels.");

        _longestSide = longestSide;
    }

    public string EngineName => "Offline relief estimate (no AI model)";

    public Task<DepthMap> EstimateAsync(
        ImageData image,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Task.Run(() => Estimate(image, progress, cancellationToken), cancellationToken);
    }

    private DepthMap Estimate(ImageData image, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.1);

        var scale = (double)_longestSide / Math.Max(image.Width, image.Height);
        var width = Math.Max(8, (int)Math.Round(image.Width * scale));
        var height = Math.Max(8, (int)Math.Round(image.Height * scale));
        var resized = image.ResampleTo(width, height);

        var luminance = new float[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                luminance[y * width + x] =
                    (0.2126f * resized.Pixels[i] + 0.7152f * resized.Pixels[i + 1] + 0.0722f * resized.Pixels[i + 2]) / 255f;
            }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0.5);

        // Edge magnitude, normalised, gives the relief its ridges.
        var edges = new float[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var dx = MathF.Abs(Luma(luminance, width, height, x + 1, y) - Luma(luminance, width, height, x - 1, y));
                var dy = MathF.Abs(Luma(luminance, width, height, x, y + 1) - Luma(luminance, width, height, x, y - 1));
                edges[y * width + x] = MathF.Sqrt(dx * dx + dy * dy);
            }

        var edgeMax = edges.Max();
        if (edgeMax > 1e-5f)
            for (var i = 0; i < edges.Length; i++)
                edges[i] /= edgeMax;

        // Centre bias: subjects usually sit towards the middle of the frame.
        var values = new float[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var nx = (x / (double)(width - 1) - 0.5) * 2;
                var ny = (y / (double)(height - 1) - 0.5) * 2;
                var radius = Math.Sqrt(nx * nx + ny * ny) / Math.Sqrt(2);
                var centre = (float)Math.Clamp(1 - radius * 1.15, 0, 1);

                values[y * width + x] =
                    0.55f * luminance[y * width + x] +
                    0.25f * edges[y * width + x] +
                    0.20f * centre;
            }

        cancellationToken.ThrowIfCancellationRequested();
        var depth = new DepthMap(width, height, values);
        depth.NormalizePercentiles();
        depth = depth.Blurred(1.2f);
        progress?.Report(1.0);
        return depth;
    }

    private static float Luma(float[] values, int width, int height, int x, int y) =>
        values[Math.Clamp(y, 0, height - 1) * width + Math.Clamp(x, 0, width - 1)];
}
