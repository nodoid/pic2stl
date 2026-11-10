using Microsoft.Maui.Graphics.Platform;
using Pic2Stl.Core.Imaging;

namespace Pic2Stl.Controls;

/// <summary>What the drawn box will be used for, which only changes how the overlay looks.</summary>
public enum PhotoEditMode
{
    Crop,
    Object,
}

/// <summary>
/// Sits over the full-size photo and lets the user drag out a rectangle on it as a starting point. The
/// selection is kept as fractions of the photo, so the same box maps onto any screen size, and everything
/// outside it is dimmed so the chosen area is obvious. In object mode the drawn box gives way to the
/// object's own outline as soon as the background match finds one, so the user sees the shape that will be
/// kept rather than the rectangle it was found in.
/// </summary>
public sealed class SelectionOverlay : GraphicsView, IDrawable
{
    private static readonly Color CropColour = Color.FromArgb("#6C8CFF");
    private static readonly Color ObjectColour = Color.FromArgb("#4ADEA9");

    private RectF? _selection;
    private PointF? _dragStart;
    private bool _dragging;

    private Microsoft.Maui.Graphics.IImage? _tint;
    private RectF _tintBounds;
    private float[]? _edges;

    private SelectionMask? _mask;
    private bool _refining;
    private bool _refineKeep;
    private PointF? _refineLast;

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
            _refining = false;
            _refineLast = null;
        };
    }

    public PhotoEditMode Mode { get; set; } = PhotoEditMode.Crop;

    public int ImageWidth { get; private set; }

    public int ImageHeight { get; private set; }

    /// <summary>The current box as fractions of the photo: x, y, width and height each between 0 and 1.</summary>
    public RectF? Selection => _selection;

    /// <summary>
    /// True when there is something to apply: a drawn box, or a found outline. An object selected with a single
    /// tap can be smaller than one percent of the photo across, so its outline also counts as a selection.
    /// </summary>
    public bool HasSelection =>
        _selection is { Width: > 0.01f, Height: > 0.01f } || _edges is { Length: > 0 };

    /// <summary>Where the photo itself is drawn inside the overlay, in overlay coordinates.</summary>
    public RectF PictureBounds => PictureRect(ViewBounds());

    public event EventHandler? SelectionChanged;

    /// <summary>Raised while an outline is being dragged, with the brush stroke the edit means.</summary>
    public event EventHandler<SelectionStroke>? StrokeAdded;

    /// <summary>Raised when an outline drag finishes, so the preview can be drawn in full again.</summary>
    public event EventHandler? RefineEnded;

    /// <summary>
    /// Raised when the user taps without dragging, with the point as a fraction of the photo, so the app can
    /// work out the bounds of whatever was tapped instead of asking for a box to be drawn first.
    /// </summary>
    public event EventHandler<PointF>? Tapped;

    public void Start(int imageWidth, int imageHeight)
    {
        ImageWidth = Math.Max(1, imageWidth);
        ImageHeight = Math.Max(1, imageHeight);
        _selection = null;
        _dragStart = null;
        _dragging = false;
        SetOutline(null, default, null);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _selection = null;
        _dragStart = null;
        _dragging = false;
        _refining = false;
        _refineLast = null;
        SetOutline(null, default, null);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Records the selection the app worked out from a tap, so the overlay knows there is something to apply
    /// and the page can enable its button. Unlike a drag this raises no events, because the page is the one
    /// supplying the box and a change notification would send it straight back to recompute.
    /// </summary>
    public void SetBox(RectF box)
    {
        _selection = box;
        _dragStart = null;
        _dragging = false;
        Invalidate();
    }

    /// <summary>
    /// The object the colour match found: a tint to show the kept pixels, the photo fractions it covers, and
    /// its outline as line segments in photo fractions (x0, y0, x1, y1, ...). Passing nulls removes them,
    /// which is what crop mode and a tap want.
    /// </summary>
    public void SetOutline(byte[]? tintPng, RectF tintBounds, float[]? edges, SelectionMask? mask = null)
    {
        _tint?.Dispose();
        _tint = null;
        _tintBounds = tintBounds;
        _edges = edges;
        _mask = mask;

        if (tintPng is null || tintPng.Length == 0)
        {
            Invalidate();
            return;
        }

        try
        {
            _tint = PlatformImage.FromStream(new MemoryStream(tintPng));
        }
        catch (Exception ex)
        {
            // A decorative preview is not worth breaking the editor over; the outline still works.
            System.Diagnostics.Debug.WriteLine($"Outline preview could not be decoded: {ex.Message}");
        }

        Invalidate();
    }

    /// <summary>
    /// Starts a drag at a point in the overlay's own coordinates. In object mode, grabbing the outline of the
    /// object that was found refines that outline instead of starting a new box.
    /// </summary>
    public void BeginBox(PointF point)
    {
        if (Mode == PhotoEditMode.Object && TryBeginRefine(point))
            return;

        _refining = false;
        _dragStart = ToFraction(point);
        _dragging = false;
    }

    /// <summary>Extends the drag; a few pixels of movement is treated as a tap, not a box.</summary>
    public void GrowBox(PointF point)
    {
        if (_refining)
        {
            var pulled = ToFraction(point);
            if (_refineLast is { } last)
                EmitSegment(last, pulled);
            _refineLast = pulled;
            return;
        }

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
        SetOutline(null, default, null);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Finishes the drag, or clears the box if the gesture was only a tap.</summary>
    public void EndBox()
    {
        if (_refining)
        {
            _refining = false;
            _refineLast = null;
            RefineEnded?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!_dragging)
        {
            var start = _dragStart;
            _dragStart = null;

            // A tap in object mode means "this thing here", so the page works out the bounds instead of the user
            // drawing them. In crop mode there is nothing to infer, so a tap clears the box for a fresh drag.
            if (Mode == PhotoEditMode.Object && start is { } point)
                Tapped?.Invoke(this, point);
            else
                Clear();

            return;
        }

        _dragStart = null;
        _dragging = false;
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var picture = PictureRect(dirtyRect);
        var dim = Color.FromRgba(0f, 0f, 0f, 0.55f);
        var accent = Mode == PhotoEditMode.Object ? ObjectColour : CropColour;
        canvas.Antialias = true;

        // Once the object has been found its own edge is the selection, so the box it was found in goes away
        // and the photo is dimmed all over with the object lit by its outline.
        if (Mode == PhotoEditMode.Object && (_edges is { Length: > 0 } || _tint is not null))
        {
            canvas.FillColor = Color.FromRgba(0f, 0f, 0f, 0.35f);
            canvas.FillRectangle(picture);

            if (_tint is not null)
                canvas.DrawImage(
                    _tint,
                    picture.Left + _tintBounds.X * picture.Width,
                    picture.Top + _tintBounds.Y * picture.Height,
                    _tintBounds.Width * picture.Width,
                    _tintBounds.Height * picture.Height);

            if (_edges is { Length: > 0 })
            {
                var outline = new PathF();
                for (var i = 0; i + 3 < _edges.Length; i += 4)
                {
                    outline.MoveTo(
                        picture.Left + _edges[i] * picture.Width,
                        picture.Top + _edges[i + 1] * picture.Height);
                    outline.LineTo(
                        picture.Left + _edges[i + 2] * picture.Width,
                        picture.Top + _edges[i + 3] * picture.Height);
                }

                canvas.StrokeColor = accent;
                canvas.StrokeSize = 2.5f;
                canvas.DrawPath(outline);
            }

            return;
        }

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

        canvas.StrokeColor = accent;
        canvas.StrokeSize = 2.5f;
        canvas.DrawRectangle(left, top, width, height);

        const float handle = 7f;
        canvas.FillColor = accent;
        foreach (var (hx, hy) in new[] { (left, top), (left + width, top), (left, top + height), (left + width, top + height) })
            canvas.FillRoundedRectangle(hx - handle / 2, hy - handle / 2, handle, handle, 2f);
    }

    /// <summary>
    /// Starts nudging the found object's outline if the touch landed on its edge. Touching the kept side trims
    /// the object back; touching just outside pushes the outline outwards. Returns false for a touch anywhere
    /// else, which starts a new box instead.
    /// </summary>
    private bool TryBeginRefine(PointF point)
    {
        var mask = _mask;
        if (mask is null || _tintBounds.Width <= 0f || _tintBounds.Height <= 0f)
            return false;

        var fraction = ToFraction(point);
        var x = (fraction.X - _tintBounds.X) / _tintBounds.Width * mask.Width;
        var y = (fraction.Y - _tintBounds.Y) / _tintBounds.Height * mask.Height;

        var grab = Math.Max(3f, Math.Min(mask.Width, mask.Height) / 22f);
        if (!NearEdge(mask, x, y, grab))
            return false;

        _refining = true;
        _refineKeep = !mask.At((int)MathF.Floor(x), (int)MathF.Floor(y));
        _refineLast = fraction;
        EmitStroke(fraction);
        return true;
    }

    /// <summary>True when the sample square around a point holds both kept and unkept pixels, i.e. it is an edge.</summary>
    private static bool NearEdge(SelectionMask mask, float x, float y, float grab)
    {
        var fromX = (int)MathF.Floor(x - grab);
        var toX = (int)MathF.Ceiling(x + grab);
        var fromY = (int)MathF.Floor(y - grab);
        var toY = (int)MathF.Ceiling(y + grab);

        var kept = 0;
        var total = 0;

        for (var py = fromY; py <= toY; py++)
            for (var px = fromX; px <= toX; px++)
            {
                total++;

                // Pixels outside the mask count as background, so the outline's outer side is grabbable too.
                if (mask.At(px, py))
                    kept++;
            }

        return kept > 0 && kept < total;
    }

    /// <summary>How wide the brush is, as a fraction of the photo's width at the current zoom-independent size.</summary>
    private float BrushRadius() =>
        ImageWidth < 1 ? 0.03f : 0.03f * Math.Max(ImageWidth, ImageHeight) / ImageWidth;

    private void EmitStroke(PointF fraction) =>
        StrokeAdded?.Invoke(this, new SelectionStroke(fraction.X, fraction.Y, BrushRadius(), _refineKeep));

    /// <summary>Emits strokes along a drag, close enough together that the brush leaves a continuous mark.</summary>
    private void EmitSegment(PointF from, PointF to)
    {
        var dx = (to.X - from.X) * ImageWidth;
        var dy = (to.Y - from.Y) * ImageHeight;
        var distance = MathF.Sqrt(dx * dx + dy * dy);
        var spacing = MathF.Max(1f, BrushRadius() * ImageWidth * 0.5f);
        var steps = Math.Max(1, (int)MathF.Ceiling(distance / spacing));

        for (var step = 1; step <= steps; step++)
        {
            var t = step / (float)steps;
            EmitStroke(new PointF(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t));
        }
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
