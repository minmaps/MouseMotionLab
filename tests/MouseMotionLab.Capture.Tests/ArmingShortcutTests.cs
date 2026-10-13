using System.Windows.Input;
using MouseMotionLab.Capture.Services;
using MouseMotionLab.Core.Domain;

namespace MouseMotionLab.Capture.Tests;

public sealed class ArmingShortcutTests
{
    [Theory]
    [InlineData(MouseButton.Left, MouseButtonFlags.LeftDown, MouseButtonFlags.LeftUp)]
    [InlineData(MouseButton.Right, MouseButtonFlags.RightDown, MouseButtonFlags.RightUp)]
    [InlineData(MouseButton.Middle, MouseButtonFlags.MiddleDown, MouseButtonFlags.MiddleUp)]
    [InlineData(MouseButton.XButton1, MouseButtonFlags.Button4Down, MouseButtonFlags.Button4Up)]
    [InlineData(MouseButton.XButton2, MouseButtonFlags.Button5Down, MouseButtonFlags.Button5Up)]
    public void MouseShortcutsMatchRawButtonPressesAndIgnoreReleases(
        MouseButton button, MouseButtonFlags down, MouseButtonFlags up)
    {
        var shortcut = new ArmingShortcut(Key.None, ModifierKeys.Control, button);

        Assert.True(shortcut.IsValid);
        Assert.True(shortcut.MatchesMouseDown(down, ModifierKeys.Control));
        Assert.True(shortcut.MatchesMouseDown(down | up, ModifierKeys.Control));
        Assert.False(shortcut.MatchesMouseDown(up, ModifierKeys.Control));
        Assert.False(shortcut.MatchesMouseDown(MouseButtonFlags.None, ModifierKeys.Control));
        Assert.False(shortcut.MatchesMouseDown(down, ModifierKeys.None));
        Assert.False(shortcut.Matches(Key.Space, ModifierKeys.Control));
        Assert.False(ArmingShortcut.Default.MatchesMouseDown(down, ModifierKeys.None));
    }

    [Fact]
    public void MouseShortcutPersistsAndOldKeyboardSettingsRemainCompatible()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mousemotion-shortcut-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "arming-shortcut.json");
        try
        {
            var store = new ArmingShortcutStore(path);
            var shortcut = new ArmingShortcut(Key.None, ModifierKeys.Shift, MouseButton.XButton2);
            store.Save(shortcut);
            Assert.Equal(shortcut, new ArmingShortcutStore(path).Load());
            Assert.Equal("Maj + Souris 5", store.Load().DisplayName);

            File.WriteAllText(path, "{\"TriggerKey\":\"F8\",\"Modifiers\":\"Control\"}");
            Assert.Equal(new ArmingShortcut(Key.F8, ModifierKeys.Control), store.Load());
            store.Save(ArmingShortcut.Default);
            Assert.Null(store.Load().TriggerMouseButton);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ShortcutCannotContainBothMouseAndKeyboardTriggers()
    {
        Assert.False(new ArmingShortcut(Key.Space, ModifierKeys.None, MouseButton.Left).IsValid);
        Assert.False(new ArmingShortcut(Key.None, ModifierKeys.None, (MouseButton)999).IsValid);
    }

    [Fact]
    public void ModifierCombinationsMatchExactly()
    {
        var shortcut = new ArmingShortcut(Key.F, ModifierKeys.Control | ModifierKeys.Shift);

        Assert.True(shortcut.IsValid);
        Assert.True(shortcut.Matches(Key.F, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.False(shortcut.Matches(Key.F, ModifierKeys.None));
        Assert.False(shortcut.Matches(Key.F, ModifierKeys.Control));
        Assert.False(shortcut.Matches(Key.G, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Equal("Ctrl + Maj + F", shortcut.DisplayName);
    }

    [Theory]
    [InlineData(Key.Escape, ModifierKeys.None)]
    [InlineData(Key.Back, ModifierKeys.None)]
    [InlineData(Key.F11, ModifierKeys.None)]
    [InlineData(Key.LeftCtrl, ModifierKeys.None)]
    [InlineData(Key.F4, ModifierKeys.Alt)]
    [InlineData(Key.F, ModifierKeys.Windows)]
    public void ReservedAndSystemShortcutsCannotReplaceArmingKey(Key key, ModifierKeys modifiers)
    {
        Assert.False(new ArmingShortcut(key, modifiers).IsValid);
    }

    [Fact]
    public void SavedShortcutSurvivesReloadAndCanBeResetToSpace()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mousemotion-shortcut-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "arming-shortcut.json");
        try
        {
            var store = new ArmingShortcutStore(path);
            Assert.Equal(ArmingShortcut.Default, store.Load());
            var shortcut = new ArmingShortcut(Key.F8, ModifierKeys.Alt | ModifierKeys.Control);
            store.Save(shortcut);
            Assert.Equal(shortcut, new ArmingShortcutStore(path).Load());
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));

            store.Save(ArmingShortcut.Default);
            Assert.Equal(ArmingShortcut.Default, new ArmingShortcutStore(path).Load());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("invalid json")]
    [InlineData("{}")]
    [InlineData("{\"TriggerKey\":\"F11\",\"Modifiers\":\"None\"}")]
    [InlineData("{\"TriggerKey\":\"UnknownKey\",\"Modifiers\":\"None\"}")]
    [InlineData("{\"TriggerKey\":100000,\"Modifiers\":0}")]
    public void CorruptOrReservedSavedShortcutFallsBackToSpace(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mousemotion-shortcut-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "arming-shortcut.json");
        try
        {
            File.WriteAllText(path, json);
            Assert.Equal(ArmingShortcut.Default, new ArmingShortcutStore(path).Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
