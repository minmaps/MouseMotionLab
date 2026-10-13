using System.Text.Json.Serialization;
using System.Windows.Input;
using MouseMotionLab.Core.Domain;

namespace MouseMotionLab.Capture.Services;

public sealed record ArmingShortcut(Key TriggerKey, ModifierKeys Modifiers, MouseButton? TriggerMouseButton = null)
{
    public static ArmingShortcut Default { get; } = new(Key.Space, ModifierKeys.None);

    [JsonIgnore]
    public bool IsValid =>
        (Modifiers & ~(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) == 0 &&
        (TriggerMouseButton is { } mouseButton
            ? Enum.IsDefined(mouseButton) && TriggerKey == Key.None
            : Enum.IsDefined(TriggerKey) &&
                TriggerKey is not (Key.None or Key.Escape or Key.Back or Key.F11 or
                    Key.System or Key.ImeProcessed or Key.DeadCharProcessed or Key.Cancel or Key.Sleep) &&
                !IsModifierKey(TriggerKey) &&
                !(TriggerKey == Key.F4 && Modifiers.HasFlag(ModifierKeys.Alt)));

    public bool Matches(Key key, ModifierKeys modifiers) =>
        TriggerMouseButton is null && TriggerKey == key && Modifiers == modifiers;

    public bool MatchesMouseDown(MouseButtonFlags flags, ModifierKeys modifiers)
    {
        var downFlag = TriggerMouseButton switch
        {
            MouseButton.Left => MouseButtonFlags.LeftDown,
            MouseButton.Right => MouseButtonFlags.RightDown,
            MouseButton.Middle => MouseButtonFlags.MiddleDown,
            MouseButton.XButton1 => MouseButtonFlags.Button4Down,
            MouseButton.XButton2 => MouseButtonFlags.Button5Down,
            _ => MouseButtonFlags.None
        };
        return downFlag != MouseButtonFlags.None && (flags & downFlag) != 0 && Modifiers == modifiers;
    }

    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Maj");
            parts.Add(TriggerMouseButton is { } mouseButton ? mouseButton switch
            {
                MouseButton.Left => "Clic gauche",
                MouseButton.Right => "Clic droit",
                MouseButton.Middle => "Clic molette",
                MouseButton.XButton1 => "Souris 4",
                MouseButton.XButton2 => "Souris 5",
                _ => mouseButton.ToString()
            } : TriggerKey switch
            {
                Key.Space => "Espace",
                Key.Return => "Entrée",
                Key.Delete => "Suppr",
                Key.Home => "Début",
                Key.End => "Fin",
                Key.Prior => "Page ↑",
                Key.Next => "Page ↓",
                Key.Up => "↑",
                Key.Down => "↓",
                Key.Left => "←",
                Key.Right => "→",
                >= Key.D0 and <= Key.D9 => ((int)TriggerKey - (int)Key.D0).ToString(),
                >= Key.NumPad0 and <= Key.NumPad9 => $"Pavé {(int)TriggerKey - (int)Key.NumPad0}",
                _ => TriggerKey.ToString()
            });
            return string.Join(" + ", parts);
        }
    }
}
