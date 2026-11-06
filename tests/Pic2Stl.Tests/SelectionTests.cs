using Pic2Stl.Core.Colours;
using Pic2Stl.Core.Depth;
using Pic2Stl.Core.Imaging;
using Pic2Stl.Core.Pipeline;

namespace Pic2Stl.Tests;

[TestFixture]
public class SelectionRectTests
{
    [Test]
    public void FromCorners_AcceptsADragInAnyDirection()
    {
        var downRight = SelectionRect.FromCorners(2, 3, 9, 11, 20, 20);
        var upLeft = SelectionRect.FromCorners(9, 11, 2, 3, 20, 20);

        Assert.Multiple(() =>
        {
            Assert.That(downRight, Is.EqualTo(upLeft));
            Assert.That(downRight, Is.EqualTo(new SelectionRect(2, 3, 8, 9)));
            Assert.That(downRight.Right, Is.EqualTo(9));
            Assert.That(downRight.Bottom, Is.EqualTo(11));
        });
    }

    [Test]
    public void FromCorners_ClampsToTheImage()
    {
        var rect = SelectionRect.FromCorners(-50, -50, 500, 500, 10, 8);

        Assert.Multiple(() =>
        {
            Assert.That(rect, Is.EqualTo(new SelectionRect(0, 0, 10, 8)));
            Assert.That(rect.IsEmpty, Is.False);
        });
    }

    [Test]
    public void FromFractions_MapsTheWholePhotoToEveryPixel()
    {
        var rect = SelectionRect.FromFractions(0, 0, 1, 1, 100, 50);

        Assert.That(rect, Is.EqualTo(new SelectionRect(0, 0, 100, 50)));
    }

    [Test]
    public void FromFractions_ClampsFractionsOutsideThePhoto()
    {
        var rect = SelectionRect.FromFractions(0.25, 0.5, 1.5, -0.5, 80, 40);

        Assert.Multiple(() =>
        {
            Assert.That(rect.X, Is.EqualTo(20));
            Assert.That(rect.Y, Is.EqualTo(0));
            Assert.That(rect.Right, Is.EqualTo(79));
            Assert.That(rect.Bottom, Is.EqualTo(20), "Half way down the photo is pixel 20 of 40.");
        });
    }
}

[TestFixture]
public class ImageCropTests
{
    [Test]
    public void Crop_CopiesExactlyTheRequestedRegion()
    {
        var source = TestImages.FourQuadrants(40, 40);
        var cropped = source.Crop(new SelectionRect(0, 0, 20, 20));

        Assert.Multiple(() =>
        {
            Assert.That(cropped.Width, Is.EqualTo(20));
            Assert.That(cropped.Height, Is.EqualTo(20));
            Assert.That(cropped.GetColor(0, 0), Is.EqualTo(new Rgb(255, 0, 0)));
            Assert.That(cropped.GetColor(19, 19), Is.EqualTo(new Rgb(255, 0, 0)));
        });
    }

    [Test]
    public void Crop_KeepsAllFourQuadrantsWhenGivenTheCentre()
    {
        var source = TestImages.FourQuadrants(40, 40);
        var cropped = source.Crop(new SelectionRect(10, 10, 20, 20));

        Assert.Multiple(() =>
        {
            Assert.That(cropped.GetColor(0, 0), Is.EqualTo(new Rgb(255, 0, 0)));
            Assert.That(cropped.GetColor(19, 0), Is.EqualTo(new Rgb(0, 255, 0)));
            Assert.That(cropped.GetColor(0, 19), Is.EqualTo(new Rgb(0, 0, 255)));
            Assert.That(cropped.GetColor(19, 19), Is.EqualTo(new Rgb(255, 255, 0)));
        });
    }

    [Test]
    public void Crop_ClampsAnOversizeSelection()
    {
        var cropped = TestImages.Gradient(12, 9).Crop(new SelectionRect(-4, -4, 100, 100));

        Assert.Multiple(() =>
        {
            Assert.That(cropped.Width, Is.EqualTo(12));
            Assert.That(cropped.Height, Is.EqualTo(9));
        });
    }

    [Test]
    public void Crop_DoesNotAliasTheSourcePixels()
    {
        var source = TestImages.TwoColourSides(8, 4);
        var cropped = source.Crop(new SelectionRect(0, 0, 4, 4));

        cropped.Pixels[0] = 1;
        Assert.That(source.GetChannel(0, 0, 0), Is.EqualTo(220));
    }
}

[TestFixture]
public class ObjectSelectorTests
{
    private static readonly Rgb Background = new(35, 70, 130);
    private static readonly Rgb Subject = new(220, 60, 40);

    /// <summary>A red disc on a blue field, with a lighter sky band at the top of the scene.</summary>
    private static ImageData SubjectOnBackground(int width = 60, int height = 40)
    {
        var pixels = new byte[width * height * 4];
        var centreX = width / 2.0;
        var centreY = height / 2.0;
        var radius = Math.Min(width, height) * 0.3;

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var inSubject = (x - centreX) * (x - centreX) + (y - centreY) * (y - centreY) <= radius * radius;
                var colour = inSubject ? Subject : Background;

                pixels[i] = colour.R;
                pixels[i + 1] = colour.G;
                pixels[i + 2] = colour.B;
                pixels[i + 3] = 255;
            }

        return new ImageData(width, height, pixels);
    }

    [Test]
    public void Select_KeepsTheSubjectAndDropsTheCornersOfTheBox()
    {
        var image = SubjectOnBackground();
        var box = new SelectionRect(10, 5, 40, 30);

        var selection = ObjectSelector.Select(image, box);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Bounds.Width, Is.LessThan(box.Width), "The outline should be tighter than the box.");
            Assert.That(selection.Bounds.Height, Is.LessThan(box.Height));
            Assert.That(selection.Mask.Width, Is.EqualTo(selection.Bounds.Width));
            Assert.That(selection.Mask.Height, Is.EqualTo(selection.Bounds.Height));
            Assert.That(selection.Mask.At(selection.Mask.Width / 2, selection.Mask.Height / 2), Is.True);
            Assert.That(selection.Mask.At(0, 0), Is.False, "A corner of the bounding box is background.");
            Assert.That(selection.Mask.At(selection.Mask.Width - 1, selection.Mask.Height - 1), Is.False);
        });
    }

    [Test]
    public void Select_FindsTheSubjectWhereItActuallyIs()
    {
        var image = SubjectOnBackground();
        var selection = ObjectSelector.Select(image, new SelectionRect(0, 0, 60, 40));

        // The disc is centred on (30, 20) with a radius of 12, so its bounds are 18..42 by 8..32.
        Assert.Multiple(() =>
        {
            Assert.That(selection.Bounds.X, Is.InRange(17, 19));
            Assert.That(selection.Bounds.Y, Is.InRange(7, 9));
            Assert.That(selection.Bounds.Right, Is.InRange(41, 43));
            Assert.That(selection.Bounds.Bottom, Is.InRange(31, 33));
        });
    }

    [Test]
    public void Select_FallsBackToTheWholeBoxWhenTheColourIsUniform()
    {
        var image = ImageData.Create(40, 30, Background);
        var box = new SelectionRect(5, 5, 20, 15);

        var selection = ObjectSelector.Select(image, box);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Bounds, Is.EqualTo(box));
            Assert.That(selection.Mask.IsEverything, Is.True);
        });
    }

    [Test]
    public void Select_IgnoresSpecksThatAreNotConnectedToTheSubject()
    {
        var pixels = new byte[40 * 30 * 4];
        for (var i = 0; i < 40 * 30; i++)
        {
            pixels[i * 4] = Background.R;
            pixels[i * 4 + 1] = Background.G;
            pixels[i * 4 + 2] = Background.B;
            pixels[i * 4 + 3] = 255;
        }

        // A big subject in the middle and a tiny speck of the same colour two pixels away from it.
        void Paint(int x, int y)
        {
            var i = (y * 40 + x) * 4;
            pixels[i] = Subject.R;
            pixels[i + 1] = Subject.G;
            pixels[i + 2] = Subject.B;
        }

        for (var y = 10; y < 20; y++)
            for (var x = 10; x < 20; x++)
                Paint(x, y);

        Paint(22, 15);

        var selection = ObjectSelector.Select(new ImageData(40, 30, pixels), new SelectionRect(8, 8, 16, 16));

        Assert.Multiple(() =>
        {
            Assert.That(selection.Bounds, Is.EqualTo(new SelectionRect(10, 10, 10, 10)));
            Assert.That(selection.Mask.KeptCount, Is.EqualTo(100));
        });
    }

    [Test]
    public void Select_ValidatesItsInput()
    {
        var image = SubjectOnBackground();

        Assert.Multiple(() =>
        {
            Assert.That(() => ObjectSelector.Select(image, new SelectionRect(0, 0, 1, 1)), Throws.ArgumentException);
            Assert.That(
                () => ObjectSelector.Select(image, new SelectionRect(0, 0, 10, 10), 0f),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => ObjectSelector.Select(image, new SelectionRect(0, 0, 10, 10), 1.5f),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }
}

[TestFixture]
public class SelectionMaskTests
{
    [Test]
    public void ResampleTo_KeepsTheOutlineWhereItBelongs()
    {
        var mask = new SelectionMask(4, 2, [false, false, true, true, false, false, true, true]);
        var half = mask.ResampleTo(2, 1);

        Assert.Multiple(() =>
        {
            Assert.That(half.Width, Is.EqualTo(2));
            Assert.That(half.At(0, 0), Is.False);
            Assert.That(half.At(1, 0), Is.True);
        });
    }

    [Test]
    public void Constructor_ValidatesItsBuffer()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => new SelectionMask(2, 2, new bool[3]), Throws.ArgumentException);
            Assert.That(() => new SelectionMask(0, 2, []), Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }
}

[TestFixture]
public class PngEncoderTests
{
    [Test]
    public void Encode_RoundTripsThroughTheDecoder()
    {
        var original = TestImages.Gradient(13, 7);
        var encoded = PngEncoder.Encode(original);
        var decoded = PngDecoder.Decode(encoded);

        Assert.Multiple(() =>
        {
            Assert.That(encoded.Take(8), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
            Assert.That(decoded.Width, Is.EqualTo(13));
            Assert.That(decoded.Height, Is.EqualTo(7));
            Assert.That(decoded.Pixels, Is.EqualTo(original.Pixels));
        });
    }

    [Test]
    public void Encode_KeepsAlphaAndSinglePixelImages()
    {
        var image = new ImageData(1, 1, [10, 20, 30, 40]);
        var decoded = PngDecoder.Decode(PngEncoder.Encode(image));

        Assert.Multiple(() =>
        {
            Assert.That(decoded.Width, Is.EqualTo(1));
            Assert.That(decoded.GetChannel(0, 0, 0), Is.EqualTo(10));
            Assert.That(decoded.GetChannel(0, 0, 3), Is.EqualTo(40));
        });
    }
}

[TestFixture]
public class MaskedGenerationTests
{
    private sealed class FixedDepthEstimator : IDepthEstimator
    {
        public string EngineName => "test estimator";

        public Task<DepthMap> EstimateAsync(
            ImageData image, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(TestImages.Bump(48, 36));
    }

    /// <summary>Keeps the left half of the photo and drops the right half.</summary>
    private static SelectionMask LeftHalf(int width, int height)
    {
        var keep = new bool[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                keep[y * width + x] = x < width / 2;

        return new SelectionMask(width, height, keep);
    }

    [Test]
    public async Task GenerateAsync_FlattensEverythingOutsideTheObject()
    {
        var source = TestImages.Gradient(40, 20);
        var pipeline = new MeshGenerationPipeline(new FixedDepthEstimator());
        var result = await pipeline.GenerateAsync(
            source,
            new GenerationOptions { ExportDetail = 20, PreviewDetail = 8, Keep = LeftHalf(40, 20) });

        // Export grid is 20x10, so front vertices run y * 20 + x and the right half is x >= 10.
        var mesh = result.Mesh;
        var heights = new List<float>();
        for (var y = 0; y < 10; y++)
            for (var x = 10; x < 20; x++)
                heights.Add(mesh.Vertices[y * 20 + x].Z);

        Assert.That(heights.Distinct().Count(), Is.EqualTo(1), "The background half must be a flat base.");

        var kept = new List<float>();
        for (var y = 0; y < 10; y++)
            for (var x = 0; x < 10; x++)
                kept.Add(mesh.Vertices[y * 20 + x].Z);

        Assert.That(kept.Distinct().Count(), Is.GreaterThan(1), "The kept half should still carry the relief.");
        TestImages.AssertWatertight(mesh);
    }

    [Test]
    public async Task GenerateAsync_WithoutAMaskKeepsTheWholeRelief()
    {
        var source = TestImages.Gradient(40, 20);
        var pipeline = new MeshGenerationPipeline(new FixedDepthEstimator());

        var whole = await pipeline.GenerateAsync(
            source, new GenerationOptions { ExportDetail = 20, PreviewDetail = 8 });
        var masked = await pipeline.GenerateAsync(
            source, new GenerationOptions { ExportDetail = 20, PreviewDetail = 8, Keep = LeftHalf(40, 20) });

        Assert.That(masked.Mesh.SignedVolume(), Is.LessThan(whole.Mesh.SignedVolume()));
    }

    [Test]
    public void GenerateAsync_RejectsAMaskThatDoesNotMatchThePhoto()
    {
        var pipeline = new MeshGenerationPipeline(new FixedDepthEstimator());
        var options = new GenerationOptions { ExportDetail = 16, PreviewDetail = 8, Keep = LeftHalf(10, 10) };

        Assert.ThrowsAsync<ArgumentException>(
            async () => await pipeline.GenerateAsync(TestImages.Gradient(40, 20), options));
    }
}
