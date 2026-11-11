using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bap.Companion.Core;

namespace Bap.Companion.Server;

internal sealed class WallpaperEngineSwitcher(InstalledCatalog catalog, ILogger<WallpaperEngineSwitcher> logger)
{
    public async Task<string?> GetCurrentAsync(int monitorIndex, CancellationToken cancellationToken)
    {
        if (monitorIndex is < 0 or > 31) throw new ArgumentOutOfRangeException(nameof(monitorIndex));
        var executable = FindExecutable() ?? throw new FileNotFoundException("Wallpaper Engine executable was not found.");
        return await ReadCurrentFromConfigAsync(Path.Combine(Path.GetDirectoryName(executable)!, "config.json"), monitorIndex, cancellationToken);
    }

    public async Task ApplyPropertiesAsync(IReadOnlyDictionary<string, JsonElement> properties, int monitorIndex,
        CancellationToken cancellationToken)
    {
        if (properties.Count == 0) return;
        if (monitorIndex is < 0 or > 31) throw new ArgumentOutOfRangeException(nameof(monitorIndex));
        var executable = FindExecutable() ?? throw new FileNotFoundException("Wallpaper Engine executable was not found.");
        var serialized = JsonSerializer.Serialize(properties);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = $"-control applyProperties -properties RAW~({serialized})~END -monitor {monitorIndex}"
        };
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));
        var version = FileVersionInfo.GetVersionInfo(executable).FileVersion ?? "unknown";
        logger.LogInformation("Wallpaper Engine applyProperties launch; version={Version}; monitor={MonitorIndex}; propertyKeys={PropertyKeys}; payloadSha256={PayloadHash}; callerPid={CallerPid}",
            version, monitorIndex, string.Join(',', properties.Keys.Order(StringComparer.Ordinal)), payloadHash, Environment.ProcessId);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not invoke Wallpaper Engine.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("Wallpaper Engine applyProperties did not respond within 8 seconds.");
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
        {
            var details = string.Join(Environment.NewLine, new[] { error.Trim(), output.Trim() }.Where(text => !string.IsNullOrWhiteSpace(text)));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(details)
                ? $"Wallpaper Engine control failed (exit code {process.ExitCode}); it returned no diagnostic output."
                : $"Wallpaper Engine control failed (exit code {process.ExitCode}):{Environment.NewLine}{details}");
        }
    }

    public async Task<WallpaperSwitchReceipt> OpenAsync(string hostKey, int monitorIndex, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hostKey) || hostKey.Length > 256)
            throw new ArgumentException("A valid Host catalog key is required.", nameof(hostKey));
        if (monitorIndex is < 0 or > 31)
            throw new ArgumentOutOfRangeException(nameof(monitorIndex), "Monitor index must be between 0 and 31.");

        var host = await catalog.GetAsync(hostKey, cancellationToken);
        if (host is null || host.Role != "host")
            throw new InvalidOperationException($"Unknown Host catalog key: {hostKey}");

        var projectFile = Path.Combine(host.ProjectDirectory, "project.json");
        if (!File.Exists(projectFile))
            throw new InvalidOperationException("The selected Host project has no project.json file.");
        var executable = FindExecutable() ?? throw new FileNotFoundException("Wallpaper Engine executable was not found.");

        await RunOpenCommandAsync(executable, projectFile, monitorIndex, cancellationToken);
        var expectedEntry = await ReadEntryFileAsync(projectFile, cancellationToken);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Task.Delay(400, cancellationToken);
            var current = await ReadCurrentFromConfigAsync(Path.Combine(Path.GetDirectoryName(executable)!, "config.json"), monitorIndex, cancellationToken);
            if (string.IsNullOrWhiteSpace(current)) continue;
            if (PathEquals(current, projectFile) || expectedEntry is not null && PathEquals(current, expectedEntry))
                return new("confirmed", projectFile, monitorIndex, current);
        }

        return new("command-accepted", projectFile, monitorIndex, null);
    }

    private static string? FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("BAP_WE_EXE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        foreach (var root in SteamWorkshopLocator.FindLocalProjectRoots(Environment.GetEnvironmentVariable("BAP_STEAM_ROOT")))
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

    private static async Task RunOpenCommandAsync(string executable, string projectFile, int monitorIndex, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-control", "openWallpaper", "-file", projectFile, "-monitor", monitorIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not invoke Wallpaper Engine.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Wallpaper Engine control did not respond within 8 seconds.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Wallpaper Engine openWallpaper failed with exit code {process.ExitCode}.");
    }

    private static async Task<string?> ReadEntryFileAsync(string projectFile, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(projectFile, cancellationToken));
            if (!document.RootElement.TryGetProperty("file", out var entry) || entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString()))
                return null;
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectFile)!, entry.GetString()!));
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or NotSupportedException) { return null; }
    }

    private static async Task<string?> ReadCurrentFromConfigAsync(string configFile, int monitorIndex, CancellationToken cancellationToken)
    {
        if (!File.Exists(configFile)) return null;
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(configFile, cancellationToken));
            foreach (var user in document.RootElement.EnumerateObject())
            {
                if (user.Value.ValueKind != JsonValueKind.Object ||
                    !user.Value.TryGetProperty("general", out var general) || general.ValueKind != JsonValueKind.Object ||
                    !general.TryGetProperty("wallpaperconfig", out var wallpaperConfig) ||
                    wallpaperConfig.ValueKind != JsonValueKind.Object ||
                    !wallpaperConfig.TryGetProperty("selectedwallpapers", out var selected) ||
                    selected.ValueKind != JsonValueKind.Object ||
                    !selected.TryGetProperty($"Monitor{monitorIndex}", out var wallpaper) ||
                    wallpaper.ValueKind != JsonValueKind.Object ||
                    !wallpaper.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.String)
                    continue;
                var value = file.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
            }
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or NotSupportedException) { }
        return null;
    }

    private static bool PathEquals(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return false; }
    }
}
