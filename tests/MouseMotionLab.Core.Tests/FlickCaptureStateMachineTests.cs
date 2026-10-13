using MouseMotionLab.Core.Capture;
using MouseMotionLab.Core.Domain;

namespace MouseMotionLab.Core.Tests;

public sealed class FlickCaptureStateMachineTests
{
    private const long Frequency = 10_000_000;

    [Fact]
    public void ClickPacketIsIncludedAndEndpointIsExact()
    {
        var machine = new FlickCaptureStateMachine(Frequency);
        var sessionId = Guid.NewGuid();
        machine.Arm(sessionId, 7, 1_000);

        Assert.Null(machine.Handle(Event(2_000, 4, -2)));
        Assert.Null(machine.Handle(Event(12_000, 3, 1)));
        var trial = machine.Handle(Event(22_000, 2, 5, MouseButtonFlags.LeftDown));

        Assert.NotNull(trial);
        Assert.Equal(TrialStatus.Accepted, trial.Status);
        Assert.Equal(3, trial.Events.Count);
        Assert.Equal(9, trial.Metrics.TargetDx);
        Assert.Equal(4, trial.Metrics.TargetDy);
        Assert.Equal(22_000, trial.TerminalQpc);
        Assert.Equal(CaptureLifecycle.Idle, machine.Lifecycle);
    }

    [Fact]
    public void ClickWithoutMovementCreatesInvalidTrial()
    {
        var machine = new FlickCaptureStateMachine(Frequency);
        machine.Arm(Guid.NewGuid(), 1, 1_000);

        var trial = machine.Handle(Event(2_000, 0, 0, MouseButtonFlags.LeftDown));

        Assert.NotNull(trial);
        Assert.Equal(TrialStatus.Invalid, trial.Status);
        Assert.Equal("no_movement", trial.RejectionReason);
        Assert.Single(trial.Events);
    }

    [Fact]
    public void NonMonotonicTimestampInvalidatesTrial()
    {
        var machine = new FlickCaptureStateMachine(Frequency);
        machine.Arm(Guid.NewGuid(), 1, 1_000);
        Assert.Null(machine.Handle(Event(3_000, 2, 1)));

        var trial = machine.Handle(Event(2_500, 1, 1));

        Assert.NotNull(trial);
        Assert.Equal(TrialStatus.Invalid, trial.Status);
        Assert.Equal("non_monotonic_timestamp", trial.RejectionReason);
    }

    [Fact]
    public void BoundedEventBufferFailsClosed()
    {
        var machine = new FlickCaptureStateMachine(Frequency, maxEvents: 32);
        machine.Arm(Guid.NewGuid(), 1, 1_000);
        for (var index = 0; index < 32; index++)
        {
            Assert.Null(machine.Handle(Event(2_000 + index, 1, 0)));
        }

        var trial = machine.Handle(Event(3_000, 1, 0));

        Assert.NotNull(trial);
        Assert.Equal(TrialStatus.Invalid, trial.Status);
        Assert.Equal("event_buffer_overflow", trial.RejectionReason);
        Assert.Equal(32, trial.Events.Count);
    }

    [Fact]
    public void CancellationRetainsStartedRawData()
    {
        var machine = new FlickCaptureStateMachine(Frequency);
        machine.Arm(Guid.NewGuid(), 1, 1_000);
        Assert.Null(machine.Handle(Event(2_000, 2, 3)));

        var trial = machine.Cancel(5_000, "focus_lost");

        Assert.NotNull(trial);
        Assert.Equal(TrialStatus.Cancelled, trial.Status);
        Assert.Equal("focus_lost", trial.RejectionReason);
        Assert.Single(trial.Events);
    }

    [Fact]
    public void InvalidationRetainsArmedTrialForAudit()
    {
        var machine = new FlickCaptureStateMachine(Frequency);
        machine.Arm(Guid.NewGuid(), 1, 1_000);

        var trial = machine.Invalidate(5_000, "device_disconnected");

        Assert.NotNull(trial);
        Assert.Equal(TrialStatus.Invalid, trial.Status);
        Assert.Equal("device_disconnected", trial.RejectionReason);
        Assert.Empty(trial.Events);
        Assert.Equal(5_000, trial.FirstMotionQpc);
        Assert.Equal(CaptureLifecycle.Idle, machine.Lifecycle);
    }

    [Fact]
    public void TimingDiagnosticsReportMedianMaximumAndPauses()
    {
        var machine = new FlickCaptureStateMachine(Frequency);
        machine.Arm(Guid.NewGuid(), 1, 1_000);
        Assert.Null(machine.Handle(Event(2_000, 1, 0)));
        Assert.Null(machine.Handle(Event(12_000, 1, 0)));
        var trial = machine.Handle(Event(112_000, 1, 0, MouseButtonFlags.LeftDown));

        Assert.NotNull(trial);
        Assert.Equal(5_500d, trial.Metrics.MedianIntervalMicroseconds);
        Assert.Equal(10_000d, trial.Metrics.MaximumIntervalMicroseconds);
        Assert.Equal(1, trial.Metrics.PauseCount);
    }

    private static RawMouseEvent Event(
        long qpc,
        int dx,
        int dy,
        MouseButtonFlags buttons = MouseButtonFlags.None) =>
        new(qpc, dx, dy, 0, buttons, "mouse-1");
}

