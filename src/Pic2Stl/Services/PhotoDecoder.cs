using Pic2Stl.Core.Imaging;

namespace Pic2Stl.Services;

/// <summary>A decoded photo plus the PNG used for its on-screen thumbnail.</summary>
public sealed record LoadedPhoto(ImageData Image, byte[] PreviewPng);

/// <summary>A photo the app could not turn into pixels. The message is written to be shown to the user.</summary>
public sealed class PhotoLoadException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Reads a photo chosen with MediaPicker. Each platform downsizes and re-encodes to PNG first, which applies
/// EXIF orientation (and HEIC decoding) with the native imaging stack; the pixels are then read by the
/// shared, testable PNG decoder. Failures become <see cref="PhotoLoadException"/>s that say what went wrong.
/// </summary>
public sealed partial class PhotoDecoder
{
    public async Task<LoadedPhoto> LoadAsync(Stream stream, int maxSide = 1024, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();

        try
        {
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PhotoLoadException(
                "That photo could not be read from the device. It may have been moved, deleted or still downloading.",
                ex);
        }

        if (buffer.Length == 0)
            throw new PhotoLoadException("That photo is empty (0 bytes).");

        byte[] png;

        try
        {
            png = await DecodeToPngAsync(buffer.ToArray(), maxSide, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            throw new PhotoLoadException(
                "That photo could not be opened. It may be damaged, or in a format this device cannot read — " +
                "try a JPEG or PNG.",
                ex);
        }
        catch (OutOfMemoryException ex)
        {
            throw new PhotoLoadException("That photo is too large for this device to open. Try a smaller one.", ex);
        }

        try
        {
            return new LoadedPhoto(PngDecoder.Decode(png), png);
        }
        catch (InvalidDataException ex)
        {
            throw new PhotoLoadException($"That photo's image data could not be decoded: {ex.Message}", ex);
        }
    }

    /// <summary>Implemented per platform; returns an upright, downsized PNG.</summary>
    private static partial Task<byte[]> DecodeToPngAsync(byte[] source, int maxSide, CancellationToken cancellationToken);
}
