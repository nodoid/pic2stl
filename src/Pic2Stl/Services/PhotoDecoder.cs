using Pic2Stl.Core.Imaging;

namespace Pic2Stl.Services;

/// <summary>A decoded photo plus the PNG used for its on-screen thumbnail.</summary>
public sealed record LoadedPhoto(ImageData Image, byte[] PreviewPng);

/// <summary>
/// Reads a photo chosen with MediaPicker. Each platform downsizes and re-encodes to PNG first, which applies
/// EXIF orientation (and HEIC decoding) with the native imaging stack; the pixels are then read by the
/// shared, testable PNG decoder.
/// </summary>
public sealed partial class PhotoDecoder
{
    public async Task<LoadedPhoto> LoadAsync(Stream stream, int maxSide = 1024, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        if (buffer.Length == 0)
            throw new InvalidDataException("The selected photo is empty.");

        var png = await DecodeToPngAsync(buffer.ToArray(), maxSide, cancellationToken).ConfigureAwait(false);
        return new LoadedPhoto(PngDecoder.Decode(png), png);
    }

    /// <summary>Implemented per platform; returns an upright, downsized PNG.</summary>
    private static partial Task<byte[]> DecodeToPngAsync(byte[] source, int maxSide, CancellationToken cancellationToken);
}
