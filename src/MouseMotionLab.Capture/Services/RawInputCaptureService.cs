using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows.Input;
using MouseMotionLab.Capture.Interop;
using MouseMotionLab.Core.Domain;

namespace MouseMotionLab.Capture.Services;

public sealed class RawInputCaptureService : IAsyncDisposable
{
    private readonly object _deviceGate = new();
    private readonly Channel<CapturedMousePacket> _packets;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RawInputNative.WindowProcedure _windowProcedure;
    private readonly Thread _nativeThread;
    private readonly Task _consumer;
    private Dictionary<nint, RawInputDeviceDescriptor> _devicesByHandle = new();
    private IReadOnlyList<RawInputDeviceDescriptor> _devices = Array.Empty<RawInputDeviceDescriptor>();
    private RawInputNative.Point _currentMessageCursorPosition;
    private nint _window;
    private string? _selectedDeviceId;
    private int _captureEnabled;
    private int _cursorOriginX;
    private int _cursorOriginY;
    private int _cursorOriginValid;
    private long _droppedPackets;
    private bool _started;

    public RawInputCaptureService(int packetQueueCapacity = 8_192)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(packetQueueCapacity, 256);
        if (!RawInputNative.QueryPerformanceFrequency(out var frequency) || frequency <= 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryPerformanceFrequency failed.");
        }

        QpcFrequency = frequency;
        _packets = Channel.CreateBounded<CapturedMousePacket>(new BoundedChannelOptions(packetQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            // The Win32 thread never waits. With Wait mode, TryWrite returns false
            // when saturated so the active trial can be failed closed explicitly.
            FullMode = BoundedChannelFullMode.Wait
        });
        _windowProcedure = WindowProcedure;
        _nativeThread = new Thread(NativeThreadMain)
        {
            IsBackground = true,
            Name = "MouseMotionLab.RawInput"
        };
        _consumer = Task.Run(ConsumePacketsAsync);
    }

    public event EventHandler<RawMouseEvent>? PacketReceived;

    public Func<RawMouseEvent, ModifierKeys, Task>? PacketHandlerAsync { get; set; }

    public event EventHandler? PacketDropped;

    public event EventHandler? DevicesChanged;

    public event EventHandler<Exception>? ServiceError;

    public long QpcFrequency { get; }

    public long DroppedPackets => Interlocked.Read(ref _droppedPackets);

    public int PendingPackets => _packets.Reader.CanCount ? _packets.Reader.Count : -1;

    public IReadOnlyList<RawInputDeviceDescriptor> Devices
    {
        get
        {
            lock (_deviceGate)
            {
                return _devices.ToArray();
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _started = true;
        _nativeThread.Start();
        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void SetCaptureGate(bool enabled, string? selectedDeviceId)
    {
        if (!enabled)
        {
            Volatile.Write(ref _captureEnabled, 0);
            Volatile.Write(ref _cursorOriginValid, 0);
            Volatile.Write(ref _selectedDeviceId, null);
            return;
        }

        if (string.IsNullOrWhiteSpace(selectedDeviceId))
        {
            throw new ArgumentException("A selected Raw Input device is required.", nameof(selectedDeviceId));
        }

        if (!RawInputNative.GetCursorPos(out var cursorOrigin))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetCursorPos(origin) failed.");
        }

        Volatile.Write(ref _cursorOriginX, cursorOrigin.X);
        Volatile.Write(ref _cursorOriginY, cursorOrigin.Y);
        Volatile.Write(ref _selectedDeviceId, selectedDeviceId);
        Volatile.Write(ref _cursorOriginValid, 1);
        Volatile.Write(ref _captureEnabled, 1);
    }

    public long TimestampNow()
    {
        if (!RawInputNative.QueryPerformanceCounter(out var value))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryPerformanceCounter failed.");
        }

        return value;
    }

    public async ValueTask DisposeAsync()
    {
        SetCaptureGate(false, null);
        if (_window != nint.Zero)
        {
            _ = RawInputNative.PostMessage(_window, RawInputNative.WmClose, 0, 0);
        }

        if (_nativeThread.IsAlive)
        {
            await Task.Run(() => _nativeThread.Join(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }

        _packets.Writer.TryComplete();
        _cancellation.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await _consumer.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // Expected if the consumer cannot drain during shutdown.
        }

        _cancellation.Dispose();
    }

    private void NativeThreadMain()
    {
        var className = $"MouseMotionLab.RawInput.{Guid.NewGuid():N}";
        var instance = RawInputNative.GetModuleHandle(null);
        var procedurePointer = Marshal.GetFunctionPointerForDelegate(_windowProcedure);
        var windowClass = new RawInputNative.WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<RawInputNative.WindowClassEx>(),
            Style = RawInputNative.CsHRedraw | RawInputNative.CsVRedraw,
            WindowProcedure = procedurePointer,
            Instance = instance,
            ClassName = className
        };

        try
        {
            if (RawInputNative.RegisterClassEx(ref windowClass) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed.");
            }

            _window = RawInputNative.CreateWindowEx(
                0,
                className,
                "MouseMotionLab Raw Input",
                0,
                0,
                0,
                0,
                0,
                RawInputNative.HwndMessage,
                0,
                instance,
                0);
            if (_window == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed.");
            }

            var registration = new[]
            {
                new RawInputNative.RawInputDeviceRegistration
                {
                    UsagePage = RawInputNative.HidUsagePageGeneric,
                    Usage = RawInputNative.HidUsageGenericMouse,
                    Flags = RawInputNative.RidevInputSink | RawInputNative.RidevDevNotify,
                    TargetWindow = _window
                }
            };
            if (!RawInputNative.RegisterRawInputDevices(
                    registration,
                    (uint)registration.Length,
                    (uint)Marshal.SizeOf<RawInputNative.RawInputDeviceRegistration>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterRawInputDevices failed.");
            }

            RefreshDevices();
            _ready.TrySetResult();

            while (true)
            {
                var result = RawInputNative.GetMessage(out var message, nint.Zero, 0, 0);
                if (result == 0)
                {
                    break;
                }

                if (result == -1)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMessage failed.");
                }

                _currentMessageCursorPosition = message.Position;
                _ = RawInputNative.TranslateMessage(ref message);
                _ = RawInputNative.DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            ServiceError?.Invoke(this, exception);
        }
        finally
        {
            if (_window != nint.Zero)
            {
                _ = RawInputNative.DestroyWindow(_window);
                _window = nint.Zero;
            }

            _ = RawInputNative.UnregisterClass(className, instance);
            _packets.Writer.TryComplete();
        }
    }

    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case RawInputNative.WmInput:
                    ProcessRawInput(lParam);
                    return nint.Zero;
                case RawInputNative.WmInputDeviceChange:
                    RefreshDevices();
                    DevicesChanged?.Invoke(this, EventArgs.Empty);
                    return nint.Zero;
                case RawInputNative.WmClose:
                    _ = RawInputNative.DestroyWindow(window);
                    return nint.Zero;
                case RawInputNative.WmDestroy:
                    RawInputNative.PostQuitMessage(0);
                    return nint.Zero;
            }
        }
        catch (Exception exception)
        {
            ServiceError?.Invoke(this, exception);
        }

        return RawInputNative.DefWindowProc(window, message, wParam, lParam);
    }

    private void ProcessRawInput(nint rawInputHandle)
    {
        uint size = 0;
        var headerSize = (uint)RawInputParser.HeaderSize;
        var firstResult = RawInputNative.GetRawInputData(
            rawInputHandle,
            RawInputNative.RidInput,
            nint.Zero,
            ref size,
            headerSize);
        if (firstResult == uint.MaxValue || size == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetRawInputData(size) failed.");
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var copied = RawInputNative.GetRawInputData(
                rawInputHandle,
                RawInputNative.RidInput,
                buffer,
                ref size,
                headerSize);
            if (copied == uint.MaxValue || copied != size)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetRawInputData(payload) failed.");
            }

            var bytes = new byte[size];
            Marshal.Copy(buffer, bytes, 0, (int)size);
            var qpc = TimestampNow();
            var mouseEvent = RawInputParser.ParseMouseInput(bytes, qpc, ResolveDeviceId);
            if (mouseEvent is null || Volatile.Read(ref _captureEnabled) == 0)
            {
                return;
            }

            var selected = Volatile.Read(ref _selectedDeviceId);
            if (!RawInputDeviceFilter.IsSelected(mouseEvent, selected))
            {
                return;
            }

            if (Volatile.Read(ref _cursorOriginValid) == 0)
            {
                return;
            }

            var cursorPosition = _currentMessageCursorPosition;
            mouseEvent = mouseEvent with
            {
                CursorDxPixels = cursorPosition.X - Volatile.Read(ref _cursorOriginX),
                CursorDyPixels = cursorPosition.Y - Volatile.Read(ref _cursorOriginY)
            };

            var modifiers = mouseEvent.ButtonFlags != MouseButtonFlags.None
                ? KeyboardModifierState.Read()
                : ModifierKeys.None;
            if (!_packets.Writer.TryWrite(new CapturedMousePacket(mouseEvent, modifiers)))
            {
                Interlocked.Increment(ref _droppedPackets);
                PacketDropped?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private string? ResolveDeviceId(nint handle)
    {
        lock (_deviceGate)
        {
            return _devicesByHandle.TryGetValue(handle, out var descriptor) ? descriptor.Id : null;
        }
    }

    private void RefreshDevices()
    {
        var devices = RawInputDeviceEnumerator.EnumerateMice();
        lock (_deviceGate)
        {
            _devices = devices;
            _devicesByHandle = devices.ToDictionary(item => item.Handle);
        }
    }

    private async Task ConsumePacketsAsync()
    {
        await foreach (var packet in _packets.Reader.ReadAllAsync(_cancellation.Token).ConfigureAwait(false))
        {
            try
            {
                if (PacketHandlerAsync is { } handler)
                {
                    await handler(packet.Event, packet.Modifiers).ConfigureAwait(false);
                }

                PacketReceived?.Invoke(this, packet.Event);
            }
            catch (Exception exception)
            {
                ServiceError?.Invoke(this, exception);
            }
        }
    }

    private readonly record struct CapturedMousePacket(RawMouseEvent Event, ModifierKeys Modifiers);
}

