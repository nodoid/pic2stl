using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Pic2Stl.Core.Imaging;

namespace Pic2Stl.Core.Depth;

/// <summary>
/// Runs a monocular depth network (Depth Anything V2 Small, quantised) locally with ONNX Runtime.
/// The model is free and Apache-2.0 licensed; inference happens on the device, nothing is uploaded.
/// </summary>
public sealed class OnnxDepthEstimator : IDepthEstimator, IDisposable
{
    /// <summary>ImageNet statistics used by the Depth Anything models.</summary>
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    /// <summary>The ViT patch size; both network dimensions must be multiples of this.</summary>
    private const int PatchSize = 14;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _longestSide;
    private bool _disposed;

    public OnnxDepthEstimator(string modelPath, int longestSide = 518)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Depth model not found.", modelPath);

        if (longestSide < PatchSize * 4)
            throw new ArgumentOutOfRangeException(nameof(longestSide), $"Longest side must be at least {PatchSize * 4} pixels.");

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount - 1, 1, 4),
        };

        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
        _longestSide = longestSide;
    }

    public string EngineName => "Depth Anything V2 (on-device AI)";

    public Task<DepthMap> EstimateAsync(
        ImageData image,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Task.Run(() => RunInference(image, progress, cancellationToken), cancellationToken);
    }

    private DepthMap RunInference(ImageData image, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.05);

        // Keep the aspect ratio, scale so the longest side matches the network size, and stay on patch multiples.
        var scale = (double)_longestSide / Math.Max(image.Width, image.Height);
        var width = Math.Max(PatchSize, (int)Math.Round(image.Width * scale / PatchSize) * PatchSize);
        var height = Math.Max(PatchSize, (int)Math.Round(image.Height * scale / PatchSize) * PatchSize);

        var resized = image.ResampleTo(width, height);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0.2);

        var tensor = new DenseTensor<float>([1, 3, height, width]);
        var pixels = resized.Pixels;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                tensor[0, 0, y, x] = (pixels[i] / 255f - Mean[0]) / Std[0];
                tensor[0, 1, y, x] = (pixels[i + 1] / 255f - Mean[1]) / Std[1];
                tensor[0, 2, y, x] = (pixels[i + 2] / 255f - Mean[2]) / Std[2];
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0.3);

        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
        var output = results.First().AsTensor<float>();
        progress?.Report(0.9);

        // Output is [1, h, w] (some exports add a channel axis). Values are relative inverse depth: larger = nearer.
        var outHeight = output.Dimensions[^2];
        var outWidth = output.Dimensions[^1];
        if (outWidth != width || outHeight != height)
            throw new InvalidOperationException(
                $"Depth model returned {outWidth}x{outHeight} but {width}x{height} was requested.");

        var values = new float[outWidth * outHeight];
        for (var y = 0; y < outHeight; y++)
            for (var x = 0; x < outWidth; x++)
                values[y * outWidth + x] = output[0, y, x];

        var depth = new DepthMap(outWidth, outHeight, values);
        depth.NormalizePercentiles(0.01f, 0.99f);
        progress?.Report(1.0);
        return depth;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _session.Dispose();
    }
}
