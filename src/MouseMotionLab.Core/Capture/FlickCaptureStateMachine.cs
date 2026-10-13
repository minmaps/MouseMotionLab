using MouseMotionLab.Core.Domain;

namespace MouseMotionLab.Core.Capture;

public sealed class FlickCaptureStateMachine
{
    private readonly object _gate = new();
    private readonly List<RawMouseEvent> _events;
    private readonly int _maxEvents;
    private readonly long _qpcFrequency;
    private CaptureLifecycle _lifecycle = CaptureLifecycle.Idle;
    private Guid _sessionId;
    private int _ordinal;
    private long _armedQpc;
    private long _firstMotionQpc;
    private long _lastQpc;
    private bool _overflowed;

    public FlickCaptureStateMachine(long qpcFrequency, int maxEvents = 8_192)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpcFrequency);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEvents, 32);
        _qpcFrequency = qpcFrequency;
        _maxEvents = maxEvents;
        _events = new List<RawMouseEvent>(Math.Min(maxEvents, 1_024));
    }

    public CaptureLifecycle Lifecycle
    {
        get
        {
            lock (_gate)
            {
                return _lifecycle;
            }
        }
    }

    public void Arm(Guid sessionId, int ordinal, long qpcTicks)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));
        }

        lock (_gate)
        {
            if (_lifecycle != CaptureLifecycle.Idle)
            {
                throw new InvalidOperationException("A trial is already armed or recording.");
            }

            _sessionId = sessionId;
            _ordinal = ordinal;
            _armedQpc = qpcTicks;
            _firstMotionQpc = 0;
            _lastQpc = qpcTicks;
            _overflowed = false;
            _events.Clear();
            _lifecycle = CaptureLifecycle.Armed;
        }
    }

    public TrialCapture? Handle(RawMouseEvent mouseEvent)
    {
        lock (_gate)
        {
            if (_lifecycle == CaptureLifecycle.Idle)
            {
                return null;
            }

            if (mouseEvent.QpcTicks < _lastQpc)
            {
                return Complete(
                    TrialStatus.Invalid,
                    "non_monotonic_timestamp",
                    Math.Max(_lastQpc, mouseEvent.QpcTicks));
            }

            _lastQpc = mouseEvent.QpcTicks;

            if (_lifecycle == CaptureLifecycle.Armed && mouseEvent.HasMovement)
            {
                _firstMotionQpc = mouseEvent.QpcTicks;
                _lifecycle = CaptureLifecycle.Recording;
            }

            if (_lifecycle == CaptureLifecycle.Recording)
            {
                if (_events.Count >= _maxEvents)
                {
                    _overflowed = true;
                    return Complete(TrialStatus.Invalid, "event_buffer_overflow", mouseEvent.QpcTicks);
                }

                _events.Add(mouseEvent);
            }

            if (mouseEvent.HasLeftDown)
            {
                if (_lifecycle == CaptureLifecycle.Armed)
                {
                    _firstMotionQpc = mouseEvent.QpcTicks;
                    _events.Add(mouseEvent);
                    return Complete(TrialStatus.Invalid, "no_movement", mouseEvent.QpcTicks);
                }

                return Complete(TrialStatus.Accepted, null, mouseEvent.QpcTicks);
            }

            return null;
        }
    }

    public TrialCapture? Cancel(long qpcTicks, string reason = "cancelled_by_user")
    {
        lock (_gate)
        {
            if (_lifecycle == CaptureLifecycle.Idle)
            {
                return null;
            }

            if (_lifecycle == CaptureLifecycle.Armed || _events.Count == 0)
            {
                ResetUnsafe();
                return null;
            }

            return Complete(TrialStatus.Cancelled, reason, Math.Max(qpcTicks, _lastQpc));
        }
    }

    public TrialCapture? Invalidate(long qpcTicks, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("An invalidation reason is required.", nameof(reason));
        }

        lock (_gate)
        {
            if (_lifecycle == CaptureLifecycle.Idle)
            {
                return null;
            }

            return Complete(TrialStatus.Invalid, reason, Math.Max(qpcTicks, _lastQpc));
        }
    }

    public CaptureSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new CaptureSnapshot(
                _lifecycle,
                _armedQpc,
                _firstMotionQpc,
                _events.ToArray(),
                _overflowed);
        }
    }

    private TrialCapture Complete(TrialStatus status, string? reason, long terminalQpc)
    {
        var firstMotion = _firstMotionQpc == 0 ? terminalQpc : _firstMotionQpc;
        var trial = TrialCapture.Create(
            Guid.NewGuid(),
            _sessionId,
            _ordinal,
            _armedQpc,
            firstMotion,
            terminalQpc,
            status,
            reason,
            _events,
            _qpcFrequency);

        ResetUnsafe();
        return trial;
    }

    private void ResetUnsafe()
    {
        _lifecycle = CaptureLifecycle.Idle;
        _sessionId = Guid.Empty;
        _ordinal = 0;
        _armedQpc = 0;
        _firstMotionQpc = 0;
        _lastQpc = 0;
        _overflowed = false;
        _events.Clear();
    }
}

