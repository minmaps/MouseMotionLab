using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MouseMotionLab.Capture.Services;

public sealed class ArmingShortcutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly string _filePath;

    public ArmingShortcutStore(string? filePath = null)
    {
        _filePath = Path.GetFullPath(filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MouseMotionLab", "arming-shortcut.json"));
    }

    public ArmingShortcut Load()
    {
        try
        {
            var shortcut = JsonSerializer.Deserialize<ArmingShortcut>(File.ReadAllText(_filePath), JsonOptions);
            return shortcut is { IsValid: true } ? shortcut : ArmingShortcut.Default;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ArmingShortcut.Default;
        }
    }

    public void Save(ArmingShortcut shortcut)
    {
        if (!shortcut.IsValid)
        {
            throw new ArgumentException("La touche d’armement est invalide ou réservée.", nameof(shortcut));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(shortcut, JsonOptions));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
