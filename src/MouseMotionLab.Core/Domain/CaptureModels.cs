using System.Collections.ObjectModel;

namespace MouseMotionLab.Core.Domain;

public enum CaptureLifecycle
{
    Idle,
    Armed,
    Recording
}

public enum TrialStatus
{
    Accepted,
    Excluded,
    Invalid,
    Cancelled
}

[Flags]
public enum MouseButtonFlags : ushort
{
    None = 0x0000,
    LeftDown = 0x0001,
    LeftUp = 0x0002,
    RightDown = 0x0004,
    RightUp = 0x0008,
    MiddleDown = 0x0010,
    MiddleUp = 0x0020,
    Button4Down = 0x0040,
    Button4Up = 0x0080,
    Button5Down = 0x0100,
    Button5Up = 0x0200,
    Wheel = 0x0400,
    HorizontalWheel = 0x0800
}

public sealed record RawMouseEvent(
    long QpcTicks,
    int Dx,
    int Dy,
    short WheelDelta,
    MouseButtonFlags ButtonFlags,
    string DeviceId,
    int? CursorDxPixels = null,
    int? CursorDyPixels = null)
{
    public bool HasMovement => Dx != 0 || Dy != 0;

    public bool HasLeftDown => (ButtonFlags & MouseButtonFlags.LeftDown) != 0;

    public bool HasCursorSample => CursorDxPixels.HasValue && CursorDyPixels.HasValue;
}

public sealed record CaptureProfile(
    Guid Id,
    string DeviceId,
    string DisplayName,
    int Dpi,
    int NominalPollingHz,
    double DisplayPixelsPerMillimeter,
    DateTimeOffset CreatedUtc)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DeviceId))
        {
            throw new ArgumentException("A Raw Input device must be selected.", nameof(DeviceId));
        }

        if (Dpi is < 100 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(Dpi), "DPI must be between 100 and 100000.");
        }

        if (NominalPollingHz is < 125 or > 8_000)
        {
            throw new ArgumentOutOfRangeException(nameof(NominalPollingHz), "Polling rate must be between 125 and 8000 Hz.");
        }

        if (DisplayPixelsPerMillimeter is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DisplayPixelsPerMillimeter),
                "Display scale must be greater than 0 and at most 100 px/mm.");
        }
    }
}

public sealed record CaptureSession(
    Guid Id,
    Guid ProfileId,
    DateTimeOffset StartedUtc,
    string AppVersion,
    string OsVersion,
    long QpcFrequency);

public sealed record TrialMetrics(
    int TargetDx,
    int TargetDy,
    double PathLengthCounts,
    double DurationMicroseconds,
    double EffectivePacketHz,
    int EventCount,
    double MedianIntervalMicroseconds,
    double MaximumIntervalMicroseconds,
    int PauseCount);

public sealed record TrialCapture(
    Guid Id,
    Guid SessionId,
    int Ordinal,
    long ArmedQpc,
    long FirstMotionQpc,
    long TerminalQpc,
    TrialStatus Status,
    string? RejectionReason,
    TrialMetrics Metrics,
    IReadOnlyList<RawMouseEvent> Events,
    DateTimeOffset CreatedUtc)
{
    public static TrialCapture Create(
        Guid id,
        Guid sessionId,
        int ordinal,
        long armedQpc,
        long firstMotionQpc,
        long terminalQpc,
        TrialStatus status,
        string? rejectionReason,
        IReadOnlyList<RawMouseEvent> events,
        long qpcFrequency,
        DateTimeOffset? createdUtc = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpcFrequency);

        var eventCopy = new ReadOnlyCollection<RawMouseEvent>(events.ToArray());
        var targetDx = eventCopy.Sum(item => item.Dx);
        var targetDy = eventCopy.Sum(item => item.Dy);
        var pathLength = eventCopy.Sum(item => Math.Sqrt((double)item.Dx * item.Dx + (double)item.Dy * item.Dy));
        var durationTicks = Math.Max(0, terminalQpc - firstMotionQpc);
        var durationUs = durationTicks * 1_000_000d / qpcFrequency;
        var effectiveHz = durationTicks > 0 && eventCopy.Count > 1
            ? (eventCopy.Count - 1) * (double)qpcFrequency / durationTicks
            : 0d;
        var intervalMicroseconds = eventCopy
            .Zip(eventCopy.Skip(1), (left, right) =>
                Math.Max(0, right.QpcTicks - left.QpcTicks) * 1_000_000d / qpcFrequency)
            .ToArray();
        Array.Sort(intervalMicroseconds);
        var medianIntervalUs = intervalMicroseconds.Length switch
        {
            0 => 0d,
            var length when length % 2 == 1 => intervalMicroseconds[length / 2],
            var length => (intervalMicroseconds[length / 2 - 1] + intervalMicroseconds[length / 2]) / 2d
        };
        var maximumIntervalUs = intervalMicroseconds.Length == 0 ? 0d : intervalMicroseconds[^1];
        var pauseCount = intervalMicroseconds.Count(value => value > 8_000d);

        var metrics = new TrialMetrics(
            targetDx,
            targetDy,
            pathLength,
            durationUs,
            effectiveHz,
            eventCopy.Count,
            medianIntervalUs,
            maximumIntervalUs,
            pauseCount);

        return new TrialCapture(
            id,
            sessionId,
            ordinal,
            armedQpc,
            firstMotionQpc,
            terminalQpc,
            status,
            rejectionReason,
            metrics,
            eventCopy,
            createdUtc ?? DateTimeOffset.UtcNow);
    }
}

public sealed record CaptureSnapshot(
    CaptureLifecycle Lifecycle,
    long ArmedQpc,
    long FirstMotionQpc,
    IReadOnlyList<RawMouseEvent> Events,
    bool Overflowed);

