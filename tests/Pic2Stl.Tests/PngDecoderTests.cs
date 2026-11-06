using Pic2Stl.Core.Colours;
using Pic2Stl.Core.Imaging;

namespace Pic2Stl.Tests;

[TestFixture]
public class PngDecoderTests
{
    private static ImageData Sample()
    {
        var image = new ImageData(3, 2, new byte[3 * 2 * 4]);
        var colours = new[]
        {
            new Rgb(255, 0, 0), new Rgb(0, 255, 0), new Rgb(0, 0, 255),
            new Rgb(10, 20, 30), new Rgb(200, 150, 100), new Rgb(1, 2, 3),
        };

        for (var i = 0; i < colours.Length; i++)
        {
            image.Pixels[i * 4] = colours[i].R;
            image.Pixels[i * 4 + 1] = colours[i].G;
            image.Pixels[i * 4 + 2] = colours[i].B;
            image.Pixels[i * 4 + 3] = 255;
        }

        return image;
    }

    [Test]
    public void Decode_RgbaRoundTripsExactly()
    {
        var source = Sample();
        var decoded = PngDecoder.Decode(TestPng.Encode(source));

        Assert.Multiple(() =>
        {
            Assert.That(decoded.Width, Is.EqualTo(3));
            Assert.That(decoded.Height, Is.EqualTo(2));
            Assert.That(decoded.Pixels, Is.EqualTo(source.Pixels));
        });
    }

    [Test]
    public void Decode_UpFilteredImageRoundTrips()
    {
        var source = Sample();
        var decoded = PngDecoder.Decode(TestPng.Encode(source, filter: 2));

        Assert.That(decoded.Pixels, Is.EqualTo(source.Pixels));
    }

    [Test]
    public void Decode_RgbImageGetsOpaqueAlpha()
    {
        var source = Sample();
        var decoded = PngDecoder.Decode(TestPng.Encode(source, colourType: 2));

        Assert.Multiple(() =>
        {
            Assert.That(decoded.GetColor(0, 0), Is.EqualTo(new Rgb(255, 0, 0)));
            Assert.That(decoded.GetChannel(0, 0, 3), Is.EqualTo(255));
            Assert.That(decoded.GetColor(2, 1), Is.EqualTo(new Rgb(1, 2, 3)));
        });
    }

    [Test]
    public void Decode_RejectsNonPngData()
    {
        Assert.That(() => PngDecoder.Decode("not a png"u8.ToArray()), Throws.TypeOf<InvalidDataException>());
        Assert.That(() => PngDecoder.Decode(Array.Empty<byte>()), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Decode_RejectsTruncatedFile()
    {
        var png = TestPng.Encode(Sample());
        var truncated = png[..(png.Length / 2)];

        Assert.That(() => PngDecoder.Decode(truncated), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Decode_SixteenBitRgbaUsesTheHighByte()
    {
        var source = Sample();
        var decoded = PngDecoder.Decode(TestPng.Encode(source, bitDepth: 16, lowByte: 0x7F));

        Assert.That(decoded.Pixels, Is.EqualTo(source.Pixels));
    }

    [Test]
    public void Decode_SixteenBitUpFilteredImageRoundTrips()
    {
        var source = Sample();
        var decoded = PngDecoder.Decode(TestPng.Encode(source, filter: 2, bitDepth: 16, lowByte: 0xAB));

        Assert.That(decoded.Pixels, Is.EqualTo(source.Pixels));
    }

    [Test]
    public void Decode_SixteenBitRgbImageGetsOpaqueAlpha()
    {
        var source = Sample();
        var decoded = PngDecoder.Decode(TestPng.Encode(source, colourType: 2, bitDepth: 16));

        Assert.Multiple(() =>
        {
            Assert.That(decoded.GetColor(1, 0), Is.EqualTo(new Rgb(0, 255, 0)));
            Assert.That(decoded.GetChannel(1, 0, 3), Is.EqualTo(255));
        });
    }

    [Test]
    public void Decode_RejectsBitDepthThatIsNotValidForItsColourType()
    {
        var png = TestPng.Encode(Sample());
        // IHDR starts after the 8-byte signature and 8 bytes of chunk header; bit depth is byte 8 of the data.
        png[8 + 8 + 8] = 4;

        var error = Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(png));

        Assert.That(error!.Message, Does.Contain("not valid for colour type 6"));
    }

    [Test]
    public void Decode_OneBitGreyscaleExpandsToBlackAndWhite()
    {
        var source = Greys(5, 2, 0, 255, 0, 255, 0, 255, 255, 0, 0, 255);
        var decoded = PngDecoder.Decode(TestPng.Encode(source, colourType: 0, bitDepth: 1));

        Assert.That(decoded.Pixels, Is.EqualTo(source.Pixels));
    }

    [Test]
    public void Decode_TwoBitGreyscaleScalesToFullRange()
    {
        var source = Greys(3, 2, 0, 85, 170, 255, 85, 170);
        var decoded = PngDecoder.Decode(TestPng.Encode(source, colourType: 0, bitDepth: 2));

        Assert.That(decoded.Pixels, Is.EqualTo(source.Pixels));
    }

    [Test]
    public void Decode_FourBitGreyscaleWithLeftFilterRoundTrips()
    {
        var source = Greys(3, 2, 0, 17, 255, 34, 68, 102);
        var decoded = PngDecoder.Decode(TestPng.Encode(source, filter: 1, colourType: 0, bitDepth: 4));

        Assert.That(decoded.Pixels, Is.EqualTo(source.Pixels));
    }

    [Test]
    public void Decode_SixteenBitGreyscaleUsesTheHighByte()
    {
        var source = Greys(4, 1, 0, 128, 255, 127);
        var decoded = PngDecoder.Decode(TestPng.Encode(source, colourType: 0, bitDepth: 16, lowByte: 0x44));

        Assert.That(decoded.Pixels, Is.EqualTo(source.Pixels));
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(4)]
    public void Decode_PaletteImageUsesPaletteColoursAndTransparency(int bitDepth)
    {
        var indices = new byte[] { 0, 1, 0, 1, 0, 1 };
        var palette = new byte[] { 10, 20, 30, 200, 150, 100 };
        var alpha = new byte[] { 255, 64 };

        var decoded = PngDecoder.Decode(TestPng.EncodePalette(3, 2, indices, bitDepth, palette, alpha));

        Assert.Multiple(() =>
        {
            Assert.That(decoded.GetColor(0, 0), Is.EqualTo(new Rgb(10, 20, 30)));
            Assert.That(decoded.GetChannel(0, 0, 3), Is.EqualTo(255));
            Assert.That(decoded.GetColor(1, 0), Is.EqualTo(new Rgb(200, 150, 100)));
            Assert.That(decoded.GetChannel(1, 0, 3), Is.EqualTo(64));
            Assert.That(decoded.GetColor(2, 1), Is.EqualTo(new Rgb(200, 150, 100)));
        });
    }

    [Test]
    public void Decode_PaletteWithoutTransparencyIsOpaque()
    {
        var palette = new byte[] { 10, 20, 30, 200, 150, 100 };
        var decoded = PngDecoder.Decode(TestPng.EncodePalette(2, 1, [1, 0], 4, palette));

        Assert.That(decoded.GetChannel(0, 0, 3), Is.EqualTo(255));
    }

    [Test]
    public void Decode_PaethFilteredPaletteImageRoundTrips()
    {
        var indices = new byte[] { 0, 1, 2, 3, 2, 1, 0, 3 };
        var palette = Enumerable.Range(0, 4).SelectMany(i => new byte[] { (byte)(i * 40), 0, 255 }).ToArray();

        var decoded = PngDecoder.Decode(TestPng.EncodePalette(4, 2, indices, 4, palette, filter: 4));

        Assert.Multiple(() =>
        {
            Assert.That(decoded.GetColor(0, 0), Is.EqualTo(new Rgb(0, 0, 255)));
            Assert.That(decoded.GetColor(3, 1), Is.EqualTo(new Rgb(120, 0, 255)));
            Assert.That(decoded.GetColor(1, 1), Is.EqualTo(new Rgb(40, 0, 255)));
        });
    }

    [Test]
    public void Decode_PaletteWithoutPaletteChunkSaysWhatIsMissing()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            PngDecoder.Decode(TestPng.EncodePalette(2, 1, [0, 1], 4, palette: null)));

        Assert.That(error!.Message, Does.Contain("no palette"));
    }

    [Test]
    public void Decode_InterlacedPngSaysItIsUnsupported()
    {
        var png = TestPng.Encode(Sample());
        // Interlace method is the last byte of the IHDR data.
        png[8 + 8 + 12] = 1;

        var error = Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(png));

        Assert.That(error!.Message, Does.Contain("interlaced"));
    }

    private static ImageData Greys(int width, int height, params byte[] levels)
    {
        var image = new ImageData(width, height, new byte[width * height * 4]);

        for (var i = 0; i < levels.Length; i++)
        {
            image.Pixels[i * 4] = levels[i];
            image.Pixels[i * 4 + 1] = levels[i];
            image.Pixels[i * 4 + 2] = levels[i];
            image.Pixels[i * 4 + 3] = 255;
        }

        return image;
    }
}
