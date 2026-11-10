using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alexandra.Core;
using Alexandra.Infrastructure;

namespace Alexandra.App;

public sealed class Settings
{
    public string? ControllerId { get; set; }
    public int ControllerButton { get; set; }
    public int KeyboardKey { get; set; } = 123;
    public int Microphone { get; set; } = -1;
    public string Model { get; set; } = "gpt-5.4";
    public string Voice { get; set; } = VoiceCatalog.DefaultVoice;
    public bool AllowCloud { get; set; }
    public bool DiagnosticLoggingEnabled { get; set; }
    public bool ResearchBrowserEnabled { get; set; } = true;
    public bool ThinkingSoundEnabled { get; set; } = true;
    public bool MemoryEnabled { get; set; } = true;
    public bool AutoUpdateEnabled { get; set; } = true;
    public bool OpenWithElite { get; set; }
    public bool SetupCompleted { get; set; }
    public bool MinimalMode { get; set; } = true;
    public bool KeepOnTop { get; set; } = true;
    public double WindowScale { get; set; } = 1;
    public SavedWindowPosition? WindowPosition { get; set; }
    public string JournalDirectory { get; set; } = Path.Combine(
        WindowsKnownFolderPaths.TryGetSavedGamesPath() ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"),
        "Frontier Developments", "Elite Dangerous");
}
public sealed class SettingsStore
{
    private readonly string _directory;
    public Settings Value { get; private set; } = new();
    public SettingsStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Alexandra");
        var path = Path.Combine(_directory, "settings.json");
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                Value = JsonSerializer.Deserialize<Settings>(json) ?? new();
                using var document = JsonDocument.Parse(json);
                // Carry the previous overlay preference forward, without resurrecting it after a new explicit choice.
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    !document.RootElement.TryGetProperty(nameof(Settings.MinimalMode), out _) &&
                    document.RootElement.TryGetProperty("TransparentBackground", out var legacy) &&
                    legacy.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    Value.MinimalMode = legacy.GetBoolean();
            }
            catch (JsonException) { } // Do not erase an unreadable file until a deliberate save.
        }
    }
    public void Save()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    public string? ReadKey()
    {
        var path = Path.Combine(_directory, "openai.key");
        if (File.Exists(path))
        {
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)); }
            catch (CryptographicException) { throw new InvalidOperationException("The saved key could not be unlocked. Re-enter it in Setup."); }
        }
        return Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    }
    public void SaveKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return; // Blank means preserve, never silently delete.
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "openai.key");
        File.WriteAllBytes(path + ".tmp", ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
        File.Move(path + ".tmp", path, true);
    }
}

