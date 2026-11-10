using Pic2Stl.Core.Colours;
using Pic2Stl.Core.Imaging;

namespace Pic2Stl.Tests;

[TestFixture]
public class PaletteBuilderTests
{
    [Test]
    public void Build_FourQuadrantsGivesAllFourColours()
    {
        var result = PaletteBuilder.Build(TestImages.FourQuadrants(), 4);

        Assert.Multiple(() =>
        {
            Assert.That(result.Palette.Count, Is.EqualTo(4));
            Assert.That(result.Map.Indices.Distinct().Count(), Is.EqualTo(4));
        });
    }

    [Test]
    public void Build_FourColourLimitIsRespectedForRichImage()
    {
        var gradient = GradientImage(64, 64);
        var result = PaletteBuilder.Build(gradient, 4);

        Assert.That(result.Palette.Count, Is.LessThanOrEqualTo(4));
    }

    [Test]
    public void Build_SixteenColoursGivesMoreDetailThanFour()
    {
        var gradient = GradientImage(96, 96);
        var four = PaletteBuilder.Build(gradient, 4);
        var sixteen = PaletteBuilder.Build(gradient, 16);

        Assert.Multiple(() =>
        {
            Assert.That(four.Palette.Count, Is.EqualTo(4));
            Assert.That(sixteen.Palette.Count, Is.EqualTo(16));
            Assert.That(sixteen.Palette.Colours.Distinct().Count(), Is.EqualTo(16));
        });
    }

    [Test]
    public void Build_SolidImageCollapsesToOneColour()
    {
        var image = ImageData.Create(12, 9, new Rgb(10, 120, 200));
        var result = PaletteBuilder.Build(image, 4);

        Assert.Multiple(() =>
        {
            Assert.That(result.Palette.Count, Is.EqualTo(1));
            Assert.That(result.Palette.Colours[0], Is.EqualTo(new Rgb(10, 120, 200)));
            Assert.That(result.Map.Indices.All(i => i == 0), Is.True);
        });
    }

    [Test]
    public void Build_IsDeterministic()
    {
        var image = GradientImage(40, 30);
        var first = PaletteBuilder.Build(image, 4);
        var second = PaletteBuilder.Build(image, 4);

        Assert.That(second.Palette.Colours, Is.EqualTo(first.Palette.Colours));
        Assert.That(second.Map.Indices, Is.EqualTo(first.Map.Indices));
    }

    [Test]
    public void Map_PicksTheNearestPaletteEntry()
    {
        var palette = new Palette(new[] { new Rgb(0, 0, 0), new Rgb(255, 255, 255) });
        var image = ImageData.Create(2, 1, new Rgb(240, 240, 240));
        image.Pixels[0] = 10;
        image.Pixels[1] = 10;
        image.Pixels[2] = 10;

        var map = PaletteBuilder.Map(image, palette);

        Assert.That(map.At(0, 0), Is.EqualTo((byte)0));
        Assert.That(map.At(1, 0), Is.EqualTo((byte)1));
    }

    [Test]
    public void Build_TransparentImageYieldsSingleFallbackColour()
    {
        var image = ImageData.Create(4, 4, new Rgb(0, 0, 0), alpha: 0);
        var result = PaletteBuilder.Build(image, 4);

        Assert.That(result.Palette.Count, Is.EqualTo(1));
    }

    [Test]
    public void Build_RejectsColourCountOutsideSupportedRange()
    {
        var image = TestImages.TwoColourSides();

        Assert.Multiple(() =>
        {
            Assert.That(() => PaletteBuilder.Build(image, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => PaletteBuilder.Build(image, 300), Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void Map_ClampsOutOfRangeCoordinates()
    {
        var palette = new Palette(new[] { new Rgb(1, 2, 3) });
        var map = PaletteBuilder.Map(TestImages.TwoColourSides(8, 8), palette);

        Assert.Multiple(() =>
        {
            Assert.That(map.At(-5, -5), Is.EqualTo(0));
            Assert.That(map.At(100, 100), Is.EqualTo(0));
        });
    }

    private static ImageData GradientImage(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                pixels[i] = (byte)(x * 255 / (width - 1));
                pixels[i + 1] = (byte)(y * 255 / (height - 1));
                pixels[i + 2] = (byte)(255 - x * 255 / (width - 1));
                pixels[i + 3] = 255;
            }

        return new ImageData(width, height, pixels);
    }
}

[TestFixture]
public class ColourPalettesTests
{
    [Test]
    public void Presets_AreFourByFourDistinctColours()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ColourPalettes.Presets, Has.Count.EqualTo(16));
            Assert.That(ColourPalettes.Presets.Distinct().Count(), Is.EqualTo(16));
        });
    }

    [Test]
    public void Shades_AreTwoHundredAndFiftySixDistinctColours()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ColourPalettes.Shades, Has.Count.EqualTo(256));
            Assert.That(ColourPalettes.Shades, Has.Count.EqualTo(ColourPalettes.ShadeColumns * ColourPalettes.ShadeRows));
            Assert.That(ColourPalettes.Shades.Distinct().Count(), Is.EqualTo(256));
        });
    }

    [Test]
    public void Shades_RunFromNearlyBlackToPastelTints()
    {
        var darkest = ColourPalettes.Shades.Min(colour => colour.R + colour.G + colour.B);
        var lightest = ColourPalettes.Shades.Max(colour => colour.R + colour.G + colour.B);

        Assert.Multiple(() =>
        {
            Assert.That(darkest, Is.LessThan(120));
            Assert.That(lightest, Is.GreaterThan(660));
        });
    }
}
