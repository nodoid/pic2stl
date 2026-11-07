using Microsoft.Maui.Graphics.Platform;

namespace Pic2Stl.Controls;

/// <summary>What the drawn box will be used for, which only changes how the overlay looks.</summary>
public enum PhotoEditMode
{
    Crop,
    Object,
}

/// <summary>
/// Sits over the full-size photo and lets the user drag out a rectangle on it. The selection is kept as
/// fractions of the photo, so the same box maps onto any screen size, and everything outside it is dimmed so
/// the chosen area is obvious. In object mode it can also show the outline the colour match found, so the
/// user sees what will actually be kept before they commit.
/// </summary>
public sealed class SelectionOverlay : GraphicsView, IDrawable
{
    private static readonly Color CropColour = Color.FromArgb("#6C8CFF");
    private static readonly Color ObjectColour = Color.FromArgb("#4ADEA9");

    private RectF? _selection;
    private PointF? _dragStart;
    private bool _dragging;

    private Microsoft.Maui.Graphics.IImage? _outline;
    private RectF _outlineBounds;

    public SelectionOverlay()
    {
        Drawable = this;

        StartInteraction += (_, e) =>
        {
            if (e.Touches.Length > 0)
                BeginBox(e.Touches[0]);
        };

        DragInteraction += (_, e) =>
        {
            if (e.Touches.Length > 0)
                GrowBox(e.Touches[0]);
        };

        EndInteraction += (_, _) => EndBox();

        CancelInteraction += (_, _) =>
        {
            _dragStart = null;
            _dragging = false;
        };
    }

    public PhotoEditMode Mode { get; set; } = PhotoEditMode.Crop;

    public int ImageWidth { get; private set; }

    public int ImageHeight { get; private set; }

    /// <summary>The current box as fractions of the photo: x, y, width and height each between 0 and 1.</summary>
    public RectF? Selection => _selection;

    public bool HasSelection => _selection is { Width: > 0.01f, Height: > 0.01f };

    /// <summary>Where the photo itself is drawn inside the overlay, in overlay coordinates.</summary>
    public RectF PictureBounds => PictureRect(ViewBounds());

    public event EventHandler? SelectionChanged;

    public void Start(int imageWidth, int imageHeight)
    {
        ImageWidth = Math.Max(1, imageWidth);
        ImageHeight = Math.Max(1, imageHeight);
        _selection = null;
        _dragStart = null;
        _dragging = false;
        SetOutline(null, default);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _selection = null;
        _dragStart = null;
        _dragging = false;
        SetOutline(null, default);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The outline the colour match found, as a PNG to draw over the photo plus the photo fractions it covers.
    /// Passing null removes it, which is what crop mode and a tap want.
    /// </summary>
    public void SetOutline(byte[]? png, RectF bounds)
    {
        _outline?.Dispose();
        _outline = null;
        _outlineBounds = bounds;

        if (png is null || png.Length == 0)
        {
            Invalidate();
            return;
        }

        try
        {
            _outline = PlatformImage.FromStream(new MemoryStream(png));
        }
        catch (Exception ex)
        {
            // A decorative preview is not worth breaking the editor over; the box alone still works.
            System.Diagnostics.Debug.WriteLine($"Outline preview could not be decoded: {ex.Message}");
        }

        Invalidate();
    }

    /// <summary>Starts a drag at a point in the overlay's own coordinates.</summary>
    public void BeginBox(PointF point)
    {
        _dragStart = ToFraction(point);
        _dragging = false;
    }

    /// <summary>Extends the drag; a few pixels of movement is treated as a tap, not a box.</summary>
    public void GrowBox(PointF point)
    {
        if (_dragStart is not { } start)
            return;

        var current = ToFraction(point);
        var bounds = ViewBounds();
        var picture = PictureRect(bounds);

        if (!_dragging)
        {
            var dx = (current.X - start.X) * picture.Width;
            var dy = (current.Y - start.Y) * picture.Height;
            if (dx * dx + dy * dy < 36)
                return;

            _dragging = true;
        }

        _selection = Normalise(start, current);
        SetOutline(null, default);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Finishes the drag, or clears the box if the gesture was only a tap.</summary>
    public void EndBox()
    {
        if (!_dragging)
        {
            // A tap clears the box so the user can start drawing again.
            Clear();
            _dragStart = null;
            return;
        }

        _dragStart = null;
        _dragging = false;
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var picture = PictureRect(dirtyRect);
        var dim = Color.FromRgba(0f, 0f, 0f, 0.55f);

        if (!HasSelection)
        {
            canvas.FillColor = Color.FromRgba(0f, 0f, 0f, 0.3f);
            canvas.FillRectangle(picture);
            return;
        }

        var box = _selection!.Value;
        var left = picture.Left + box.X * picture.Width;
        var top = picture.Top + box.Y * picture.Height;
        var width = box.Width * picture.Width;
        var height = box.Height * picture.Height;

        // Dim the four bands around the box rather than the whole picture, so the photo shows through inside.
        canvas.FillColor = dim;
        canvas.FillRectangle(picture.Left, picture.Top, picture.Width, top - picture.Top);
        canvas.FillRectangle(picture.Left, top + height, picture.Width, picture.Bottom - (top + height));
        canvas.FillRectangle(picture.Left, top, left - picture.Left, height);
        canvas.FillRectangle(left + width, top, picture.Right - (left + width), height);

        // The found object glows inside the box, so a weak colour match is visible before it is applied.
        if (_outline is not null)
        {
            var outlineLeft = picture.Left + _outlineBounds.X * picture.Width;
            var outlineTop = picture.Top + _outlineBounds.Y * picture.Height;
            canvas.DrawImage(
                _outline,
                outlineLeft,
                outlineTop,
                _outlineBounds.Width * picture.Width,
                _outlineBounds.Height * picture.Height);
        }

        var accent = Mode == PhotoEditMode.Object ? ObjectColour : CropColour;
        canvas.StrokeColor = accent;
        canvas.StrokeSize = 2.5f;
        canvas.Antialias = true;
        canvas.DrawRectangle(left, top, width, height);

        const float handle = 7f;
        canvas.FillColor = accent;
        foreach (var (hx, hy) in new[] { (left, top), (left + width, top), (left, top + height), (left + width, top + height) })
            canvas.FillRoundedRectangle(hx - handle / 2, hy - handle / 2, handle, handle, 2f);
    }

    private RectF ViewBounds() => new(0, 0, (float)Math.Max(Width, 0), (float)Math.Max(Height, 0));

    /// <summary>The photo as drawn by the Image control it sits over: fitted, centred and unstretched.</summary>
    private RectF PictureRect(RectF bounds)
    {
        if (bounds.Width < 1 || bounds.Height < 1 || ImageWidth < 1 || ImageHeight < 1)
            return bounds;

        var scale = MathF.Min(bounds.Width / ImageWidth, bounds.Height / ImageHeight);
        var width = ImageWidth * scale;
        var height = ImageHeight * scale;

        return new RectF(
            bounds.Left + (bounds.Width - width) / 2f,
            bounds.Top + (bounds.Height - height) / 2f,
            width,
            height);
    }

    private PointF ToFraction(PointF point)
    {
        var picture = PictureRect(ViewBounds());
        if (picture.Width < 1 || picture.Height < 1)
            return new PointF(0.5f, 0.5f);

        return new PointF(
            Math.Clamp((point.X - picture.Left) / picture.Width, 0f, 1f),
            Math.Clamp((point.Y - picture.Top) / picture.Height, 0f, 1f));
    }

    private static RectF Normalise(PointF start, PointF end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        return new RectF(left, top, Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
    }
}
