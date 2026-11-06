using Pic2Stl.Core.Colours;
using Pic2Stl.Core.Depth;
using Pic2Stl.Core.Geometry;
using Pic2Stl.Core.Imaging;
using Pic2Stl.Core.Meshing;

namespace Pic2Stl.Core.Pipeline;

/// <summary>User-adjustable generation settings.</summary>
public sealed class GenerationOptions
{
    /// <summary>Print colours: 4 by default, 16 when the tombstone-style palette is enabled.</summary>
    public int ColourCount { get; init; } = 4;

    /// <summary>Width of the finished model in millimetres.</summary>
    public float WidthMm { get; init; } = 80f;

    /// <summary>Peak relief height in millimetres; 0 derives a sensible value from the width.</summary>
    public float ReliefMm { get; init; }

    /// <summary>Longest side of the export grid, in samples. Higher means more detail and larger files.</summary>
    public int ExportDetail { get; init; } = 200;

    /// <summary>Longest side of the on-screen preview grid, in samples.</summary>
    public int PreviewDetail { get; init; } = 72;

    public int EffectiveColourCount => Math.Clamp(ColourCount, 1, 16);

    public float EffectiveReliefMm => ReliefMm > 0 ? ReliefMm : MathF.Max(2f, WidthMm * 0.22f);

    public void Validate()
    {
        if (ColourCount is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(ColourCount), "Colour count must be between 1 and 16.");

        if (WidthMm is < 10f or > 300f)
            throw new ArgumentOutOfRangeException(nameof(WidthMm), "Width must be between 10 and 300 mm.");

        if (ExportDetail is < 16 or > 512)
            throw new ArgumentOutOfRangeException(nameof(ExportDetail), "Export detail must be between 16 and 512 samples.");

        if (PreviewDetail is < 8 or > 256)
            throw new ArgumentOutOfRangeException(nameof(PreviewDetail), "Preview detail must be between 8 and 256 samples.");
    }
}

/// <summary>A progress snapshot: percentage 0-100 plus what the app is currently doing.</summary>
public readonly record struct GenerationProgress(int Percent, string Stage);

/// <summary>Everything the UI and the exporters need from one run.</summary>
public sealed record GenerationResult(
    Mesh Mesh,
    Mesh PreviewMesh,
    Palette Palette,
    DepthMap Depth,
    string EngineName,
    TimeSpan Elapsed)
{
    public int TriangleCount => Mesh.Triangles.Count;

    public int PreviewTriangleCount => PreviewMesh.Triangles.Count;
}

/// <summary>
/// Orchestrates depth estimation, mesh construction and colour quantisation. Progress runs 0-100 in the
/// same order the stages execute, so the on-screen bar is a real measure of work done.
/// </summary>
public sealed class MeshGenerationPipeline
{
    private readonly IDepthEstimator _depthEstimator;
    private readonly TimeProvider _timeProvider;

    public MeshGenerationPipeline(IDepthEstimator depthEstimator, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(depthEstimator);
        _depthEstimator = depthEstimator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string EngineName => _depthEstimator.EngineName;

    public async Task<GenerationResult> GenerateAsync(
        ImageData source,
        GenerationOptions options,
        IProgress<GenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var started = _timeProvider.GetTimestamp();

        void Report(int percent, string stage) => progress?.Report(new GenerationProgress(Math.Clamp(percent, 0, 100), stage));

        Report(1, "Reading photo");

        // Depth estimation is the expensive stage; give it most of the bar. The sink is synchronous on purpose:
        // Progress<T> would post to the captured context and let late reports arrive after later stages,
        // making the bar jump backwards.
        var depthProgress = new DepthStageProgress(progress);
        var depth = await _depthEstimator
            .EstimateAsync(source, depthProgress, cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        Report(58, "Smoothing depth");

        var smoothed = depth.Blurred(0.8f);

        // Export grid.
        var (exportWidth, exportHeight) = GridSize(source.Width, source.Height, options.ExportDetail);
        var exportDepth = smoothed.ResampleTo(exportWidth, exportHeight);
        var exportImage = source.ResampleTo(exportWidth, exportHeight);

        Report(66, "Choosing print colours");
        var quantised = PaletteBuilder.Build(exportImage, options.EffectiveColourCount);

        cancellationToken.ThrowIfCancellationRequested();
        Report(74, "Building solid model");

        var meshOptions = new SolidMeshOptions
        {
            WidthMm = options.WidthMm,
            ReliefMm = options.EffectiveReliefMm,
            BackRatio = 0.45f,
            MinThicknessMm = MathF.Max(1.2f, options.WidthMm * 0.025f),
        };

        var mesh = await Task.Run(() => SolidMesher.Build(exportDepth, quantised.Map, meshOptions), cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        Report(88, "Preparing 3D view");

        var (previewWidth, previewHeight) = GridSize(source.Width, source.Height, options.PreviewDetail);
        var previewDepth = smoothed.ResampleTo(previewWidth, previewHeight);
        var previewImage = source.ResampleTo(previewWidth, previewHeight);
        var previewMap = PaletteBuilder.Map(previewImage, quantised.Palette);
        var previewMesh = SolidMesher.Build(previewDepth, previewMap, meshOptions);

        var elapsed = _timeProvider.GetElapsedTime(started);
        Report(100, "Done");

        return new GenerationResult(mesh, previewMesh, quantised.Palette, smoothed, _depthEstimator.EngineName, elapsed);
    }

    /// <summary>Maps the 0..1 depth-stage progress onto the pipeline's 5..55 percent window.</summary>
    private sealed class DepthStageProgress(IProgress<GenerationProgress>? sink) : IProgress<double>
    {
        public void Report(double value)
        {
            var percent = 5 + (int)(Math.Clamp(value, 0, 1) * 50);
            sink?.Report(new GenerationProgress(percent, "Estimating 3D depth with AI"));
        }
    }

    /// <summary>Picks a grid whose longest side equals <paramref name="longestSide"/> and keeps the aspect ratio.</summary>
    public static (int Width, int Height) GridSize(int imageWidth, int imageHeight, int longestSide)
    {
        if (imageWidth < 1 || imageHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Image must be at least 1x1.");

        if (longestSide < 2)
            throw new ArgumentOutOfRangeException(nameof(longestSide), "Longest side must be at least 2.");

        var scale = (double)longestSide / Math.Max(imageWidth, imageHeight);
        var width = Math.Max(2, (int)Math.Round(imageWidth * scale, MidpointRounding.AwayFromZero));
        var height = Math.Max(2, (int)Math.Round(imageHeight * scale, MidpointRounding.AwayFromZero));
        return (width, height);
    }
}
