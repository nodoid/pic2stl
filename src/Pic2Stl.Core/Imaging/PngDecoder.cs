using System.Buffers.Binary;
using System.IO.Compression;
using Pic2Stl.Core.Imaging;

namespace Pic2Stl.Core.Imaging;

/// <summary>
/// Minimal PNG reader for the non-interlaced images the platform photo decoders produce. 8-bit and 16-bit
/// samples are accepted; 16-bit ones are reduced to 8 bits, which is all the AI input needs.
/// Decoding in shared code keeps the AI input identical on iOS and Android and makes it testable.
/// </summary>
public static class PngDecoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static ImageData Decode(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        return Decode(png.AsSpan());
    }

    public static ImageData Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
            throw new InvalidDataException("Not a PNG file.");

        var offset = Signature.Length;
        int width = 0, height = 0, bitDepth = 0, colourType = -1, interlace = 0;
        using var compressed = new MemoryStream();
        var seenHeader = false;

        while (offset + 8 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset, 4));
            var type = System.Text.Encoding.ASCII.GetString(png.Slice(offset + 4, 4));
            var dataStart = offset + 8;

            if (length < 0 || dataStart + length + 4 > png.Length)
                throw new InvalidDataException($"PNG chunk '{type}' is truncated.");

            var data = png.Slice(dataStart, length);

            switch (type)
            {
                case "IHDR":
                    if (length < 13)
                        throw new InvalidDataException("PNG header is too short.");

                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..8]);
                    bitDepth = data[8];
                    colourType = data[9];
                    if (data[10] != 0 || data[11] != 0)
                        throw new InvalidDataException("Unsupported PNG compression or filter method.");
                    interlace = data[12];
                    seenHeader = true;
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
            throw new InvalidDataException("PNG has no IHDR chunk.");

        if (bitDepth is not (8 or 16))
            throw new InvalidDataException($"Only 8-bit and 16-bit PNGs are supported, got {bitDepth} bit.");

        if (interlace != 0)
            throw new InvalidDataException("Interlaced PNGs are not supported.");

        var channels = colourType switch
        {
            0 => 1, // greyscale
            2 => 3, // RGB
            4 => 2, // greyscale + alpha
            6 => 4, // RGBA
            _ => throw new InvalidDataException($"Unsupported PNG colour type {colourType}."),
        };

        if (width < 1 || height < 1)
            throw new InvalidDataException("PNG has invalid dimensions.");

        // 16-bit samples are two big-endian bytes each; filtering works byte by byte either way.
        var bytesPerSample = bitDepth / 8;
        var bytesPerPixel = channels * bytesPerSample;
        var stride = width * bytesPerPixel;
        var raw = new byte[height * (stride + 1)];

        compressed.Position = 0;
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            var read = 0;
            while (read < raw.Length)
            {
                var chunk = zlib.Read(raw, read, raw.Length - read);
                if (chunk == 0)
                    break;
                read += chunk;
            }

            if (read != raw.Length)
                throw new InvalidDataException("PNG image data is shorter than expected.");
        }

        var pixels = new byte[width * height * 4];
        var previous = new byte[stride];
        var current = new byte[stride];

        for (var y = 0; y < height; y++)
        {
            var rowStart = y * (stride + 1);
            var filter = raw[rowStart];
            Buffer.BlockCopy(raw, rowStart + 1, current, 0, stride);
            Unfilter(filter, current, previous, bytesPerPixel);

            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var s = x * bytesPerPixel;

                // For 16-bit samples the high byte is the 8-bit value, so reading it truncates correctly.
                var red = current[s];
                var green = channels >= 3 ? current[s + bytesPerSample] : red;
                var blue = channels >= 3 ? current[s + 2 * bytesPerSample] : red;
                var alpha = channels switch
                {
                    2 => current[s + bytesPerSample],
                    4 => current[s + 3 * bytesPerSample],
                    _ => (byte)255,
                };

                pixels[i] = red;
                pixels[i + 1] = green;
                pixels[i + 2] = blue;
                pixels[i + 3] = alpha;
            }

            (previous, current) = (current, previous);
        }

        return new ImageData(width, height, pixels);
    }

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
                throw new InvalidDataException($"Unsupported PNG row filter {filter}.");
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
