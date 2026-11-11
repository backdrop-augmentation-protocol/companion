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
    var client = new HostAuthorizationClient();
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

    await EnsureServiceAsync(client);
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
        case ["desktop", "status"]:
            var runtime = await client.GetRuntimeSnapshotsAsync();
            Print(runtime, json, output);
            if (!json && runtime.Count == 0) Console.WriteLine("No Host has reported a live runtime snapshot.");
            if (!json)
                foreach (var host in runtime)
                    Console.WriteLine($"{host.HostName} ({host.HostId}) — {host.Instances.Count} component instance(s), updated {host.UpdatedAt:O}");
            return 0;
        case ["preset", "list"]:
            var presets = await client.GetPresetsAsync();
            Print(presets, json, output);
            if (!json && presets.Presets.Count == 0) Console.WriteLine("No presets are configured.");
            if (!json)
                foreach (var preset in presets.Presets)
                    Console.WriteLine($"{preset.Id,-24} {preset.Name} — {preset.Components.Count} component instance(s)");
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
    Console.WriteLine("  catalog list      List installed Wallpaper Engine projects");
    Console.WriteLine("  catalog refresh   Refresh the installed project catalog");
    Console.WriteLine("  desktop status    Show live Host and component snapshots");
    Console.WriteLine("  preset list       List configured presets");
    Console.WriteLine();
    Console.WriteLine("Use --json for machine-readable output. Data commands start the service when needed.");
}
