namespace Pic2Stl.Core.Geometry;

/// <summary>
/// Turns finger drags into an orbit of a 3D object: one finger rotates it, two fingers pinch to zoom. The rules
/// live here rather than in the view so they can be tested without a screen, and so a two-finger pinch can never
/// be mistaken for a one-finger drag.
/// </summary>
public sealed class OrbitGesture
{
    public const float MinZoom = 0.35f;
    public const float MaxZoom = 5f;

    /// <summary>Radians of rotation per point of finger travel.</summary>
    private const float RadiansPerPoint = 0.01f;

    private bool _active;
    private int _fingers;
    private float _startYaw;
    private float _startPitch;
    private float _startZoom;
    private float _startX;
    private float _startY;
    private float _startSpread = 1f;

    public float Yaw { get; private set; } = -0.55f;

    public float Pitch { get; private set; } = 0.32f;

    public float Zoom { get; private set; } = 1f;

    public void Reset()
    {
        Yaw = -0.55f;
        Pitch = 0.32f;
        Zoom = 1f;
        _active = false;
    }

    /// <summary>Starts a gesture from the points currently down. Returns false if there is nothing to track.</summary>
    public bool Begin(ReadOnlySpan<(float X, float Y)> points)
    {
        _active = points.Length is 1 or 2;
        if (!_active)
            return false;

        _fingers = points.Length;
        _startYaw = Yaw;
        _startPitch = Pitch;
        _startZoom = Zoom;
        _startX = points[0].X;
        _startY = points[0].Y;
        _startSpread = Spread(points);
        return true;
    }

    /// <summary>Applies a drag. Returns false when nothing changed.</summary>
    public bool Move(ReadOnlySpan<(float X, float Y)> points)
    {
        if (!_active || points.Length == 0)
            return false;

        // A finger arriving or leaving mid-drag re-baselines instead of jumping the model about.
        if (points.Length != _fingers)
        {
            Begin(points);
            return false;
        }

        if (points.Length == 1)
        {
            Yaw = _startYaw + (points[0].X - _startX) * RadiansPerPoint;
            Pitch = Math.Clamp(_startPitch + (points[0].Y - _startY) * RadiansPerPoint, -1.45f, 1.45f);
            return true;
        }

        // Two fingers only ever zoom: the spread between them sets the scale, not either finger alone.
        Zoom = Math.Clamp(_startZoom * (Spread(points) / _startSpread), MinZoom, MaxZoom);
        return true;
    }

    public void End() => _active = false;

    private static float Spread(ReadOnlySpan<(float X, float Y)> points)
    {
        if (points.Length < 2)
            return 1f;

        var dx = points[1].X - points[0].X;
        var dy = points[1].Y - points[0].Y;
        return MathF.Max(MathF.Sqrt(dx * dx + dy * dy), 1f);
    }
}
