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
    public void Decode_RejectsUnsupportedBitDepth()
    {
        var png = TestPng.Encode(Sample());
        // IHDR starts after the 8-byte signature and 8 bytes of chunk header; bit depth is byte 8 of the data.
        png[8 + 8 + 8] = 4;

        Assert.That(() => PngDecoder.Decode(png), Throws.TypeOf<InvalidDataException>());
    }
}
