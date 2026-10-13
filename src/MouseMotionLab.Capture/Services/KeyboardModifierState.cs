using System.Windows.Input;
using MouseMotionLab.Capture.Interop;

namespace MouseMotionLab.Capture.Services;

internal static class KeyboardModifierState
{
    // Sample modifiers on the native input thread at button arrival, before a
    // queued click can be delayed by session creation or database writes.
    internal static ModifierKeys Read()
    {
        var modifiers = ModifierKeys.None;
        if (IsDown(0x11)) modifiers |= ModifierKeys.Control;
        if (IsDown(0x12)) modifiers |= ModifierKeys.Alt;
        if (IsDown(0x10)) modifiers |= ModifierKeys.Shift;
        if (IsDown(0x5B) || IsDown(0x5C)) modifiers |= ModifierKeys.Windows;
        return modifiers;
    }

    private static bool IsDown(int virtualKey) => (RawInputNative.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
