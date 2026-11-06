using CoreGraphics;
using Foundation;
using UIKit;

namespace Pic2Stl.Services;

public sealed partial class PhotoDecoder
{
    private static partial Task<byte[]> DecodeToPngAsync(byte[] source, int maxSide, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var data = NSData.FromArray(source);
            using var image = UIImage.LoadFromData(data)
                              ?? throw new InvalidDataException("The photo could not be decoded.");

            var size = image.Size;
            var scale = Math.Min(1.0, maxSide / Math.Max(size.Width, size.Height));
            var width = Math.Max(1, (int)Math.Round(size.Width * scale));
            var height = Math.Max(1, (int)Math.Round(size.Height * scale));

            // Rendering bakes in EXIF orientation, so the pixels come out upright.
            using var format = new UIGraphicsImageRendererFormat { Opaque = false, Scale = 1 };
            using var renderer = new UIGraphicsImageRenderer(new CGSize(width, height), format);
            using var resized = renderer.CreateImage(_ => image.Draw(new CGRect(0, 0, width, height)));
            using var png = resized.AsPNG()
                            ?? throw new InvalidDataException("The photo could not be encoded.");
            return png.ToArray();
        }, cancellationToken);
    }
}
