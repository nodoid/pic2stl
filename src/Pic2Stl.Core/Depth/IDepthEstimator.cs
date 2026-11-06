using Pic2Stl.Core.Imaging;

namespace Pic2Stl.Core.Depth;

/// <summary>Produces a normalised depth field for a photo.</summary>
public interface IDepthEstimator
{
    /// <summary>Human readable name of the engine, shown on screen.</summary>
    string EngineName { get; }

    /// <summary>
    /// Estimates depth. Progress is reported in the 0..1 range.
    /// </summary>
    Task<DepthMap> EstimateAsync(ImageData image, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}
