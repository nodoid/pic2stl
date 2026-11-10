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
    public void SelectAt_FindsTheBoundsOfTheObjectUnderTheTap()
    {
        var image = SubjectOnBackground();

        // A single tap in the middle of the disc: no box is drawn, the bounds come from the colour match.
        var selection = ObjectSelector.SelectAt(image, 30, 20);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Bounds.X, Is.InRange(17, 19));
            Assert.That(selection.Bounds.Y, Is.InRange(7, 9));
            Assert.That(selection.Bounds.Right, Is.InRange(41, 43));
            Assert.That(selection.Bounds.Bottom, Is.InRange(31, 33));
            Assert.That(selection.Mask.At(selection.Mask.Width / 2, selection.Mask.Height / 2), Is.True);
            Assert.That(selection.Mask.At(0, 0), Is.False, "A corner of the bounding box is background.");
        });
    }

    [Test]
    public void SelectAt_IgnoresSpecksThatAreNotConnectedToTheTap()
    {
        var pixels = new byte[40 * 30 * 4];
        for (var i = 0; i < 40 * 30; i++)
        {
            pixels[i * 4] = Background.R;
            pixels[i * 4 + 1] = Background.G;
            pixels[i * 4 + 2] = Background.B;
            pixels[i * 4 + 3] = 255;
        }

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

        var selection = ObjectSelector.SelectAt(new ImageData(40, 30, pixels), 15, 15);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Bounds, Is.EqualTo(new SelectionRect(10, 10, 10, 10)));
            Assert.That(selection.Mask.KeptCount, Is.EqualTo(100));
        });
    }

    [Test]
    public void SelectAt_OnTheBackgroundKeepsTheWholePictureRatherThanABackgroundShapedCrop()
    {
        var image = SubjectOnBackground();

        // The background wraps all the way round the subject, so its own outline is the whole photo. Filling the
        // subject in as a hole leaves everything kept, which Apply treats as "no mask" instead of cutting the
        // object out by mistake.
        var selection = ObjectSelector.SelectAt(image, 1, 1);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Bounds, Is.EqualTo(new SelectionRect(0, 0, 60, 40)));
            Assert.That(selection.Mask.IsEverything, Is.True);
        });
    }

    [Test]
    public void SelectAt_ClampsATapOutsideThePhotoToItsEdge()
    {
        var image = SubjectOnBackground();

        var selection = ObjectSelector.SelectAt(image, -20, 500);

        Assert.That(selection.Bounds.X, Is.GreaterThanOrEqualTo(0));
        Assert.That(selection.Bounds.Bottom, Is.LessThanOrEqualTo(40));
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
    public void Select_FollowsTheEdgeOfASubjectWithMoreThanOneColour()
    {
        // Half red, half yellow disc on blue. No single colour describes the subject, but its edge against the
        // background does, so the selection still follows the shape rather than falling back to the box.
        const int width = 60, height = 40;
        var pixels = new byte[width * height * 4];
        var other = new Rgb(255, 220, 0);

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var inside = (x - 30) * (x - 30) + (y - 20) * (y - 20) <= 12 * 12;
                var colour = !inside ? Background : x < 30 ? Subject : other;

                pixels[i] = colour.R;
                pixels[i + 1] = colour.G;
                pixels[i + 2] = colour.B;
                pixels[i + 3] = 255;
            }

        var selection = ObjectSelector.Select(new ImageData(width, height, pixels), new SelectionRect(0, 0, width, height));

        Assert.Multiple(() =>
        {
            Assert.That(selection.Bounds.X, Is.InRange(17, 19));
            Assert.That(selection.Bounds.Right, Is.InRange(41, 43));
            Assert.That(selection.Mask.At(2, selection.Mask.Height / 2), Is.True, "The red half is kept.");
            Assert.That(selection.Mask.At(selection.Mask.Width - 3, selection.Mask.Height / 2), Is.True, "So is the yellow half.");
            Assert.That(selection.Mask.At(0, 0), Is.False, "The corner of the box is still background.");
        });
    }

    [Test]
    public void Select_FillsGapsThatAreEnclosedByTheSubject()
    {
        // A red square with a blue patch inside it: the patch is enclosed, so it is part of the object rather
        // than a hole that would be flattened to the base.
        const int width = 40, height = 40;
        var pixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var inSquare = x >= 12 && x <= 27 && y >= 12 && y <= 27;
                var inGap = x >= 14 && x <= 17 && y >= 14 && y <= 17;
                var colour = inSquare && !inGap ? Subject : Background;

                pixels[i] = colour.R;
                pixels[i + 1] = colour.G;
                pixels[i + 2] = colour.B;
                pixels[i + 3] = 255;
            }

        var selection = ObjectSelector.Select(new ImageData(width, height, pixels), new SelectionRect(8, 8, 26, 26));

        Assert.Multiple(() =>
        {
            Assert.That(selection.Bounds, Is.EqualTo(new SelectionRect(12, 12, 16, 16)));
            Assert.That(selection.Mask.KeptCount, Is.EqualTo(16 * 16), "The enclosed gap is filled in.");
            Assert.That(selection.Mask.At(3, 3), Is.True);
            Assert.That(selection.Mask.At(0, 0), Is.True);
        });
    }

    [Test]
    public void Select_ALooserMatchCutsMoreOfTheSurroundingColour()
    {
        // The disc has a halo that is closer in colour to the background than the subject is; how much of the
        // halo survives depends on the colour match.
        const int width = 60, height = 40;
        var pixels = new byte[width * height * 4];
        var halo = new Rgb(50, 50, 50);
        var subject = new Rgb(20, 20, 20);
        var background = new Rgb(60, 60, 60);

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var distance = Math.Sqrt((x - 30) * (x - 30) + (y - 20) * (y - 20));
                var colour = distance <= 12 ? subject : distance <= 18 ? halo : background;

                pixels[i] = colour.R;
                pixels[i + 1] = colour.G;
                pixels[i + 2] = colour.B;
                pixels[i + 3] = 255;
            }

        var image = new ImageData(width, height, pixels);
        var box = new SelectionRect(0, 0, width, height);

        var strict = ObjectSelector.Select(image, box, 0.03f);
        var loose = ObjectSelector.Select(image, box, 0.05f);

        Assert.Multiple(() =>
        {
            Assert.That(strict.Mask.KeptCount, Is.GreaterThan(loose.Mask.KeptCount));
            Assert.That(loose.Bounds.Width, Is.LessThan(strict.Bounds.Width));
            Assert.That(strict.Bounds.Width, Is.GreaterThan(30), "The halo is still part of the object.");
            Assert.That(loose.Bounds.Width, Is.InRange(24, 26), "Only the dark core is left.");
        });
    }

    [Test]
    public void Refine_AddsAPatchBeyondTheFoundObject()
    {
        var image = SubjectOnBackground();
        var selection = ObjectSelector.Select(image, new SelectionRect(10, 5, 40, 30));

        // A stroke out at (45, 20), three pixels across, which is past the disc's right edge at 42.
        var refined = ObjectSelector.Refine(
            selection, image.Width, image.Height, [new SelectionStroke(0.75f, 0.5f, 0.05f, true)]);

        Assert.Multiple(() =>
        {
            Assert.That(refined.Bounds.Right, Is.GreaterThan(selection.Bounds.Right));
            Assert.That(refined.Mask.KeptCount, Is.GreaterThan(selection.Mask.KeptCount));
            Assert.That(refined.Mask.At(refined.Bounds.Width - 1, refined.Bounds.Height / 2), Is.True);
        });
    }

    [Test]
    public void Refine_CutsAPatchOutOfTheObject()
    {
        var image = SubjectOnBackground();
        var selection = ObjectSelector.Select(image, new SelectionRect(10, 5, 40, 30));

        var refined = ObjectSelector.Refine(
            selection, image.Width, image.Height, [new SelectionStroke(0.5f, 0.5f, 0.03f, false)]);

        Assert.That(refined.Mask.KeptCount, Is.LessThan(selection.Mask.KeptCount));
    }

    [Test]
    public void Refine_ClampsStrokesToThePhotoAndIgnoresAnEmptyList()
    {
        var image = SubjectOnBackground();
        var selection = ObjectSelector.Select(image, new SelectionRect(10, 5, 40, 30));

        var wide = ObjectSelector.Refine(
            selection, image.Width, image.Height, [new SelectionStroke(0f, 0f, 0.2f, true)]);

        Assert.Multiple(() =>
        {
            Assert.That(wide.Bounds.X, Is.EqualTo(0));
            Assert.That(wide.Bounds.Y, Is.EqualTo(0));
            Assert.That(ObjectSelector.Refine(selection, image.Width, image.Height, []), Is.SameAs(selection));
        });
    }

    [Test]
    public void Refine_ValidatesTheImageSize()
    {
        var selection = ObjectSelector.Select(SubjectOnBackground(), new SelectionRect(10, 5, 40, 30));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ObjectSelector.Refine(selection, 0, 40, [new SelectionStroke(0.5f, 0.5f, 0.03f, true)]));
    }

    [Test]
    public void Select_ValidatesItsInput()
    {        var image = SubjectOnBackground();

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
    public void Outline_TracesTheEdgeOfTheKeptRegion()
    {
        // A single kept pixel in the middle is surrounded on all four sides.
        var single = new SelectionMask(3, 3, [false, false, false, false, true, false, false, false, false]);
        var edges = single.Outline();

        Assert.Multiple(() =>
        {
            Assert.That(edges, Has.Count.EqualTo(4));
            Assert.That(edges, Does.Contain(((float)1, (float)1, (float)2, (float)1)));
            Assert.That(edges, Does.Contain(((float)1, (float)2, (float)2, (float)2)));
            Assert.That(edges, Does.Contain(((float)1, (float)1, (float)1, (float)2)));
            Assert.That(edges, Does.Contain(((float)2, (float)1, (float)2, (float)2)));
        });
    }

    [Test]
    public void Outline_OfAFullMaskIsJustItsPerimeter()
    {
        var edges = SelectionMask.All(3, 3).Outline();

        // Four merged runs around a 3x3 square, and nothing along the inside seams.
        Assert.Multiple(() =>
        {
            Assert.That(edges, Has.Count.EqualTo(4));
            Assert.That(edges, Does.Contain(((float)0, (float)0, (float)3, (float)0)), "The top edge is one run.");
            Assert.That(edges, Does.Contain(((float)0, (float)3, (float)3, (float)3)));
            Assert.That(edges, Does.Contain(((float)0, (float)0, (float)0, (float)3)));
            Assert.That(edges, Does.Contain(((float)3, (float)0, (float)3, (float)3)));
            Assert.That(edges, Does.Not.Contain(((float)1, (float)1, (float)2, (float)1)), "Inside seams are not.");
        });
    }

    [Test]
    public void Outline_LeavesOutTheEdgeBetweenTwoKeptPixels()
    {
        var edges = new SelectionMask(2, 1, [true, true]).Outline();

        Assert.Multiple(() =>
        {
            Assert.That(edges, Has.Count.EqualTo(4), "A 2x1 strip has a top and bottom run plus one at each end.");
            Assert.That(edges.Count(e => e.Y0 != e.Y1), Is.EqualTo(2), "Only the two ends are vertical.");
        });
    }

    [Test]
    public void Smoothed_FillsNotchesWithoutErodingTheObject()
    {
        const int width = 11, height = 7;
        var keep = new bool[width * height];

        // A 5x5 block from (2,2), with a one-pixel notch in its top edge and a one-pixel detail on its corner.
        for (var y = 2; y <= 6; y++)
            for (var x = 2; x <= 6; x++)
                keep[y * width + x] = true;

        keep[1 * width + 7] = true;
        keep[2 * width + 4] = false;

        var original = new SelectionMask(width, height, keep);
        var smoothed = original.Smoothed();

        var eroded = false;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (original.At(x, y) && !smoothed.At(x, y))
                    eroded = true;

        Assert.Multiple(() =>
        {
            Assert.That(smoothed.At(4, 2), Is.True, "A gap with five neighbours is a notch.");
            Assert.That(smoothed.At(7, 1), Is.True, "Thin detail the user selected must be kept.");
            Assert.That(smoothed.At(0, 0), Is.False);
            Assert.That(eroded, Is.False, "Smoothing must never remove a selected pixel.");
        });
    }

    [Test]
    public void Smoothed_KeepsAOnePixelWideLine()
    {
        const int width = 5, height = 7;
        var keep = new bool[width * height];
        for (var y = 0; y < height; y++)
            keep[y * width + 2] = true;

        var smoothed = new SelectionMask(width, height, keep).Smoothed();

        Assert.That(smoothed.KeptCount, Is.EqualTo(height), "A line has two neighbours per pixel, so it is not a spike.");
    }

    [Test]
    public void Tint_PaintsTheKeptPixelsAndLeavesTheRestTransparent()
    {
        var mask = new SelectionMask(2, 2, [true, false, false, true]);
        var tint = mask.Tint(new Rgb(10, 200, 120), 90);

        Assert.Multiple(() =>
        {
            Assert.That(tint.Width, Is.EqualTo(2));
            Assert.That(tint.Height, Is.EqualTo(2));
            Assert.That(tint.GetColor(0, 0), Is.EqualTo(new Rgb(10, 200, 120)));
            Assert.That(tint.GetChannel(0, 0, 3), Is.EqualTo(90));
            Assert.That(tint.GetChannel(1, 0, 3), Is.EqualTo(0), "Background pixels must not be tinted.");
            Assert.That(tint.GetChannel(1, 1, 3), Is.EqualTo(90));
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
    public async Task GenerateAsync_TrimsTheSolidToTheObjectInsteadOfThePhoto()
    {
        var source = TestImages.Gradient(40, 20);
        var pipeline = new MeshGenerationPipeline(new FixedDepthEstimator());
        var result = await pipeline.GenerateAsync(
            source,
            new GenerationOptions { ExportDetail = 20, PreviewDetail = 8, Keep = LeftHalf(40, 20) });

        // The export grid is 20x10 and only the 10x10 kept half is printed: the model is 80x80 mm, the object's
        // own square bounds, instead of the photo's 80x40 mm plate with a background half on it.
        var (min, max) = result.Mesh.Bounds();

        Assert.Multiple(() =>
        {
            Assert.That(max.X - min.X, Is.EqualTo(80).Within(0.5));
            Assert.That(max.Y - min.Y, Is.EqualTo(80).Within(1));
        });

        TestImages.AssertWatertight(result.Mesh);
        TestImages.AssertOutwardFacing(result.Mesh);
    }

    [Test]
    public async Task GenerateAsync_WithoutAMaskKeepsTheWholePhoto()
    {
        var source = TestImages.Gradient(40, 20);
        var pipeline = new MeshGenerationPipeline(new FixedDepthEstimator());

        var whole = await pipeline.GenerateAsync(
            source, new GenerationOptions { ExportDetail = 20, PreviewDetail = 8 });
        var (min, max) = whole.Mesh.Bounds();

        Assert.Multiple(() =>
        {
            Assert.That(max.X - min.X, Is.EqualTo(80).Within(0.5));
            Assert.That(max.Y - min.Y, Is.EqualTo(40).Within(0.5));
        });

        TestImages.AssertWatertight(whole.Mesh);
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
