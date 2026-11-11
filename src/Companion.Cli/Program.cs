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

    if (command[0] == "settings")
        return await RunSettingsCommandAsync(command, client, json, output);
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
            if (json) Print(new { refreshed = true }, true, output);
            else Console.WriteLine("Installed wallpaper catalog refreshed.");
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
        case ["desktop", "refresh"]:
            await client.RefreshCatalogAsync();
            var refreshedRuntime = await client.GetRuntimeSnapshotsAsync();
            Print(new { refreshed = true, runtimeHosts = refreshedRuntime }, json, output);
            if (!json) Console.WriteLine($"Desktop catalog refreshed; {refreshedRuntime.Count} Host runtime snapshot(s) available.");
            return 0;
        case ["desktop", "component", "set-property", var propertySessionId, var propertyInstanceId, var propertyKey, var propertyValue]:
            var snapshots = await client.GetRuntimeSnapshotsAsync();
            var selectedRuntimeHost = snapshots.FirstOrDefault(host => host.SessionId == propertySessionId)
                ?? throw new InvalidOperationException($"Runtime Host session was not found: {propertySessionId}");
            var selectedInstance = selectedRuntimeHost.Instances.FirstOrDefault(instance => instance.InstanceId == propertyInstanceId)
                ?? throw new InvalidOperationException($"Runtime component instance was not found: {propertyInstanceId}");
            var component = (await client.GetCatalogAsync()).FirstOrDefault(item => item.Role == "component" && item.BapId == selectedInstance.ComponentId)
                ?? throw new InvalidOperationException($"Installed component was not found: {selectedInstance.ComponentId}");
            var propertyDefinition = component.Properties?.FirstOrDefault(property => property.Key == propertyKey)
                ?? throw new InvalidOperationException($"Component property was not found: {propertyKey}");
            using (var parsedValue = JsonDocument.Parse(propertyValue))
            {
                if (!WallpaperPropertyReader.IsValidValue(propertyDefinition, parsedValue.RootElement))
                    throw new ArgumentException($"Value does not match the declared component property '{propertyKey}'.");
                var mergedConfig = new Dictionary<string, JsonElement>(selectedInstance.Config, StringComparer.Ordinal)
                {
                    [propertyKey] = parsedValue.RootElement.Clone()
                };
                var nextRevision = checked(selectedInstance.ConfigRevision + 1);
                await client.SetRuntimeConfigAsync(propertySessionId, propertyInstanceId, mergedConfig, nextRevision);
                Print(new { queued = true, sessionId = propertySessionId, instanceId = propertyInstanceId, propertyKey, revision = nextRevision }, json, output);
                if (!json) Console.WriteLine($"Runtime property {propertyKey} queued for component instance {propertyInstanceId}.");
            }
            return 0;
        case ["desktop", "host", "set-property", var hostPropertyKey, var hostPropertyValue]:
            var desktopSettings = CreateDesktopSettings();
            var displayIndex = desktopSettings.DisplayIndex;
            using (var parsedHostValue = JsonDocument.Parse(hostPropertyValue))
            {
                var receipt = await client.SetHostPropertyAsync(null, displayIndex, hostPropertyKey, parsedHostValue.RootElement);
                Print(new { applied = true, hostKey = receipt.HostKey, hostTitle = receipt.HostTitle, propertyKey = receipt.PropertyKey, displayIndex = receipt.MonitorIndex }, json, output);
                if (!json) Console.WriteLine($"Applied Host property {receipt.PropertyKey} to {receipt.HostTitle} on display {receipt.MonitorIndex}.");
            }
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
                for (var index = 0; index < presets.Presets.Count; index++)
                {
                    var preset = presets.Presets[index];
                    var scope = presets.GlobalPresetId == preset.Id ? "all Hosts" : string.Join(", ", presets.HostPresetIds
                        .Where(binding => binding.Value.Contains(preset.Id, StringComparer.Ordinal)).Select(binding => binding.Key));
                    if (string.IsNullOrEmpty(scope)) scope = "no Hosts";
                    Console.WriteLine($"{index,2}. [{(preset.Enabled ? "enabled" : "disabled")}] {preset.Name} ({preset.Id}) - {preset.Components.Count} component instance(s); {scope}");
                }
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
        case ["preset", "create", var createId, ..] when command.Length > 3:
            var created = await client.CreatePresetAsync(createId, string.Join(' ', command.Skip(3)));
            Print(created, json, output);
            if (!json) Console.WriteLine($"Created preset {createId}.");
            return 0;
        case ["preset", "delete", var deleteId]:
            var deleted = await client.DeletePresetAsync(deleteId);
            Print(deleted, json, output);
            if (!json) Console.WriteLine($"Deleted preset {deleteId}.");
            return 0;
        case ["preset", "rename", var renamePresetId, ..] when command.Length > 3:
            var renamed = await client.RenamePresetAsync(renamePresetId, string.Join(' ', command.Skip(3)));
            Print(renamed, json, output);
            if (!json) Console.WriteLine($"Renamed preset {renamePresetId}.");
            return 0;
        case ["preset", "set-enabled", var enabledPresetId, var presetEnabledText]:
            if (!bool.TryParse(presetEnabledText, out var presetEnabled))
            {
                Console.Error.WriteLine("Enabled must be 'true' or 'false'.");
                return UsageError;
            }
            var enabledState = await client.SetPresetEnabledAsync(enabledPresetId, presetEnabled);
            Print(enabledState, json, output);
            if (!json) Console.WriteLine($"Preset {enabledPresetId} is {(presetEnabled ? "enabled" : "disabled")}.");
            return 0;
        case ["preset", "move", var movePresetId, var presetDestinationText]:
            if (!int.TryParse(presetDestinationText, out var presetDestination) || presetDestination < 0)
            {
                Console.Error.WriteLine("Destination index must be a nonnegative integer (zero-based).");
                return UsageError;
            }
            var movedPreset = await client.MovePresetAsync(movePresetId, presetDestination);
            Print(movedPreset, json, output);
            if (!json) Console.WriteLine($"Moved preset {movePresetId} to priority index {presetDestination}.");
            return 0;
        case ["preset", "host", "add", var hostPresetId, var hostKey]:
            var withHost = await client.AddPresetHostAsync(hostPresetId, hostKey);
            Print(withHost, json, output);
            if (!json) Console.WriteLine($"Added Host {hostKey} to preset {hostPresetId}.");
            return 0;
        case ["preset", "host", "add", var unverifiedPresetId, var unverifiedHostKey, "--allow-unverified"]:
            var withUnverifiedHost = await client.AddPresetHostAsync(unverifiedPresetId, unverifiedHostKey, allowUnverified: true);
            Print(withUnverifiedHost, json, output);
            if (!json) Console.WriteLine($"Added Host {unverifiedHostKey} to preset {unverifiedPresetId}.");
            return 0;
        case ["preset", "host", "remove", var unbindPresetId, var unbindHostKey]:
            var withoutHost = await client.RemovePresetHostAsync(unbindPresetId, unbindHostKey);
            Print(withoutHost, json, output);
            if (!json) Console.WriteLine($"Removed Host {unbindHostKey} from preset {unbindPresetId}.");
            return 0;
        case ["preset", "component", "add", var addPresetId, var componentKey, var newInstanceId]:
            var withComponent = await client.AddPresetComponentAsync(addPresetId, componentKey, newInstanceId);
            Print(withComponent, json, output);
            if (!json) Console.WriteLine($"Added {componentKey} as instance {newInstanceId} to preset {addPresetId}.");
            return 0;
        case ["preset", "component", "remove", var removePresetId, var removeInstanceId]:
            var withoutComponent = await client.RemovePresetComponentAsync(removePresetId, removeInstanceId);
            Print(withoutComponent, json, output);
            if (!json) Console.WriteLine($"Removed component instance {removeInstanceId} from preset {removePresetId}.");
            return 0;
        case ["preset", "component", "move", var movePresetId, var moveInstanceId, var destinationText]:
            if (!int.TryParse(destinationText, out var destinationIndex) || destinationIndex < 0)
            {
                Console.Error.WriteLine("Destination index must be a nonnegative integer (zero-based).");
                return UsageError;
            }
            var reordered = await client.MovePresetComponentAsync(movePresetId, moveInstanceId, destinationIndex);
            Print(reordered, json, output);
            if (!json) Console.WriteLine($"Moved component instance {moveInstanceId} to index {destinationIndex} in preset {movePresetId}.");
            return 0;
        case ["preset", "global", "set", var globalPresetId]:
            var global = await client.SetGlobalPresetAsync(globalPresetId == "none" ? null : globalPresetId);
            Print(global, json, output);
            if (!json) Console.WriteLine(globalPresetId == "none" ? "Cleared the global preset." : $"Set {globalPresetId} as the global preset.");
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
    Console.WriteLine("  desktop refresh   Refresh installed projects and read live runtime snapshots");
    Console.WriteLine("  desktop component set-property <session> <instance> <key> <json-value>  Set one live component property");
    Console.WriteLine("  desktop host set-property <key> <json-value>  Apply and save one property for the active Host");
    Console.WriteLine("  desktop set-config <session> <instance> <revision> <json-object>  Queue runtime config");
    Console.WriteLine("  desktop set-layout <session> <instance> <layout-json>  Queue runtime layout");
    Console.WriteLine("  settings show     Show non-language application settings");
    Console.WriteLine("  settings startup get|set <true|false>  Read or change Windows startup behavior");
    Console.WriteLine("  settings display get|set <index>  Read or change the selected display (zero-based)");
    Console.WriteLine("  settings logs path  Show the Companion log file path");
    Console.WriteLine("  preset list       List configured presets");
    Console.WriteLine("  preset create <id> <name>  Create a preset");
    Console.WriteLine("  preset rename <id> <name>  Rename a preset");
    Console.WriteLine("  preset delete <id>  Delete a preset and clear its bindings");
    Console.WriteLine("  preset set-enabled <id> <true|false>  Enable or disable a preset");
    Console.WriteLine("  preset move <id> <index>  Set preset priority (zero-based)");
    Console.WriteLine("  preset host add <preset-id> <host-catalog-key> [--allow-unverified]");
    Console.WriteLine("  preset host remove <preset-id> <host-catalog-key>");
    Console.WriteLine("  preset component add <preset> <catalog-key> <instance-id>");
    Console.WriteLine("  preset component remove <preset> <instance-id>");
    Console.WriteLine("  preset component move <preset> <instance-id> <zero-based-index>");
    Console.WriteLine("  preset component set-enabled <preset-id> <instance-id> <true|false>  Enable or disable one preset component instance");
    Console.WriteLine("  preset global set <preset-id|none>");
    Console.WriteLine("  authorization pending  List Host authorization requests awaiting a decision");
    Console.WriteLine("  authorization approve <session> --permission <name> [--permission <name>] [--remember]");
    Console.WriteLine("  authorization deny <session>  Deny a pending Host request");
    Console.WriteLine("  authorization trusted  List remembered Host approvals");
    Console.WriteLine("  authorization revoke <host-id>  Revoke remembered trust and active sessions");
    Console.WriteLine();
    Console.WriteLine("Use --json for machine-readable output. Data commands start the service when needed.");
}

static DesktopSettings CreateDesktopSettings() => new(
    Environment.GetEnvironmentVariable("BAPC_SETTINGS_PATH"), synchronizeStartup: false);

static async Task<int> RunSettingsCommandAsync(string[] command, HostAuthorizationClient client,
    bool json, JsonSerializerOptions output)
{
    var settings = CreateDesktopSettings();
    var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BAP Companion", "logs", "companion.log");
    if (command is ["settings", "show"])
    {
        var value = new { autoStart = settings.AutoStartEnabled, displayIndex = settings.DisplayIndex, logPath };
        Print(value, json, output);
        if (!json) Console.WriteLine($"Startup: {(value.autoStart ? "enabled" : "disabled")}\nDisplay index: {value.displayIndex}\nLog file: {value.logPath}");
        return 0;
    }
    if (command is ["settings", "startup", "get"])
    {
        Print(new { enabled = settings.AutoStartEnabled }, json, output);
        if (!json) Console.WriteLine($"Startup is {(settings.AutoStartEnabled ? "enabled" : "disabled")}.");
        return 0;
    }
    if (command is ["settings", "startup", "set", var startupValue] && bool.TryParse(startupValue, out var startupEnabled))
    {
        var executable = startupEnabled ? FindDesktopExecutable() : null;
        settings.SetAutoStart(startupEnabled, executable);
        Print(new { enabled = settings.AutoStartEnabled }, json, output);
        if (!json) Console.WriteLine($"Startup is now {(startupEnabled ? "enabled" : "disabled")}.");
        return 0;
    }
    if (command is ["settings", "display", "get"])
    {
        Print(new { displayIndex = settings.DisplayIndex }, json, output);
        if (!json) Console.WriteLine($"Selected display index: {settings.DisplayIndex}.");
        return 0;
    }
    if (command is ["settings", "display", "set", var displayText] && int.TryParse(displayText, out var displayIndex) && displayIndex is >= 0 and <= 31)
    {
        settings.SetDisplayIndex(displayIndex);
        Print(new { displayIndex = settings.DisplayIndex }, json, output);
        if (!json) Console.WriteLine($"Selected display index is now {displayIndex}.");
        return 0;
    }
    if (command is ["settings", "logs", "path"])
    {
        Print(new { logPath }, json, output);
        if (!json) Console.WriteLine(logPath);
        return 0;
    }
    Console.Error.WriteLine("Invalid or incomplete settings command. Run 'bapc --help' for usage.");
    return UsageError;
}

static string FindDesktopExecutable()
{
    var configured = Environment.GetEnvironmentVariable("BAPC_GUI_EXE");
    if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
    foreach (var name in new[] { "Bap.Companion.Desktop.Wpf.exe", "Bap.Companion.Desktop.exe" })
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(candidate)) return candidate;
    }
    throw new FileNotFoundException("Cannot locate the BAP Companion GUI executable. Set BAPC_GUI_EXE to its full path before enabling startup.");
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
