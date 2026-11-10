using Android.Graphics;
using Android.Media;

namespace Pic2Stl.Services;

public sealed partial class PhotoDecoder
{
    private static partial Task<byte[]> DecodeToPngAsync(byte[] source, int maxSide, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var decoded = BitmapFactory.DecodeByteArray(source, 0, source.Length)
                                ?? throw new InvalidDataException("The photo could not be decoded.");

            var rotation = 0;
            var mirror = false;
            try
            {
                using var exif = new ExifInterface(new MemoryStream(source));
                var orientation = (Orientation)exif.GetAttributeInt(
                    ExifInterface.TagOrientation,
                    (int)Orientation.Normal);

                // EXIF orientation is a rotation plus, for the mirrored variants, a flip applied after it.
                (rotation, mirror) = orientation switch
                {
                    Orientation.Rotate90 => (90, false),
                    Orientation.Rotate180 => (180, false),
                    Orientation.Rotate270 => (270, false),
                    Orientation.FlipHorizontal => (0, true),
                    Orientation.FlipVertical => (180, true),
                    Orientation.Transpose => (90, true),
                    Orientation.Transverse => (270, true),
                    _ => (0, false),
                };
            }
            catch (Exception)
            {
                // No EXIF block: treat the bitmap as upright.
            }

            Bitmap upright = decoded;
            Bitmap? transformed = null;
            if (rotation != 0 || mirror)
            {
                using var matrix = new Matrix();
                if (rotation != 0)
                    matrix.PostRotate(rotation);
                if (mirror)
                    matrix.PostScale(-1, 1);

                transformed = Bitmap.CreateBitmap(decoded, 0, 0, decoded.Width, decoded.Height, matrix, true);
                upright = transformed ?? decoded;
            }

            try
            {
                var scale = Math.Min(1.0, maxSide / (double)Math.Max(upright.Width, upright.Height));
                Bitmap sized = upright;
                Bitmap? scaled = null;

                if (scale < 1.0)
                {
                    var width = Math.Max(1, (int)Math.Round(upright.Width * scale));
                    var height = Math.Max(1, (int)Math.Round(upright.Height * scale));
                    scaled = Bitmap.CreateScaledBitmap(upright, width, height, filter: true);
                    sized = scaled ?? upright;
                }

                try
                {
                    using var output = new MemoryStream();
                    if (!sized.Compress(Bitmap.CompressFormat.Png!, 100, output))
                        throw new InvalidDataException("The photo could not be encoded.");

                    return output.ToArray();
                }
                finally
                {
                    scaled?.Dispose();
                }
            }
            finally
            {
                transformed?.Dispose();
            }
        }, cancellationToken);
    }
}
