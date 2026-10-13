using MouseMotionLab.Capture.Services;

namespace MouseMotionLab.Capture.Tests;

public sealed class FirstPersonLookControllerTests
{
    [Fact]
    public void RawDeltasRotateYawAndInvertPitch()
    {
        var controller = new FirstPersonLookController();

        controller.ApplyRawDelta(100, 25, 0.05);

        Assert.Equal(5d, controller.YawDegrees, precision: 8);
        Assert.Equal(-1.25d, controller.PitchDegrees, precision: 8);
        Assert.Equal(1d, controller.LookDirection.Length, precision: 8);
    }

    [Fact]
    public void YawWrapsAndPitchIsClamped()
    {
        var controller = new FirstPersonLookController();

        controller.ApplyRawDelta(4_000, -4_000, 0.1);

        Assert.Equal(40d, controller.YawDegrees, precision: 8);
        Assert.Equal(85d, controller.PitchDegrees, precision: 8);
    }

    [Fact]
    public void OrientationPersistsAcrossIndependentFlicks()
    {
        var controller = new FirstPersonLookController();
        controller.ApplyRawDelta(200, -50, 0.04);
        var yawBeforeArm = controller.YawDegrees;
        var pitchBeforeArm = controller.PitchDegrees;

        // Arming is deliberately not part of the camera controller. Starting a new
        // trial therefore cannot recenter or otherwise alter the current view.
        controller.ApplyRawDelta(0, 0, 0.04);

        Assert.Equal(yawBeforeArm, controller.YawDegrees);
        Assert.Equal(pitchBeforeArm, controller.PitchDegrees);
    }
}
