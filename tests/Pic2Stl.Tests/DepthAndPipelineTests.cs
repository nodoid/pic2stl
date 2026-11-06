using Pic2Stl.Core.Colours;
using Pic2Stl.Core.Depth;
using Pic2Stl.Core.Imaging;
using Pic2Stl.Core.Pipeline;

namespace Pic2Stl.Tests;

[TestFixture]
public class ImageDataTests
{
    [Test]
    public void Constructor_ValidatesBufferSize()
    {
        Assert.That(() => new ImageData(2, 2, new byte[3]), Throws.ArgumentException);
        Assert.That(() => new ImageData(0, 2, Array.Empty<byte>()), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void ResampleTo_ChangesSizeAndKeepsColourSides()
    {
        var source = TestImages.TwoColourSides(32, 16);
        var resized = source.ResampleTo(8, 4);

        Assert.Multiple(() =>
        {
            Assert.That(resized.Width, Is.EqualTo(8));
            Assert.That(resized.Height, Is.EqualTo(4));
            Assert.That(resized.GetColor(0, 2).R, Is.GreaterThan(150));
            Assert.That(resized.GetColor(7, 2).B, Is.GreaterThan(150));
        });
    }

    [Test]
    public void GetChannel_ClampsOutOfRangeAccess()
    {
        var image = ImageData.Create(3, 3, new Rgb(9, 8, 7));

        Assert.Multiple(() =>
        {
            Assert.That(image.GetChannel(-1, -1, 0), Is.EqualTo(9));
            Assert.That(image.GetChannel(99, 99, 2), Is.EqualTo(7));
        });
    }
}

[TestFixture]
public class DepthMapTests
{
    [Test]
    public void NormalizePercentiles_StretchesToFullRange()
    {
        var values = new float[100];
        for (var i = 0; i < values.Length; i++)
            values[i] = 10 + i * 0.1f;

        var map = new DepthMap(10, 10, values);
        map.NormalizePercentiles(0f, 1f);

        Assert.Multiple(() =>
        {
            Assert.That(map.Min(), Is.EqualTo(0f).Within(1e-5));
            Assert.That(map.Max(), Is.EqualTo(1f).Within(1e-5));
        });
    }

    [Test]
    public void NormalizePercentiles_FlatFieldBecomesZero()
    {
        var map = DepthMap.Constant(4, 4, 0.3f);
        map.NormalizePercentiles();

        Assert.That(map.Values.All(v => v == 0f), Is.True);
    }

    [Test]
    public void ResampleTo_ReportsRequestedSizeAndKeepsRange()
    {
        var map = TestImages.Bump(24, 18).ResampleTo(12, 9);

        Assert.Multiple(() =>
        {
            Assert.That(map.Width, Is.EqualTo(12));
            Assert.That(map.Height, Is.EqualTo(9));
            Assert.That(map.Values, Has.All.InRange(0f, 1f));
        });
    }

    [Test]
    public void Blurred_ReducesContrast()
    {
        var source = TestImages.Bump(20, 20);
        var blurred = source.Blurred(3f);

        static double Variance(DepthMap map)
        {
            var mean = map.Values.Average();
            return map.Values.Select(v => (v - mean) * (v - mean)).Average();
        }

        Assert.That(Variance(blurred), Is.LessThan(Variance(source)));
    }

    [Test]
    public void Blurred_WithZeroSigmaReturnsCopy()
    {
        var source = TestImages.Bump(6, 6);
        var copy = source.Blurred(0);

        Assert.That(copy.Values, Is.EqualTo(source.Values));
        Assert.That(copy, Is.Not.SameAs(source));
    }

    [Test]
    public void Constructor_ValidatesDimensions()
    {
        Assert.That(() => new DepthMap(2, 2, new float[3]), Throws.ArgumentException);
        Assert.That(() => new DepthMap(0, 2, Array.Empty<float>()), Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}

[TestFixture]
public class HeuristicDepthEstimatorTests
{
    [Test]
    public async Task EstimateAsync_IsDeterministicAndNormalised()
    {
        var image = TestImages.FourQuadrants();
        var estimator = new HeuristicDepthEstimator(64);

        var first = await estimator.EstimateAsync(image);
        var second = await estimator.EstimateAsync(image);

        Assert.Multiple(() =>
        {
            Assert.That(first.Width, Is.EqualTo(64));
            Assert.That(first.Height, Is.EqualTo(64));
            Assert.That(first.Values, Has.All.InRange(0f, 1f));
            Assert.That(first.Values.Max(), Is.GreaterThan(0.5f));
            Assert.That(second.Values, Is.EqualTo(first.Values));
        });
    }

    [Test]
    public async Task EstimateAsync_ReportsProgressUpToOne()
    {
        var reports = new ProgressCollector<double>();
        var estimator = new HeuristicDepthEstimator(32);

        await estimator.EstimateAsync(TestImages.TwoColourSides(16, 16), reports);

        var values = await reports.WaitForAsync(r => r.Count > 0 && r[^1] >= 1.0);
        Assert.That(values, Is.Not.Empty);
        Assert.That(values[^1], Is.EqualTo(1.0).Within(1e-6));
    }

    [Test]
    public void EngineNameIsSet()
    {
        Assert.That(new HeuristicDepthEstimator().EngineName, Is.Not.Empty);
    }
}

[TestFixture]
public class MeshGenerationPipelineTests
{
    private sealed class FixedDepthEstimator : IDepthEstimator
    {
        public string EngineName => "test estimator";

        public Task<DepthMap> EstimateAsync(ImageData image, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Report(0.5);
            progress?.Report(1.0);
            return Task.FromResult(TestImages.Bump(48, 36));
        }
    }

    [Test]
    public async Task GenerateAsync_ProducesPrintableMeshAndPalette()
    {
        var pipeline = new MeshGenerationPipeline(new FixedDepthEstimator());
        var options = new GenerationOptions { ColourCount = 4, WidthMm = 70, ExportDetail = 48, PreviewDetail = 16 };

        var result = await pipeline.GenerateAsync(TestImages.FourQuadrants(), options);

        TestImages.AssertWatertight(result.Mesh);
        TestImages.AssertOutwardFacing(result.Mesh);
        Assert.Multiple(() =>
        {
            Assert.That(result.Palette.Count, Is.InRange(1, 4));
            Assert.That(result.TriangleCount, Is.GreaterThan(100));
            Assert.That(result.PreviewTriangleCount, Is.LessThan(result.TriangleCount));
            Assert.That(result.EngineName, Is.EqualTo("test estimator"));
            Assert.That(result.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.Zero));
        });
    }

    [Test]
    public async Task GenerateAsync_ReportsProgressFromStartToHundred()
    {
        var reports = new ProgressCollector<GenerationProgress>();
        var pipeline = new MeshGenerationPipeline(new FixedDepthEstimator());
        var options = new GenerationOptions { ExportDetail = 32, PreviewDetail = 12 };

        await pipeline.GenerateAsync(TestImages.FourQuadrants(), options, reports);

        var values = (await reports.WaitForAsync(r => r.Count > 0 && r[^1].Percent == 100))
            .Select(p => p.Percent)
            .ToList();

        Assert.That(values, Is.Not.Empty);
        Assert.That(values, Is.Ordered, "Progress must never go backwards.");
        Assert.That(values[^1], Is.EqualTo(100));
        Assert.That(values.All(p => p is >= 0 and <= 100), Is.True);
    }

    [Test]
    public async Task GenerateAsync_HonoursSixteenColours()
    {
        var image = TestImages.FourQuadrants();
        var pipeline = new MeshGenerationPipeline(new FixedDepthEstimator());
        var result = await pipeline.GenerateAsync(
            image,
            new GenerationOptions { ColourCount = 16, ExportDetail = 40, PreviewDetail = 12 });

        Assert.That(result.Palette.Count, Is.InRange(1, 16));
    }

    [Test]
    public async Task GenerateAsync_WidthOptionSetsModelSize()
    {
        var pipeline = new MeshGenerationPipeline(new FixedDepthEstimator());
        var result = await pipeline.GenerateAsync(
            TestImages.FourQuadrants(),
            new GenerationOptions { WidthMm = 120, ExportDetail = 24, PreviewDetail = 10 });

        var (min, max) = result.Mesh.Bounds();
        Assert.That(max.X - min.X, Is.EqualTo(120f).Within(0.01f));
    }

    [Test]
    public void Validate_RejectsOutOfRangeSettings()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => new GenerationOptions { ColourCount = 0 }.Validate(), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new GenerationOptions { WidthMm = 500 }.Validate(), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new GenerationOptions { ExportDetail = 4 }.Validate(), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new GenerationOptions { PreviewDetail = 2 }.Validate(), Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void GridSize_PreservesAspectRatio()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MeshGenerationPipeline.GridSize(4000, 3000, 200), Is.EqualTo((200, 150)));
            Assert.That(MeshGenerationPipeline.GridSize(3000, 4000, 200), Is.EqualTo((150, 200)));
            Assert.That(MeshGenerationPipeline.GridSize(16, 9, 200), Is.EqualTo((200, 113)));
        });
    }
}
