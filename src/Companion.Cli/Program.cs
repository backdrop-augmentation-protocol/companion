using System.Diagnostics;
using System.Text.Json;
using Bap.Companion.Core;
using Bap.Companion.Desktop;

const int UsageError = 2;
const int ServiceError = 3;
const int CommandError = 4;
var json = args.Contains("--json", StringComparer.Ordinal);
var command = args.Where(argument => argument != "--json").ToArray();
var output = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

if (command.Length == 0 || command is ["--help"] or ["-h"] or ["help"])
{
    PrintHelp();
    return 0;
}

try
{
    var controlPipe = Environment.GetEnvironmentVariable("BAPC_CONTROL_PIPE") ?? CompanionControlChannel.PipeName;
    var client = new HostAuthorizationClient(controlPipe);
    if (command is ["service", "status"])
    {
        var status = await client.GetServiceStatusAsync();
        Print(status, json, output);
        if (!json) Console.WriteLine($"Companion service is running (PID {status.ProcessId}, version {status.Version}, started {status.StartedAt:O}).");
        return 0;
    }
    if (command is ["service", "start"])
    {
        await EnsureServiceAsync(client);
        Print(await client.GetServiceStatusAsync(), json, output);
        return 0;
    }
    if (command is ["service", "stop"])
    {
        await client.StopServiceAsync();
        if (json) Print(new { stopped = true }, true, output);
        else Console.WriteLine("Companion service stop requested.");
        return 0;
    }

    await EnsureServiceAsync(client);
    if (command[0] == "authorization")
        return await RunAuthorizationCommandAsync(command, client, json, output);

    switch (command)
    {
        case ["catalog", "list"]:
            var items = await client.GetCatalogAsync();
            Print(items, json, output);
            if (!json)
                foreach (var item in items)
                    Console.WriteLine($"{item.Key,-24} {item.Role,-9} {item.SourceKind,-8} {item.Status,-10} {item.Title}");
            return 0;
        case ["catalog", "refresh"]:
            await client.RefreshCatalogAsync();
            Console.WriteLine("Installed wallpaper catalog refreshed.");
            return 0;
        case ["host", "list"]:
            var hosts = (await client.GetCatalogAsync()).Where(item => item.Role == "host").ToArray();
            Print(hosts, json, output);
            if (!json)
                foreach (var host in hosts)
                    Console.WriteLine($"{host.Key,-24} {host.SourceKind,-8} {host.Status,-10} {host.Title} [{host.BapId ?? "unverified"}]");
            return 0;
        case ["host", "switch", var hostKey, ..]:
            var monitorIndex = ParseMonitorIndex(command.Skip(3).ToArray());
            var switchResult = await client.SwitchWallpaperAsync(hostKey, monitorIndex);
            Print(switchResult, json, output);
            if (!json)
                Console.WriteLine(switchResult.Status == "confirmed"
                    ? $"Wallpaper Engine is displaying {hostKey} on monitor {monitorIndex}."
                    : $"Wallpaper Engine accepted the switch command for {hostKey} on monitor {monitorIndex}; current selection could not be confirmed.");
            return 0;
        case ["host", "composition", var hostKey] when !string.IsNullOrWhiteSpace(hostKey):
            var composition = await client.GetHostCompositionAsync(hostKey);
            Print(composition, json, output);
            if (!json)
            {
                Console.WriteLine($"Preset: {composition.PresetId} (revision {composition.Revision}) — {composition.Instances.Count} component instance(s)");
                foreach (var instance in composition.Instances)
                    Console.WriteLine($"{instance.InstanceId,-24} {instance.ComponentId,-40} {instance.Status,-10} {(instance.Enabled ? "enabled" : "disabled")}");
            }
            return 0;
        case ["desktop", "status"]:
            var runtime = await client.GetRuntimeSnapshotsAsync();
            Print(runtime, json, output);
            if (!json && runtime.Count == 0) Console.WriteLine("No Host has reported a live runtime snapshot.");
            if (!json)
                foreach (var host in runtime)
                    Console.WriteLine($"{host.HostName} ({host.HostId}) — {host.Instances.Count} component instance(s), updated {host.UpdatedAt:O}");
            return 0;
        case ["desktop", "set-config", var sessionId, var instanceId, var revisionText, var configText]:
            if (!long.TryParse(revisionText, out var revision) || revision < 1)
            {
                Console.Error.WriteLine("Config revision must be a positive integer.");
                return UsageError;
            }
            var config = ParseConfig(configText);
            await client.SetRuntimeConfigAsync(sessionId, instanceId, config, revision);
            Print(new { queued = true, sessionId, instanceId, revision }, json, output);
            if (!json) Console.WriteLine($"Runtime config revision {revision} queued for component instance {instanceId}; verify the Host-reported snapshot to confirm application.");
            return 0;
        case ["desktop", "set-layout", var layoutSessionId, var layoutInstanceId, var layoutText]:
            var layout = JsonSerializer.Deserialize<ComponentLayout>(layoutText, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new JsonException("Layout JSON must be an object.");
            await client.SetRuntimeLayoutAsync(layoutSessionId, layoutInstanceId, layout);
            Print(new { queued = true, sessionId = layoutSessionId, instanceId = layoutInstanceId, layout }, json, output);
            if (!json) Console.WriteLine($"Runtime layout queued for component instance {layoutInstanceId}; verify the Host-reported snapshot to confirm application.");
            return 0;
        case ["preset", "list"]:
            var presets = await client.GetPresetsAsync();
            Print(presets, json, output);
            if (!json && presets.Presets.Count == 0) Console.WriteLine("No presets are configured.");
            if (!json)
                foreach (var preset in presets.Presets)
                    Console.WriteLine($"{preset.Id,-24} {preset.Name} — {preset.Components.Count} component instance(s)");
            return 0;
        case ["preset", "component", "set-enabled", var presetId, var instanceId, var enabledText]:
            if (!bool.TryParse(enabledText, out var enabled))
            {
                Console.Error.WriteLine("Enabled must be 'true' or 'false'.");
                return UsageError;
            }
            var updatedPresets = await client.SetPresetComponentEnabledAsync(presetId, instanceId, enabled);
            var updatedInstance = updatedPresets.Presets.First(preset => preset.Id == presetId)
                .Components.First(component => component.InstanceId == instanceId);
            Print(new { presetId, instanceId, enabled = updatedInstance.Enabled }, json, output);
            if (!json) Console.WriteLine($"Component instance {instanceId} in preset {presetId} is {(updatedInstance.Enabled ? "enabled" : "disabled")}.");
            return 0;
        default:
            PrintHelp();
            return UsageError;
    }
}
catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or InvalidDataException)
{
    Console.Error.WriteLine($"bapc: {error.Message}");
    return ServiceError;
}
catch (JsonException error)
{
    Console.Error.WriteLine($"bapc: invalid JSON argument: {error.Message}");
    return UsageError;
}
catch (ArgumentException error)
{
    Console.Error.WriteLine($"bapc: {error.Message}");
    return UsageError;
}
catch (Exception error)
{
    Console.Error.WriteLine($"bapc: command failed: {error.Message}");
    return CommandError;
}

static async Task EnsureServiceAsync(HostAuthorizationClient client)
{
    try
    {
        await client.GetServiceStatusAsync();
        return;
    }
    catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException) { }

    var path = Path.Combine(AppContext.BaseDirectory, "server", "Bap.Companion.Server.exe");
    if (!File.Exists(path))
        throw new FileNotFoundException("Companion service is not running and the packaged server executable was not found.", path);
    var start = new ProcessStartInfo(path)
    {
        WorkingDirectory = Path.GetDirectoryName(path)!,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    start.ArgumentList.Add("--urls");
    start.ArgumentList.Add("http://127.0.0.1:4311");
    _ = Process.Start(start) ?? throw new InvalidOperationException("Could not start Companion service.");

    var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
    Exception? lastError = null;
    while (DateTimeOffset.UtcNow < deadline)
    {
        try
        {
            await Task.Delay(150);
            await client.GetServiceStatusAsync();
            return;
        }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException)
        {
            lastError = error;
        }
    }
    throw new IOException("Companion service did not become ready within 8 seconds.", lastError);
}

static void Print<T>(T value, bool json, JsonSerializerOptions options)
{
    if (json) Console.WriteLine(JsonSerializer.Serialize(value, options));
}

static void PrintHelp()
{
    Console.WriteLine("BAP Companion CLI (bapc)");
    Console.WriteLine();
    Console.WriteLine("Usage: bapc <command> [--json]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  service status    Show the shared Companion service status");
    Console.WriteLine("  service start     Start the service if it is not already running");
    Console.WriteLine("  service stop      Stop the shared service; active wallpaper sessions will disconnect");
    Console.WriteLine("  catalog list      List installed Wallpaper Engine projects");
    Console.WriteLine("  catalog refresh   Refresh the installed project catalog");
    Console.WriteLine("  host list         List installed Host projects");
    Console.WriteLine("  host switch <key> [--monitor N]  Switch Wallpaper Engine to an installed Host wallpaper");
    Console.WriteLine("  host composition <key>  Show the effective composition for an installed Host catalog key");
    Console.WriteLine("  desktop status    Show live Host and component snapshots");
    Console.WriteLine("  desktop set-config <session> <instance> <revision> <json-object>  Queue runtime config");
    Console.WriteLine("  desktop set-layout <session> <instance> <layout-json>  Queue runtime layout");
    Console.WriteLine("  preset list       List configured presets");
    Console.WriteLine("  preset component set-enabled <preset-id> <instance-id> <true|false>  Enable or disable one preset component instance");
    Console.WriteLine("  authorization pending  List Host authorization requests awaiting a decision");
    Console.WriteLine("  authorization approve <session> --permission <name> [--permission <name>] [--remember]");
    Console.WriteLine("  authorization deny <session>  Deny a pending Host request");
    Console.WriteLine("  authorization trusted  List remembered Host approvals");
    Console.WriteLine("  authorization revoke <host-id>  Revoke remembered trust and active sessions");
    Console.WriteLine();
    Console.WriteLine("Use --json for machine-readable output. Data commands start the service when needed.");
}

static async Task<int> RunAuthorizationCommandAsync(string[] command, HostAuthorizationClient client,
    bool json, JsonSerializerOptions output)
{
    if (command.Length == 2 && command[1] == "pending")
    {
        var requests = await client.GetPendingAsync();
        Print(requests, json, output);
        if (!json)
        {
            if (requests.Count == 0) Console.WriteLine("No Host authorization requests are pending.");
            foreach (var request in requests)
                Console.WriteLine($"{request.SessionId}  {request.ClaimedIdentity.Name} [{request.ClaimedIdentity.Id}]  requested: {string.Join(", ", request.RequestedPermissions)}");
        }
        return 0;
    }
    if (command.Length == 2 && command[1] == "trusted")
    {
        var trusted = await client.GetTrustedHostsAsync();
        Print(trusted, json, output);
        if (!json)
        {
            if (trusted.Count == 0) Console.WriteLine("No remembered Host approvals.");
            foreach (var host in trusted)
                Console.WriteLine($"{host.Id,-40} {host.Name} — {string.Join(", ", host.Permissions)}");
        }
        return 0;
    }
    if (command.Length == 3 && command[1] == "revoke")
    {
        var success = await client.RevokeTrustAsync(command[2]);
        Print(new { success, hostId = command[2] }, json, output);
        if (!success) Console.Error.WriteLine("No remembered approval or active session was found for that Host ID.");
        else if (!json) Console.WriteLine($"Revoked remembered trust and active sessions for {command[2]}.");
        return success ? 0 : CommandError;
    }
    if (command.Length >= 3 && command[1] is "approve" or "deny")
    {
        var sessionId = command[2];
        var permissions = new List<string>();
        var remember = false;
        for (var index = 3; index < command.Length; index++)
        {
            if (command[index] == "--remember") { remember = true; continue; }
            if (command[index] == "--permission" && index + 1 < command.Length)
            {
                permissions.Add(command[++index]);
                continue;
            }
            if (command[index].StartsWith("--permission=", StringComparison.Ordinal))
            {
                permissions.Add(command[index]["--permission=".Length..]);
                continue;
            }
            Console.Error.WriteLine($"Unknown or incomplete authorization option: {command[index]}");
            return UsageError;
        }
        var approve = command[1] == "approve";
        if (approve && permissions.Count == 0)
        {
            Console.Error.WriteLine("Approval requires at least one explicit --permission value.");
            return UsageError;
        }
        if (approve)
        {
            var pending = (await client.GetPendingAsync()).FirstOrDefault(request => request.SessionId == sessionId);
            if (pending is null)
            {
                Console.Error.WriteLine("That authorization request is no longer pending.");
                return CommandError;
            }
            var requested = pending.RequestedPermissions.ToHashSet(StringComparer.Ordinal);
            var unsupported = permissions.Distinct(StringComparer.Ordinal)
                .Where(permission => permission is not ("component.discovery" or "config.read" or "config.write") || !requested.Contains(permission))
                .ToArray();
            if (unsupported.Length > 0)
            {
                Console.Error.WriteLine($"Permission was not requested or is unsupported: {string.Join(", ", unsupported)}");
                return UsageError;
            }
        }
        var success = await client.DecideAsync(sessionId, approve, permissions.Distinct(StringComparer.Ordinal).ToArray(), remember);
        Print(new { success, sessionId, approved = approve, permissions = approve ? permissions.ToArray() : Array.Empty<string>(), remembered = approve && remember }, json, output);
        if (!success) Console.Error.WriteLine("That authorization request is no longer pending.");
        else if (!json) Console.WriteLine(approve ? $"Approved {sessionId} for {string.Join(", ", permissions)}." : $"Denied {sessionId}.");
        return success ? 0 : CommandError;
    }

    Console.Error.WriteLine("Invalid authorization command. Run 'bapc --help' for usage.");
    return UsageError;
}

static Dictionary<string, JsonElement> ParseConfig(string json)
{
    if (json.Length > 32 * 1024) throw new JsonException("Config JSON exceeds the 32 KiB command limit.");
    using var document = JsonDocument.Parse(json);
    if (document.RootElement.ValueKind != JsonValueKind.Object)
        throw new JsonException("Config JSON must be an object.");
    var config = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    foreach (var property in document.RootElement.EnumerateObject())
    {
        if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 128)
            throw new JsonException("Config keys must contain between 1 and 128 characters.");
        config[property.Name] = property.Value.Clone();
    }
    return config;
}

static int ParseMonitorIndex(string[] options)
{
    var monitor = 0;
    for (var index = 0; index < options.Length; index++)
    {
        string? value = null;
        if (options[index] == "--monitor" && index + 1 < options.Length) value = options[++index];
        else if (options[index].StartsWith("--monitor=", StringComparison.Ordinal)) value = options[index]["--monitor=".Length..];
        if (value is not null && int.TryParse(value, out monitor) && monitor is >= 0 and <= 31) continue;
        throw new ArgumentException($"Invalid host switch option: {options[index]}. Expected --monitor N (0-31).");
    }
    return monitor;
}
