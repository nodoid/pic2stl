namespace Pic2Stl.Core.Imaging;

/// <summary>A rectangular region of a bitmap in pixel coordinates, inclusive of both corners.</summary>
public readonly record struct SelectionRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width - 1;

    public int Bottom => Y + Height - 1;

    public bool IsEmpty => Width < 1 || Height < 1;

    /// <summary>
    /// Builds a rectangle from any two corners of a drag, so dragging up and to the left works the same as
    /// down and to the right. The result is always inside the image.
    /// </summary>
    public static SelectionRect FromCorners(int x0, int y0, int x1, int y1, int imageWidth, int imageHeight)
    {
        if (imageWidth < 1 || imageHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Image must be at least 1x1.");

        var left = Math.Clamp(Math.Min(x0, x1), 0, imageWidth - 1);
        var right = Math.Clamp(Math.Max(x0, x1), 0, imageWidth - 1);
        var top = Math.Clamp(Math.Min(y0, y1), 0, imageHeight - 1);
        var bottom = Math.Clamp(Math.Max(y0, y1), 0, imageHeight - 1);

        return new SelectionRect(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>
    /// Turns a selection expressed as 0..1 fractions of the image (what the on-screen overlay works in) into
    /// pixels. Values outside 0..1 are clamped rather than rejected, because a drag can end off the photo.
    /// </summary>
    public static SelectionRect FromFractions(
        double x0, double y0, double x1, double y1, int imageWidth, int imageHeight)
    {
        if (imageWidth < 1 || imageHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Image must be at least 1x1.");

        static int ToPixel(double fraction, int size) =>
            Math.Clamp((int)Math.Round(fraction * (size - 1)), 0, size - 1);

        return FromCorners(
            ToPixel(x0, imageWidth),
            ToPixel(y0, imageHeight),
            ToPixel(x1, imageWidth),
            ToPixel(y1, imageHeight),
            imageWidth,
            imageHeight);
    }

    public SelectionRect Clamped(int imageWidth, int imageHeight) =>
        FromCorners(X, Y, Right, Bottom, imageWidth, imageHeight);

    public override string ToString() => $"{Width}x{Height} at ({X},{Y})";
}
