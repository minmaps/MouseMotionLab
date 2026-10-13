using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using MouseMotionLab.Capture.Interop;

namespace MouseMotionLab.Capture.Services;

public sealed class CursorConfinementLease : IDisposable
{
    private static readonly object StaticGate = new();
    private static CursorConfinementLease? _current;
    private readonly RawInputNative.Point _originalPosition;
    private readonly RawInputNative.Rect _originalClip;
    private bool _disposed;

    private CursorConfinementLease(
        RawInputNative.Point originalPosition,
        RawInputNative.Rect originalClip)
    {
        _originalPosition = originalPosition;
        _originalClip = originalClip;
    }

    public static CursorConfinementLease Acquire(FrameworkElement canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        canvas.Dispatcher.VerifyAccess();

        lock (StaticGate)
        {
            _current?.DisposeUnsafe();
            _current = null;

            if (!RawInputNative.GetCursorPos(out var original))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetCursorPos failed.");
            }

            if (!RawInputNative.GetClipCursor(out var originalClip))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetClipCursor failed.");
            }

            if (canvas.ActualWidth < 2d || canvas.ActualHeight < 2d)
            {
                throw new InvalidOperationException("The capture area is not ready.");
            }

            var topLeft = canvas.PointToScreen(new Point(0, 0));
            var bottomRight = canvas.PointToScreen(new Point(canvas.ActualWidth, canvas.ActualHeight));
            var center = canvas.PointToScreen(new Point(canvas.ActualWidth / 2d, canvas.ActualHeight / 2d));
            var x = checked((int)Math.Round(center.X));
            var y = checked((int)Math.Round(center.Y));
            var rectangle = new RawInputNative.Rect
            {
                Left = checked((int)Math.Floor(Math.Min(topLeft.X, bottomRight.X))),
                Top = checked((int)Math.Floor(Math.Min(topLeft.Y, bottomRight.Y))),
                Right = checked((int)Math.Ceiling(Math.Max(topLeft.X, bottomRight.X))),
                Bottom = checked((int)Math.Ceiling(Math.Max(topLeft.Y, bottomRight.Y)))
            };
            try
            {
                if (rectangle.Right <= rectangle.Left || rectangle.Bottom <= rectangle.Top)
                {
                    throw new InvalidOperationException("The capture area has invalid screen bounds.");
                }

                if (!RawInputNative.ClipCursorRect(ref rectangle))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ClipCursor failed.");
                }

                if (!RawInputNative.SetCursorPos(x, y))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetCursorPos failed.");
                }

                _current = new CursorConfinementLease(original, originalClip);
                return _current;
            }
            catch
            {
                _ = RawInputNative.ClipCursorRect(ref originalClip);
                _ = RawInputNative.SetCursorPos(original.X, original.Y);
                throw;
            }
        }
    }

    public static void ForceRelease()
    {
        lock (StaticGate)
        {
            _current?.DisposeUnsafe();
            _current = null;
        }
    }

    public void Dispose()
    {
        lock (StaticGate)
        {
            DisposeUnsafe();
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
        }
    }

    private void DisposeUnsafe()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var originalClip = _originalClip;
        _ = RawInputNative.ClipCursorRect(ref originalClip);
        _ = RawInputNative.SetCursorPos(_originalPosition.X, _originalPosition.Y);
    }
}

