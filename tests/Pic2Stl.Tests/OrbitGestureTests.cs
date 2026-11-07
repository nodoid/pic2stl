using Pic2Stl.Core.Geometry;

namespace Pic2Stl.Tests;

[TestFixture]
public class OrbitGestureTests
{
    private static readonly (float X, float Y)[] OneFingerStart = [(100, 100)];
    private static readonly (float X, float Y)[] PinchStart = [(100, 100), (200, 100)];

    [Test]
    public void OneFingerDrag_RotatesButNeverZooms()
    {
        var orbit = new OrbitGesture();
        var startYaw = orbit.Yaw;
        var startPitch = orbit.Pitch;

        orbit.Begin(OneFingerStart);
        var moved = orbit.Move([(160, 130)]);

        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.True);
            Assert.That(orbit.Yaw, Is.GreaterThan(startYaw));
            Assert.That(orbit.Pitch, Is.GreaterThan(startPitch));
            Assert.That(orbit.Zoom, Is.EqualTo(1f), "A single finger must not change the zoom.");
        });
    }

    [Test]
    public void TwoFingerSpread_ZoomsInByTheChangeInSpread()
    {
        var orbit = new OrbitGesture();

        orbit.Begin(PinchStart);
        orbit.Move([(50, 100), (250, 100)]);

        Assert.That(orbit.Zoom, Is.EqualTo(2f).Within(0.001f), "The fingers moved from 100 to 200 apart.");
    }

    [Test]
    public void TwoFingerPinch_ZoomsOut()
    {
        var orbit = new OrbitGesture();

        orbit.Begin(PinchStart);
        orbit.Move([(125, 100), (175, 100)]);

        Assert.That(orbit.Zoom, Is.EqualTo(0.5f).Within(0.001f));
    }

    [Test]
    public void TwoFingers_NeverRotateTheModel()
    {
        var orbit = new OrbitGesture();
        var startYaw = orbit.Yaw;

        orbit.Begin(PinchStart);

        // Both fingers slide together, so the spread is unchanged but the midpoint has moved.
        orbit.Move([(140, 160), (240, 160)]);

        Assert.Multiple(() =>
        {
            Assert.That(orbit.Yaw, Is.EqualTo(startYaw), "A pinch should not spin the model.");
            Assert.That(orbit.Zoom, Is.EqualTo(1f).Within(0.001f));
        });
    }

    [Test]
    public void Zoom_IsClamped()
    {
        var orbit = new OrbitGesture();

        orbit.Begin(PinchStart);
        orbit.Move([(0, 100), (5000, 100)]);
        Assert.That(orbit.Zoom, Is.EqualTo(OrbitGesture.MaxZoom));

        orbit.End();
        orbit.Begin([(0, 100), (500, 100)]);
        orbit.Move([(249, 100), (251, 100)]);
        Assert.That(orbit.Zoom, Is.EqualTo(OrbitGesture.MinZoom));
    }

    [Test]
    public void SecondFingerArrivingMidDrag_RebaselinesInsteadOfJumping()
    {
        var orbit = new OrbitGesture();

        orbit.Begin(OneFingerStart);
        orbit.Move([(130, 100)]);

        // The second finger lands far away; the distance between them must not be read as a pinch yet.
        var rebased = orbit.Move([(130, 100), (400, 400)]);

        Assert.Multiple(() =>
        {
            Assert.That(rebased, Is.False);
            Assert.That(orbit.Zoom, Is.EqualTo(1f), "Joining fingers should not jump the zoom.");
        });

        orbit.Move([(130, 100), (300, 300)]);
        Assert.That(orbit.Zoom, Is.LessThan(1f), "Once both fingers are tracked, moving them apart zooms.");
    }

    [Test]
    public void ThreeFingers_AreIgnored()
    {
        var orbit = new OrbitGesture();
        var yaw = orbit.Yaw;

        Assert.Multiple(() =>
        {
            Assert.That(orbit.Begin([(10, 10), (20, 20), (30, 30)]), Is.False);
            Assert.That(orbit.Move([(40, 40), (50, 50), (60, 60)]), Is.False);
            Assert.That(orbit.Yaw, Is.EqualTo(yaw));
            Assert.That(orbit.Zoom, Is.EqualTo(1f));
        });
    }

    [Test]
    public void Reset_RestoresTheStartingView()
    {
        var orbit = new OrbitGesture();
        var yaw = orbit.Yaw;

        orbit.Begin(PinchStart);
        orbit.Move([(0, 100), (400, 100)]);
        orbit.Reset();

        Assert.Multiple(() =>
        {
            Assert.That(orbit.Yaw, Is.EqualTo(yaw));
            Assert.That(orbit.Zoom, Is.EqualTo(1f));
        });
    }

    [Test]
    public void TouchesAfterEnd_AreIgnored()
    {
        var orbit = new OrbitGesture();

        orbit.Begin(PinchStart);
        orbit.Move([(50, 100), (250, 100)]);
        var zoom = orbit.Zoom;
        orbit.End();

        Assert.Multiple(() =>
        {
            Assert.That(orbit.Move([(0, 100), (400, 100)]), Is.False);
            Assert.That(orbit.Zoom, Is.EqualTo(zoom));
        });
    }
}
