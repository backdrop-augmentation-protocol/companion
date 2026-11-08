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
        var current = output.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(current)) return current;

        // Some WE builds accept getWallpaper but do not forward its result to a
        // redirected stdout stream. The selected wallpaper is also persisted in
        // the user's config.json, so use that as the read-only fallback.
        return await ReadCurrentFromConfigAsync(Path.Combine(Path.GetDirectoryName(exe)!, "config.json"), monitor);
    }

    internal static async Task<string> ReadCurrentFromConfigAsync(string configPath, int monitor, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configPath)) return "";
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(configPath, cancellationToken));
            foreach (var user in document.RootElement.EnumerateObject())
            {
                if (user.Value.ValueKind != JsonValueKind.Object ||
                    !user.Value.TryGetProperty("general", out var general) || general.ValueKind != JsonValueKind.Object ||
                    !general.TryGetProperty("wallpaperconfig", out var wallpaperConfig) ||
                    wallpaperConfig.ValueKind != JsonValueKind.Object ||
                    !wallpaperConfig.TryGetProperty("selectedwallpapers", out var selected)) continue;

                var monitorKey = $"Monitor{monitor}";
                if (selected.ValueKind != JsonValueKind.Object || !selected.TryGetProperty(monitorKey, out var wallpaper) ||
                    wallpaper.ValueKind != JsonValueKind.Object ||
                    !wallpaper.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.String) return "";
                var path = file.GetString();
                return string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { }
        return "";
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

    public static async Task ApplyPropertiesAsync(IReadOnlyDictionary<string, System.Text.Json.JsonElement> properties, int monitor, CompanionLog log)
    {
        if (properties.Count == 0) return;
        var exe = FindExecutable() ?? throw new FileNotFoundException("Wallpaper Engine executable was not found");
        var json = System.Text.Json.JsonSerializer.Serialize(properties);
        var rawArguments = BuildApplyPropertiesCommandLine(json, monitor);
        var version = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "unknown";
        var payloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
        log.Write("we-control", $"applyProperties launch; exe={Path.GetFullPath(exe)}; version={version}; monitor={monitor}; args=[-control,applyProperties,-properties,RAW~(<redacted-json>)~END,-monitor,{monitor}]; argumentTransport=raw-command-line; properties=[{DescribeProperties(properties)}]; payloadSha256={payloadHash}; callerPid={Environment.ProcessId}; caller64Bit={Environment.Is64BitProcess}; workingDirectory=(inherited); shellExecute=false; createNoWindow=true; redirectStdout=true; redirectStderr=true");
        await RunAsync(exe, [], captureOutput: false, log, "applyProperties", rawArguments);
    }

    internal static string BuildApplyPropertiesCommandLine(string json, int monitor) =>
        $"-control applyProperties -properties RAW~({json})~END -monitor {monitor}";

    internal static string DescribeProperties(IReadOnlyDictionary<string, System.Text.Json.JsonElement> properties) =>
        string.Join(",", properties.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}:{pair.Value.ValueKind}"));

    private static async Task<string> RunAsync(string exe, IReadOnlyList<string> arguments, bool captureOutput, CompanionLog? diagnosticLog = null, string? diagnosticCommand = null, string? rawArguments = null)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (rawArguments is not null) start.Arguments = rawArguments;
        else foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not invoke Wallpaper Engine");
        diagnosticLog?.Write("we-control", $"{diagnosticCommand ?? "control"} started; pid={process.Id}; session={process.SessionId}");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            diagnosticLog?.Write("we-control", $"{diagnosticCommand ?? "control"} timed out; pid={process.Id}; timeoutSeconds=8");
            throw new TimeoutException("Wallpaper Engine control did not respond");
        }
        var outputText = await output;
        var errorText = await error;
        diagnosticLog?.Write("we-control", $"{diagnosticCommand ?? "control"} exited; pid={process.Id}; exitCode={process.ExitCode}; stdoutLength={outputText.Length}; stderrLength={errorText.Length}");
        if (process.ExitCode != 0) throw new InvalidOperationException(FormatControlFailure(process.ExitCode, errorText, outputText));
        return captureOutput ? outputText : "";
    }

    internal static string FormatControlFailure(int exitCode, string standardError, string standardOutput)
    {
        var details = string.Join(Environment.NewLine, new[] { standardError.Trim(), standardOutput.Trim() }
            .Where(text => !string.IsNullOrWhiteSpace(text)));
        return string.IsNullOrWhiteSpace(details)
            ? $"Wallpaper Engine control failed (exit code {exitCode}); it returned no diagnostic output."
            : $"Wallpaper Engine control failed (exit code {exitCode}):{Environment.NewLine}{details}";
    }
}
