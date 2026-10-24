using Microsoft.Win32;

namespace Bap.Companion.Desktop;

internal static class DesktopSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "BAP Companion";

    public static bool AutoStartEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppName) is string command && command.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true)
                ?? throw new InvalidOperationException("Cannot open the current user's startup settings");
            if (value) key.SetValue(AppName, $"\"{Environment.ProcessPath}\" --background", RegistryValueKind.String);
            else key.DeleteValue(AppName, false);
        }
    }
}
