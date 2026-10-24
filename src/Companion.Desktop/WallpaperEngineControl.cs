using System.Diagnostics;
using System.Text.Json;
using Bap.Companion.Core;

namespace Bap.Companion.Desktop;

internal enum WallpaperSwitchResult { Confirmed, CommandAccepted }

internal static class WallpaperEngineControl
{
    public static string? FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("BAP_WE_EXE");
        if (File.Exists(configured)) return configured;
        var localRoots = SteamWorkshopLocator.FindLocalProjectRoots(Environment.GetEnvironmentVariable("BAP_STEAM_ROOT"));
        foreach (var root in localRoots)
        {
            var engine = Directory.GetParent(root)?.Parent?.FullName;
            if (engine is null) continue;
            foreach (var name in new[] { "wallpaper64.exe", "wallpaper32.exe" })
            {
                var candidate = Path.Combine(engine, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public static async Task<string> GetCurrentAsync(int monitor)
    {
        var exe = FindExecutable() ?? throw new FileNotFoundException("Wallpaper Engine executable was not found");
        var output = await RunAsync(exe, ["-control", "getWallpaper", "-monitor", monitor.ToString()], captureOutput: true);
        return output.Trim().Trim('"');
    }

    public static async Task<WallpaperSwitchResult> OpenAsync(InstalledItem host, int monitor)
    {
        var project = Path.Combine(host.ProjectDirectory, "project.json");
        if (host.Role != "host" || !File.Exists(project)) throw new InvalidOperationException("Select an installed Web wallpaper project");
        var exe = FindExecutable() ?? throw new FileNotFoundException("Wallpaper Engine executable was not found");
        await RunAsync(exe, ["-control", "openWallpaper", "-file", project, "-monitor", monitor.ToString()], captureOutput: false);
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(project));
        var entry = metadata.RootElement.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.String
            ? Path.GetFullPath(Path.Combine(host.ProjectDirectory, file.GetString()!)) : null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Task.Delay(400);
            string current;
            try { current = await GetCurrentAsync(monitor); }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException) { return WallpaperSwitchResult.CommandAccepted; }
            if (string.IsNullOrWhiteSpace(current)) return WallpaperSwitchResult.CommandAccepted;
            string currentPath;
            try { currentPath = Path.GetFullPath(current); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException) { return WallpaperSwitchResult.CommandAccepted; }
            if (string.Equals(currentPath, Path.GetFullPath(project), StringComparison.OrdinalIgnoreCase)
                || entry is not null && string.Equals(currentPath, entry, StringComparison.OrdinalIgnoreCase)) return WallpaperSwitchResult.Confirmed;
        }
        return WallpaperSwitchResult.CommandAccepted;
    }

    private static async Task<string> RunAsync(string exe, IReadOnlyList<string> arguments, bool captureOutput)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = captureOutput, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not invoke Wallpaper Engine");
        var output = captureOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { throw new TimeoutException("Wallpaper Engine control did not respond"); }
        if (process.ExitCode != 0) throw new InvalidOperationException($"Wallpaper Engine control failed: {await error}");
        return await output;
    }
}
