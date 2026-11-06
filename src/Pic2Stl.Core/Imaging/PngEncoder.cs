using System.IO.Compression;

namespace Pic2Stl.Core.Imaging;

/// <summary>
/// Writes an RGBA8 PNG. Only used for the on-screen preview when the photo has been cropped or had an object
/// cut out, so it is deliberately simple: one filter type (none), one image data chunk, no interlacing.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] Encode(ImageData image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var stride = image.Width * 4;
        var raw = new byte[(stride + 1) * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            // Every row starts with its filter type; 0 means "store the bytes as they are".
            raw[y * (stride + 1)] = 0;
            Buffer.BlockCopy(image.Pixels, y * stride, raw, y * (stride + 1) + 1, stride);
        }

        var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);

        var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        WriteBigEndian(header, 0, image.Width);
        WriteBigEndian(header, 4, image.Height);
        header[8] = 8;  // bits per channel
        header[9] = 6;  // colour type: truecolour with alpha
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering
        header[12] = 0; // no interlacing

        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);

        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);

        var payload = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++)
            payload[i] = (byte)type[i];

        Buffer.BlockCopy(data, 0, payload, 4, data.Length);
        stream.Write(payload);

        var crc = new byte[4];
        WriteBigEndian(crc, 0, (int)Crc32(payload));
        stream.Write(crc);
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    /// <summary>The PNG CRC, shared by every chunk type.</summary>
    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
