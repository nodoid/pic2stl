using System.Buffers.Binary;
using System.IO.Compression;

namespace Pic2Stl.Core.Imaging;

/// <summary>
/// Minimal PNG reader for the images the platform photo decoders produce, plus anything else the format allows.
/// Every standard colour type and bit depth is accepted: 1, 2 and 4-bit greyscale and palette samples are
/// expanded to 8 bits, 16-bit samples are reduced to 8 bits, palette images are looked up through their PLTE
/// (and optional tRNS) chunk, and Adam7 interlacing is reassembled into a plain raster.
/// Anything it cannot read throws <see cref="InvalidDataException"/> saying what is wrong.
/// Decoding in shared code keeps the AI input identical on iOS and Android and makes it testable.
/// </summary>
public static class PngDecoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    private const int ColourTypeGreyscale = 0;
    private const int ColourTypeRgb = 2;
    private const int ColourTypePalette = 3;
    private const int ColourTypeGreyscaleAlpha = 4;
    private const int ColourTypeRgba = 6;

    public static ImageData Decode(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        return Decode(png.AsSpan());
    }

    public static ImageData Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
            throw new InvalidDataException("This file is not a PNG image.");

        var offset = Signature.Length;
        int width = 0, height = 0, bitDepth = 0, colourType = -1, interlace = 0;
        byte[]? palette = null;
        byte[]? paletteAlpha = null;
        using var compressed = new MemoryStream();
        var seenHeader = false;

        while (offset + 8 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset, 4));
            var type = System.Text.Encoding.ASCII.GetString(png.Slice(offset + 4, 4));
            var dataStart = offset + 8;

            if (length < 0 || dataStart + length + 4 > png.Length)
                throw new InvalidDataException($"The PNG '{type}' chunk is truncated.");

            var data = png.Slice(dataStart, length);

            switch (type)
            {
                case "IHDR":
                    if (length < 13)
                        throw new InvalidDataException("The PNG header is too short.");

                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..8]);
                    bitDepth = data[8];
                    colourType = data[9];
                    if (data[10] != 0 || data[11] != 0)
                        throw new InvalidDataException("The PNG uses an unsupported compression or filter method.");
                    interlace = data[12];
                    seenHeader = true;
                    break;

                case "PLTE":
                    palette = data.ToArray();
                    break;

                case "tRNS":
                    paletteAlpha = data.ToArray();
                    break;

                case "IDAT":
                    compressed.Write(data);
                    break;

                case "IEND":
                    offset = png.Length;
                    continue;
            }

            offset = dataStart + length + 4;
        }

        if (!seenHeader)
            throw new InvalidDataException("The PNG has no header (IHDR chunk).");

        if (width < 1 || height < 1)
            throw new InvalidDataException($"The PNG reports an invalid size of {width}x{height} pixels.");

        var channels = colourType switch
        {
            ColourTypeGreyscale => 1,
            ColourTypeRgb => 3,
            ColourTypePalette => 1, // a single palette index per pixel
            ColourTypeGreyscaleAlpha => 2,
            ColourTypeRgba => 4,
            _ => 0,
        };

        if (channels == 0)
            throw new InvalidDataException($"The PNG uses colour type {colourType}, which is not a defined PNG colour type.");

        // The spec allows only certain depths per colour type.
        int[] allowedDepths = colourType switch
        {
            ColourTypeGreyscale => [1, 2, 4, 8, 16],
            ColourTypePalette => [1, 2, 4, 8],
            _ => [8, 16],
        };

        if (Array.IndexOf(allowedDepths, bitDepth) < 0)
            throw new InvalidDataException(
                $"The PNG stores {bitDepth}-bit samples, which is not valid for colour type {colourType} " +
                $"(allowed: {string.Join(", ", allowedDepths)}-bit).");

        if (colourType == ColourTypePalette)
        {
            if (palette is null)
                throw new InvalidDataException("The PNG is palette-based but has no palette (PLTE chunk).");

            if (palette.Length == 0 || palette.Length % 3 != 0)
                throw new InvalidDataException("The PNG palette (PLTE chunk) is malformed.");
        }

        // Samples sit at 1, 2, 4, 8 or 16 bits each; a row is whole bytes, padded at the right-hand end.
        var bitsPerPixel = channels * bitDepth;

        // Filters work on the bytes of a row and step back by whole pixels, which rounded up is never zero.
        var filterBytesPerPixel = Math.Max(1, bitsPerPixel / 8);

        // An interlaced image is stored as seven sub-images, each coded like a whole image of its own.
        var passes = BuildPasses(interlace, width, height, bitsPerPixel);
        var raw = new byte[passes.Sum(pass => pass.Height * (pass.Stride + 1))];

        if (compressed.Length == 0)
            throw new InvalidDataException("The PNG has no image data (IDAT chunk).");

        compressed.Position = 0;
        try
        {
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            var read = 0;
            while (read < raw.Length)
            {
                var chunk = zlib.Read(raw, read, raw.Length - read);
                if (chunk == 0)
                    break;
                read += chunk;
            }

            if (read != raw.Length)
                throw new InvalidDataException("The PNG image data is shorter than its header says.");
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"The PNG image data is corrupt: {ex.Message}", ex);
        }

        var pixels = new byte[width * height * 4];
        var paletteEntries = palette is null ? 0 : palette.Length / 3;

        // Writes one sample of an unfiltered row into the output raster, expanding it to 8-bit RGBA.
        void Store(byte[] row, int column, int x, int y)
        {
            var first = column * channels;

            byte red, green, blue, alpha;

            if (colourType == ColourTypePalette)
            {
                var index = ReadSample(row, first, bitDepth);

                if (index < paletteEntries)
                {
                    red = palette![index * 3];
                    green = palette[index * 3 + 1];
                    blue = palette[index * 3 + 2];
                }
                else
                {
                    // An out-of-range index is invalid; draw it black rather than failing the whole photo.
                    red = green = blue = 0;
                }

                alpha = paletteAlpha is not null && index < paletteAlpha.Length ? paletteAlpha[index] : (byte)255;
            }
            else
            {
                red = ToByte(ReadSample(row, first, bitDepth), bitDepth);
                green = channels >= 3 ? ToByte(ReadSample(row, first + 1, bitDepth), bitDepth) : red;
                blue = channels >= 3 ? ToByte(ReadSample(row, first + 2, bitDepth), bitDepth) : red;
                alpha = channels switch
                {
                    2 => ToByte(ReadSample(row, first + 1, bitDepth), bitDepth),
                    4 => ToByte(ReadSample(row, first + 3, bitDepth), bitDepth),
                    _ => (byte)255,
                };
            }

            var i = (y * width + x) * 4;
            pixels[i] = red;
            pixels[i + 1] = green;
            pixels[i + 2] = blue;
            pixels[i + 3] = alpha;
        }

        var cursor = 0;
        foreach (var pass in passes)
        {
            var previous = new byte[pass.Stride];
            var current = new byte[pass.Stride];

            for (var row = 0; row < pass.Height; row++)
            {
                Buffer.BlockCopy(raw, cursor + 1, current, 0, pass.Stride);
                Unfilter(raw[cursor], current, previous, filterBytesPerPixel);

                for (var column = 0; column < pass.Width; column++)
                    Store(current, column, pass.StartX + column * pass.StepX, pass.StartY + row * pass.StepY);

                (previous, current) = (current, previous);
                cursor += pass.Stride + 1;
            }
        }

        return new ImageData(width, height, pixels);
    }

    /// <summary>The seven Adam7 passes, as their first pixel and their step across and down the image.</summary>
    private static readonly (int StartX, int StartY, int StepX, int StepY)[] Adam7 =
    [
        (0, 0, 8, 8),
        (4, 0, 8, 8),
        (0, 4, 4, 8),
        (2, 0, 4, 4),
        (0, 2, 2, 4),
        (1, 0, 2, 2),
        (0, 1, 1, 2),
    ];

    /// <summary>Describes the stored sub-images: one for a plain image, up to seven for an interlaced one.</summary>
    private static List<Pass> BuildPasses(int interlace, int width, int height, int bitsPerPixel)
    {
        if (interlace is not (0 or 1))
            throw new InvalidDataException($"The PNG uses interlace method {interlace}, which is not a defined method.");

        if (interlace == 0)
            return [new Pass(0, 0, 1, 1, width, height, (width * bitsPerPixel + 7) / 8)];

        List<Pass> passes = [];

        foreach (var (startX, startY, stepX, stepY) in Adam7)
        {
            // A pass holds the pixels at its offset, striding outwards. On small images some passes are empty.
            var passWidth = (width - startX + stepX - 1) / stepX;
            var passHeight = (height - startY + stepY - 1) / stepY;

            if (passWidth > 0 && passHeight > 0)
                passes.Add(new Pass(startX, startY, stepX, stepY, passWidth, passHeight, (passWidth * bitsPerPixel + 7) / 8));
        }

        return passes;
    }

    private sealed record Pass(int StartX, int StartY, int StepX, int StepY, int Width, int Height, int Stride);

    /// <summary>Reads one sample of a row, most significant bits first. 16-bit samples give their high byte.</summary>
    private static int ReadSample(byte[] row, int sampleIndex, int bitDepth)
    {
        switch (bitDepth)
        {
            case 8:
                return row[sampleIndex];

            case 16:
                return row[sampleIndex * 2];

            default:
                var bitOffset = sampleIndex * bitDepth;
                var mask = (1 << bitDepth) - 1;
                return (row[bitOffset >> 3] >> (8 - bitDepth - (bitOffset & 7))) & mask;
        }
    }

    /// <summary>Scales a sample to 0-255. Sub-byte depths spread their range over the full byte.</summary>
    private static byte ToByte(int sample, int bitDepth) =>
        bitDepth >= 8 ? (byte)sample : (byte)(sample * 255 / ((1 << bitDepth) - 1));

    private static void Unfilter(byte filter, byte[] current, byte[] previous, int bytesPerPixel)
    {
        var length = current.Length;
        switch (filter)
        {
            case 0:
                return;

            case 1:
                for (var i = bytesPerPixel; i < length; i++)
                    current[i] = (byte)(current[i] + current[i - bytesPerPixel]);
                return;

            case 2:
                for (var i = 0; i < length; i++)
                    current[i] = (byte)(current[i] + previous[i]);
                return;

            case 3:
                for (var i = 0; i < length; i++)
                {
                    var left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                    current[i] = (byte)(current[i] + ((left + previous[i]) >> 1));
                }

                return;

            case 4:
                for (var i = 0; i < length; i++)
                {
                    var left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                    var upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                    current[i] = (byte)(current[i] + Paeth(left, previous[i], upLeft));
                }

                return;

            default:
                throw new InvalidDataException($"The PNG uses unknown row filter {filter}.");
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);

        if (pa <= pb && pa <= pc)
            return a;
        return pb <= pc ? b : c;
    }
}
