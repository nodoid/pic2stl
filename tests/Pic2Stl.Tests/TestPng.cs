using System.IO.Compression;
using System.Text;
using Pic2Stl.Core.Imaging;

namespace Pic2Stl.Tests;

/// <summary>Builds PNGs for the decoder tests. Filter 0 (none) and 2 (up) cover the cases the platforms emit.</summary>
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

        if (bitDepth is not (8 or 16))
            throw new ArgumentOutOfRangeException(nameof(bitDepth));

        var bytesPerSample = bitDepth / 8;
        var bytesPerPixel = channels * bytesPerSample;
        var stride = image.Width * bytesPerPixel;
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
                    var offset = y * stride + (x * channels + c) * bytesPerSample;
                    samples[offset] = Source(x, y, c);

                    if (bytesPerSample == 2)
                        samples[offset + 1] = lowByte;
                }

        var raw = new byte[image.Height * (stride + 1)];

        for (var y = 0; y < image.Height; y++)
        {
            var rowStart = y * (stride + 1);
            raw[rowStart] = (byte)filter;

            for (var i = 0; i < stride; i++)
            {
                var value = samples[y * stride + i];
                var stored = filter == 2 && y > 0
                    ? (byte)(value - samples[(y - 1) * stride + i])
                    : value;

                raw[rowStart + 1 + i] = stored;
            }
        }

        using var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)image.Width);
        WriteBigEndian(header, 4, (uint)image.Height);
        header[8] = (byte)bitDepth;
        header[9] = (byte)colourType;
        WriteChunk(output, "IHDR", header);

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
