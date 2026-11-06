using Pic2Stl.Core.Colours;
using Pic2Stl.Core.Geometry;

namespace Pic2Stl.Controls;

/// <summary>
/// Draws a mesh on a <see cref="GraphicsView"/> and lets the user rotate it by dragging and zoom it by pinching.
/// Triangles are flat shaded, back faces are culled and the rest are painted back to front.
/// </summary>
public sealed class MeshViewer : GraphicsView, IDrawable
{
    private static readonly Vector3 LightDirection = new Vector3(-0.35f, 0.55f, 0.8f).Normalized();

    private Mesh? _mesh;
    private IReadOnlyList<Rgb> _palette = [];
    private float _yaw = -0.55f;
    private float _pitch = 0.32f;
    private float _zoom = 1f;
    private float _startYaw;
    private float _startPitch;
    private float _startZoom = 1f;

    public MeshViewer()
    {
        Drawable = this;

        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnPanUpdated;
        GestureRecognizers.Add(pan);

        var pinch = new PinchGestureRecognizer();
        pinch.PinchUpdated += OnPinchUpdated;
        GestureRecognizers.Add(pinch);
    }

    public bool HasModel => _mesh is { Triangles.Count: > 0 };

    public void SetMesh(Mesh mesh, IReadOnlyList<Rgb> palette)
    {
        _mesh = mesh;
        _palette = palette;
        ResetView();
    }

    public void ResetView()
    {
        _yaw = -0.55f;
        _pitch = 0.32f;
        _zoom = 1f;
        Invalidate();
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _startYaw = _yaw;
                _startPitch = _pitch;
                break;

            case GestureStatus.Running:
                _yaw = _startYaw + (float)e.TotalX * 0.01f;
                _pitch = Math.Clamp(_startPitch + (float)e.TotalY * 0.01f, -1.45f, 1.45f);
                Invalidate();
                break;
        }
    }

    private void OnPinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        switch (e.Status)
        {
            case GestureStatus.Started:
                _startZoom = _zoom;
                break;

            case GestureStatus.Running:
                _zoom = Math.Clamp(_startZoom + (float)(e.Scale - 1) * _startZoom, 0.35f, 5f);
                Invalidate();
                break;
        }
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        canvas.FillColor = Color.FromArgb("#0E1220");
        canvas.FillRectangle(dirtyRect);

        var mesh = _mesh;
        if (mesh is null || mesh.Triangles.Count == 0)
            return;

        var source = mesh.Vertices;
        var rotated = new Vector3[source.Count];
        var sinYaw = MathF.Sin(_yaw);
        var cosYaw = MathF.Cos(_yaw);
        var sinPitch = MathF.Sin(_pitch);
        var cosPitch = MathF.Cos(_pitch);

        for (var i = 0; i < source.Count; i++)
        {
            var v = source[i];
            var x1 = v.X * cosYaw + v.Z * sinYaw;
            var z1 = -v.X * sinYaw + v.Z * cosYaw;
            var y2 = v.Y * cosPitch - z1 * sinPitch;
            var z2 = v.Y * sinPitch + z1 * cosPitch;
            rotated[i] = new Vector3(x1, y2, z2);
        }

        var scale = MathF.Min(dirtyRect.Width, dirtyRect.Height) * 0.44f * _zoom / MathF.Max(Radius(source), 1e-3f);
        var centerX = dirtyRect.Center.X;
        var centerY = dirtyRect.Center.Y;

        float ToScreenX(Vector3 v) => centerX + v.X * scale;
        float ToScreenY(Vector3 v) => centerY - v.Y * scale;

        // Back-to-front order gives correct occlusion for a relief viewed from outside.
        var visible = new List<(float Depth, int Index)>(mesh.Triangles.Count);
        var normals = new Vector3[mesh.Triangles.Count];

        for (var t = 0; t < mesh.Triangles.Count; t++)
        {
            var triangle = mesh.Triangles[t];
            var a = rotated[triangle.A];
            var b = rotated[triangle.B];
            var c = rotated[triangle.C];
            var normal = Vector3.Cross(b - a, c - a);

            if (normal.Z <= 0)
                continue;

            normals[t] = normal.Normalized();
            visible.Add(((a.Z + b.Z + c.Z) / 3f, t));
        }

        visible.Sort(static (left, right) => left.Depth.CompareTo(right.Depth));
        canvas.Antialias = true;

        foreach (var (_, index) in visible)
        {
            var triangle = mesh.Triangles[index];
            var shade = 0.35f + 0.65f * MathF.Max(0f, Vector3.Dot(normals[index], LightDirection));
            var colour = _palette.Count > 0
                ? _palette[Math.Min(triangle.Material, _palette.Count - 1)]
                : new Rgb(200, 200, 200);

            canvas.FillColor = Color.FromRgba(
                Math.Clamp(colour.R / 255f * shade, 0f, 1f),
                Math.Clamp(colour.G / 255f * shade, 0f, 1f),
                Math.Clamp(colour.B / 255f * shade, 0f, 1f),
                1f);

            var a = rotated[triangle.A];
            var b = rotated[triangle.B];
            var c = rotated[triangle.C];

            var path = new PathF();
            path.MoveTo(ToScreenX(a), ToScreenY(a));
            path.LineTo(ToScreenX(b), ToScreenY(b));
            path.LineTo(ToScreenX(c), ToScreenY(c));
            path.Close();
            canvas.FillPath(path);
        }
    }

    private static float Radius(IReadOnlyList<Vector3> vertices)
    {
        var max = 0f;
        foreach (var v in vertices)
        {
            var distance = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            max = MathF.Max(max, distance);
        }

        return max;
    }
}
