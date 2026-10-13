using System.IO;
using System.Runtime.InteropServices;
using MouseMotionLab.Core.Domain;

namespace MouseMotionLab.Capture.Interop;

public static class RawInputParser
{
    public static int HeaderSize { get; } = Marshal.SizeOf<RawInputNative.RawInputHeader>();

    public static int MousePayloadSize { get; } = Marshal.SizeOf<RawInputNative.RawMouse>();

    public static RawMouseEvent? ParseMouseInput(
        ReadOnlySpan<byte> bytes,
        long qpcTicks,
        Func<nint, string?> resolveDeviceId)
    {
        if (bytes.Length < HeaderSize + MousePayloadSize)
        {
            throw new InvalidDataException(
                $"Raw Input packet is too small ({bytes.Length} bytes; expected at least {HeaderSize + MousePayloadSize}).");
        }

        var header = MemoryMarshal.Read<RawInputNative.RawInputHeader>(bytes);
        if (header.Type != RawInputNative.RimTypeMouse)
        {
            return null;
        }

        if (header.Size > bytes.Length || header.Size < HeaderSize + MousePayloadSize)
        {
            throw new InvalidDataException($"Invalid Raw Input packet size: {header.Size}.");
        }

        var mouse = MemoryMarshal.Read<RawInputNative.RawMouse>(bytes[HeaderSize..]);
        var deviceId = resolveDeviceId(header.Device);
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            deviceId = $"HANDLE_{header.Device.ToInt64():X}";
        }

        var buttonFlags = (MouseButtonFlags)mouse.ButtonFlags;
        var wheelDelta = (buttonFlags & (MouseButtonFlags.Wheel | MouseButtonFlags.HorizontalWheel)) != 0
            ? unchecked((short)mouse.ButtonData)
            : (short)0;

        return new RawMouseEvent(
            qpcTicks,
            mouse.LastX,
            mouse.LastY,
            wheelDelta,
            buttonFlags,
            deviceId);
    }
}

