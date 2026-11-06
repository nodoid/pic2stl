using System.IO.Compression;
using System.Text;
using Pic2Stl.Core.Imaging;

namespace Pic2Stl.Tests;

/// <summary>
/// Builds PNGs for the decoder tests. All five row filters are supported so filtered rows are exercised, and
/// packed (1, 2 and 4-bit) samples are written most significant bits first, as the format requires.
/// </summary>
internal static class TestPng
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <param name="lowByte">Value stored in the low byte of every 16-bit sample; it must be ignored.</param>
    public static byte[] Encode(ImageData image, int filter = 0, int colourType = 6, int bitDepth = 8, byte lowByte = 0)
    {
        var channels = colourType switch
        {
            0 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(colourType)),
        };

        if (bitDepth is not (1 or 2 or 4 or 8 or 16))
            throw new ArgumentOutOfRangeException(nameof(bitDepth));

        var bitsPerPixel = channels * bitDepth;
        var stride = (image.Width * bitsPerPixel + 7) / 8;
        var samples = new byte[image.Height * stride];

        byte Source(int x, int y, int channel)
        {
            var offset = (y * image.Width + x) * 4;
            return channel switch
            {
                0 => image.Pixels[offset],
                1 => channels <= 2 ? image.Pixels[offset + 3] : image.Pixels[offset + 1],
                2 => image.Pixels[offset + 2],
                _ => image.Pixels[offset + 3],
            };
        }

        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
                for (var c = 0; c < channels; c++)
                {
                    var value = Source(x, y, c);

                    // Packed depths hold a reduced range, so quantise the 8-bit colour to the samples' steps.
                    if (bitDepth < 8)
                        value = (byte)(value * ((1 << bitDepth) - 1) / 255);

                    WriteSample(samples, y * stride, x * channels + c, bitDepth, value, lowByte);
                }

        var raw = FilterRows(samples, image.Height, stride, filter, Math.Max(1, bitsPerPixel / 8));
        return Assemble(image.Width, image.Height, bitDepth, colourType, raw, null, null);
    }

    /// <summary>Builds a palette (colour type 3) PNG from one index per pixel.</summary>
    public static byte[] EncodePalette(
        int width,
        int height,
        byte[] indices,
        int bitDepth,
        byte[]? palette,
        byte[]? alpha = null,
        int filter = 0)
    {
        if (indices.Length != width * height)
            throw new ArgumentException("One index per pixel is required.", nameof(indices));

        var stride = (width * bitDepth + 7) / 8;
        var samples = new byte[height * stride];

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                WriteSample(samples, y * stride, x, bitDepth, indices[y * width + x], 0);

        var raw = FilterRows(samples, height, stride, filter, 1);
        return Assemble(width, height, bitDepth, 3, raw, palette, alpha);
    }

    private static void WriteSample(byte[] row, int rowStart, int sampleIndex, int bitDepth, byte value, byte lowByte)
    {
        switch (bitDepth)
        {
            case 8:
                row[rowStart + sampleIndex] = value;
                return;

            case 16:
                row[rowStart + sampleIndex * 2] = value;
                row[rowStart + sampleIndex * 2 + 1] = lowByte;
                return;

            default:
                var bitOffset = sampleIndex * bitDepth;
                var shift = 8 - bitDepth - (bitOffset & 7);
                row[rowStart + (bitOffset >> 3)] |= (byte)((value & ((1 << bitDepth) - 1)) << shift);
                return;
        }
    }

    private static byte[] FilterRows(byte[] samples, int height, int stride, int filter, int bytesPerPixel)
    {
        var raw = new byte[height * (stride + 1)];

        for (var y = 0; y < height; y++)
        {
            var rowStart = y * stride;
            var outputStart = y * (stride + 1);
            raw[outputStart] = (byte)filter;

            for (var i = 0; i < stride; i++)
            {
                var value = samples[rowStart + i];
                var left = i >= bytesPerPixel ? samples[rowStart + i - bytesPerPixel] : 0;
                var up = y > 0 ? samples[rowStart - stride + i] : 0;
                var upLeft = y > 0 && i >= bytesPerPixel ? samples[rowStart - stride + i - bytesPerPixel] : 0;

                var stored = filter switch
                {
                    0 => value,
                    1 => (byte)(value - left),
                    2 => (byte)(value - up),
                    3 => (byte)(value - ((left + up) >> 1)),
                    4 => (byte)(value - Paeth(left, up, upLeft)),
                    _ => throw new ArgumentOutOfRangeException(nameof(filter)),
                };

                raw[outputStart + 1 + i] = stored;
            }
        }

        return raw;
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

    private static byte[] Assemble(
        int width,
        int height,
        int bitDepth,
        int colourType,
        byte[] raw,
        byte[]? palette,
        byte[]? paletteAlpha)
    {
        using var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = (byte)bitDepth;
        header[9] = (byte)colourType;
        WriteChunk(output, "IHDR", header);

        if (palette is not null)
            WriteChunk(output, "PLTE", palette);

        if (paletteAlpha is not null)
            WriteChunk(output, "tRNS", paletteAlpha);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(raw);
            WriteChunk(output, "IDAT", compressed.ToArray());
        }

        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, Crc32(typeBytes, data));
        stream.Write(crcBytes);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
            crc = Step(crc, b);
        foreach (var b in data)
            crc = Step(crc, b);
        return crc ^ 0xFFFFFFFFu;

        static uint Step(uint crc, byte value)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            return crc;
        }
    }
}
