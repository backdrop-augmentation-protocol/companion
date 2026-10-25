using System.Text.Json;
using Microsoft.Win32;

namespace Bap.Companion.Desktop;

internal sealed record DesktopPreferences(string Language = "en", bool AutoStart = true, int DisplayIndex = 0);

internal sealed class DesktopSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "BAP Companion";
    private readonly string path;
    private DesktopPreferences preferences;

    public string Language => preferences.Language;
    public bool AutoStartEnabled => preferences.AutoStart;
    public int DisplayIndex => preferences.DisplayIndex;
    public string? StartupError { get; private set; }
    public event EventHandler? LanguageChanged;

    public DesktopSettings(string? path = null, bool synchronizeStartup = true)
    {
        this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BAP Companion", "data", "desktop.json");
        preferences = ReadPreferences(this.path);
        if (synchronizeStartup)
        {
            try { ApplyAutoStart(preferences.AutoStart); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { StartupError = error.Message; }
        }
        if (!File.Exists(this.path)) Save(preferences);
    }

    public void SetLanguage(string language)
    {
        if (language is not ("en" or "zh-CN")) throw new ArgumentException("Unsupported language", nameof(language));
        if (preferences.Language == language) return;
        var next = preferences with { Language = language };
        Save(next);
        preferences = next;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetAutoStart(bool enabled)
    {
        ApplyAutoStart(enabled);
        var next = preferences with { AutoStart = enabled };
        Save(next);
        preferences = next;
        StartupError = null;
    }

    public void SetDisplayIndex(int displayIndex)
    {
        if (displayIndex < 0) throw new ArgumentOutOfRangeException(nameof(displayIndex));
        if (preferences.DisplayIndex == displayIndex) return;
        var next = preferences with { DisplayIndex = displayIndex };
        Save(next);
        preferences = next;
    }

    internal static DesktopPreferences ReadPreferences(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            var value = JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(path));
            return value is null ? new() : value with
            {
                Language = value.Language == "zh-CN" ? "zh-CN" : "en",
                DisplayIndex = Math.Max(0, value.DisplayIndex)
            };
        }
        catch (JsonException) { return new(); }
    }

    private void Save(DesktopPreferences value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    private static void ApplyAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true)
            ?? throw new InvalidOperationException("Cannot open current-user startup settings");
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the Companion executable path");
            key.SetValue(AppName, $"\"{executable}\" --background", RegistryValueKind.String);
        }
        else key.DeleteValue(AppName, false);
    }
}
