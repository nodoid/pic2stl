using CoreGraphics;
using Foundation;
using UIKit;

namespace Pic2Stl.Services;

public sealed partial class PhotoDecoder
{
    private static async partial Task<byte[]> DecodeToPngAsync(byte[] source, int maxSide, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // UIImage and UIGraphicsImageRenderer are UIKit: they must only be used on the UI thread, and this is
        // called during photo loading, which continues on a worker thread after the stream has been read.
        return await MainThread.InvokeOnMainThreadAsync(() =>
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
            using var upright = renderer.CreateImage(_ => image.Draw(new CGRect(0, 0, width, height)));

            // On wide-colour devices that renderer can produce 16 bits per channel, so redraw into an explicit
            // 8-bit sRGB buffer: the shared decoder (and the AI input) expects 8-bit samples.
            using var colourSpace = CGColorSpace.CreateSrgb();
            using var buffer = NSMutableData.FromLength(width * height * 4);
            using var context = new CGBitmapContext(
                buffer.MutableBytes,
                width,
                height,
                bitsPerComponent: 8,
                bytesPerRow: width * 4,
                colourSpace,
                CGImageAlphaInfo.PremultipliedLast);

            context.InterpolationQuality = CGInterpolationQuality.High;

            // A bitmap context's memory is top row first, so drawing the image straight in keeps it the right
            // way up. Flipping the CTM here (as you must when drawing into a UIKit view's context) would turn
            // every photo upside down.
            context.DrawImage(new CGRect(0, 0, width, height), upright.CGImage);

            using var flattened = context.ToImage()
                                  ?? throw new InvalidDataException("The photo could not be scaled.");
            using var output = UIImage.FromImage(flattened);
            using var png = output.AsPNG()
                            ?? throw new InvalidDataException("The photo could not be encoded.");
            return png.ToArray();
        });
    }
}
