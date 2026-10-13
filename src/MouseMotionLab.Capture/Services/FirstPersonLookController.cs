using System.Windows.Media.Media3D;

namespace MouseMotionLab.Capture.Services;

public sealed class FirstPersonLookController
{
    private const double MaximumPitchDegrees = 85d;

    public double YawDegrees { get; private set; }

    public double PitchDegrees { get; private set; }

    public Vector3D LookDirection
    {
        get
        {
            var yaw = DegreesToRadians(YawDegrees);
            var pitch = DegreesToRadians(PitchDegrees);
            var horizontal = Math.Cos(pitch);
            var direction = new Vector3D(
                horizontal * Math.Sin(yaw),
                Math.Sin(pitch),
                -horizontal * Math.Cos(yaw));
            direction.Normalize();
            return direction;
        }
    }

    public void ApplyRawDelta(int dx, int dy, double degreesPerCount)
    {
        if (!double.IsFinite(degreesPerCount) || degreesPerCount <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(degreesPerCount),
                "Mouse sensitivity must be finite and positive.");
        }

        YawDegrees = NormalizeYaw(YawDegrees + dx * degreesPerCount);
        PitchDegrees = Math.Clamp(
            PitchDegrees - dy * degreesPerCount,
            -MaximumPitchDegrees,
            MaximumPitchDegrees);
    }

    public void SetOrientation(double yawDegrees, double pitchDegrees)
    {
        if (!double.IsFinite(yawDegrees) || !double.IsFinite(pitchDegrees))
        {
            throw new ArgumentOutOfRangeException(
                nameof(yawDegrees),
                "Camera angles must be finite.");
        }

        YawDegrees = NormalizeYaw(yawDegrees);
        PitchDegrees = Math.Clamp(pitchDegrees, -MaximumPitchDegrees, MaximumPitchDegrees);
    }

    private static double NormalizeYaw(double yawDegrees)
    {
        var normalized = (yawDegrees + 180d) % 360d;
        if (normalized < 0d)
        {
            normalized += 360d;
        }

        return normalized - 180d;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}
