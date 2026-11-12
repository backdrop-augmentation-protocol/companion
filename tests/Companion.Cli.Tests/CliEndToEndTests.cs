using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Xunit;

namespace Companion.Cli.Tests;

public sealed class CliEndToEndTests
{
    [Fact]
    public async Task HelpWorksWithoutStartingOrConnectingToTheService()
    {
        var result = await RunCliAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("BAP Companion CLI (bapc)", result.StandardOutput);
        Assert.Contains("authorization approve", result.StandardOutput);
    }

    [Fact]
    public async Task CatalogListUsesSharedServiceAndEmitsJson()
    {
        var service = new FakeControlService(
            ("service-status", """{"success":true,"requests":[],"service":{"processId":42,"version":"0.2.0","startedAt":"2026-01-01T00:00:00Z"}}"""),
            ("catalog-list", """{"success":true,"requests":[],"catalogItems":[{"key":"local:demo","sourceKind":"local","role":"host","title":"Demo host","projectPath":"C:/demo","status":"ready","bapId":"org.example.host","bapVersion":"1.0.0"}]}"""));

        var run = RunCliAsync(["catalog", "list", "--json"], service.PipeName);
        var requests = service.ServeAsync(2);
        var result = await run;
        var observed = await requests;

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("local:demo", json.RootElement[0].GetProperty("key").GetString());
        Assert.Equal(new[] { "service-status", "catalog-list" }, observed.Select(Operation));
    }

    [Fact]
    public async Task CatalogRefreshProducesValidJsonInsteadOfMixingText()
    {
        var service = new FakeControlService(
            ("service-status", ServiceStatus),
            ("refresh-catalog", """{"success":true,"requests":[]}"""));
        var run = RunCliAsync(["catalog", "refresh", "--json"], service.PipeName);
        var requests = service.ServeAsync(2);
        var result = await run;
        var observed = await requests;

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.True(json.RootElement.GetProperty("refreshed").GetBoolean());
        Assert.Equal(new[] { "service-status", "refresh-catalog" }, observed.Select(Operation));
    }

    [Fact]
    public async Task HostSwitchPassesMonitorAndReturnsMachineReadableReceipt()
    {
        var service = new FakeControlService(
            ("service-status", ServiceStatus),
            ("wallpaper-open", """{"success":true,"requests":[],"wallpaperSwitch":{"status":"confirmed","projectFile":"C:/demo/project.json","monitorIndex":1,"currentWallpaper":"C:/demo/index.html"}}"""));

        var run = RunCliAsync(["host", "switch", "local:demo", "--monitor", "1", "--json"], service.PipeName);
        var requests = service.ServeAsync(2);
        var result = await run;
        var observed = await requests;

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("confirmed", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("monitorIndex").GetInt32());
        Assert.Equal(new[] { "service-status", "wallpaper-open" }, observed.Select(Operation));
    }

    [Fact]
    public async Task AuthorizationApprovalRejectsUnrequestedPermissionBeforeSendingDecision()
    {
        var service = new FakeControlService(
            ("service-status", ServiceStatus),
            ("snapshot", """{"success":true,"requests":[{"sessionId":"session-1","claimedIdentity":{"id":"org.example.host","name":"Demo","version":"1.0"},"claimedCapabilities":[],"requestedPermissions":["component.discovery"],"requestedAt":"2026-01-01T00:00:00Z"}]}"""));

        var run = RunCliAsync(["authorization", "approve", "session-1", "--permission", "config.write"], service.PipeName);
        var requests = service.ServeAsync(2);
        var result = await run;
        var observed = await requests;

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Permission was not requested or is unsupported", result.StandardError);
        Assert.Equal(new[] { "service-status", "snapshot" }, observed.Select(Operation));
    }

    [Fact]
    public async Task PresetInstanceToggleSendsTargetAndPrintsUpdatedState()
    {
        var service = new FakeControlService(
            ("service-status", ServiceStatus),
            ("preset-component-enabled", """{"success":true,"requests":[],"presets":{"presets":[{"id":"daily","name":"Daily","components":[{"componentId":"org.example.clock","sourceKey":"local:clock","instanceId":"clock-1","enabled":false,"userVisible":true,"layout":{"mode":"manual","anchor":"center","x":0,"y":0,"width":0.4,"height":0.3,"scale":1,"rotation":0,"opacity":1,"zIndex":0,"locked":false}}],"layoutMode":"manual","grid":null,"revision":2,"updatedAt":"2026-01-01T00:00:00Z"}],"globalPresetId":null,"hostPresetIds":{},"hostConfigs":{}}}"""));

        var run = RunCliAsync(["preset", "component", "set-enabled", "daily", "clock-1", "false", "--json"], service.PipeName);
        var requests = service.ServeAsync(2);
        var result = await run;
        var observed = await requests;

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("daily", json.RootElement.GetProperty("presetId").GetString());
        Assert.False(json.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(new[] { "service-status", "preset-component-enabled" }, observed.Select(Operation));
    }

    [Fact]
    public async Task PresetCreateSendsExplicitIdAndJoinedName()
    {
        var service = MutationService("preset-create");
        var run = RunCliAsync(["preset", "create", "focus", "Focus", "Setup"], service.PipeName);
        var requests = service.ServeAsync(2);
        var result = await run;
        var observed = await requests;

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("focus", observed[1].GetProperty("presetId").GetString());
        Assert.Equal("Focus Setup", observed[1].GetProperty("name").GetString());
    }

    [Fact]
    public async Task PresetDeleteSendsSelectedId()
    {
        var service = MutationService("preset-delete");
        var run = RunCliAsync(["preset", "delete", "focus"], service.PipeName);
        var requests = service.ServeAsync(2);
        var result = await run;
        var observed = await requests;

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("focus", observed[1].GetProperty("presetId").GetString());
    }

    [Fact]
    public async Task PresetRenameSendsJoinedName()
    {
        var service = MutationService("preset-rename");
        var run = RunCliAsync(["preset", "rename", "focus", "After", "Work"], service.PipeName);
        var requests = service.ServeAsync(2);
        var result = await run;
        var observed = await requests;

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("focus", observed[1].GetProperty("presetId").GetString());
        Assert.Equal("After Work", observed[1].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ComponentAddRemoveAndMoveSendExactInstanceCoordinates()
    {
        var addService = MutationService("preset-component-add");
        var addRun = RunCliAsync(["preset", "component", "add", "focus", "local:clock", "clock-2"], addService.PipeName);
        var addRequests = addService.ServeAsync(2);
        var addResult = await addRun;
        var added = await addRequests;
        Assert.Equal(0, addResult.ExitCode);
        Assert.Equal("focus", added[1].GetProperty("presetId").GetString());
        Assert.Equal("local:clock", added[1].GetProperty("componentKey").GetString());
        Assert.Equal("clock-2", added[1].GetProperty("instanceId").GetString());

        var removeService = MutationService("preset-component-remove");
        var removeRun = RunCliAsync(["preset", "component", "remove", "focus", "clock-2"], removeService.PipeName);
        var removeRequests = removeService.ServeAsync(2);
        var removeResult = await removeRun;
        var removed = await removeRequests;
        Assert.Equal(0, removeResult.ExitCode);
        Assert.Equal("clock-2", removed[1].GetProperty("instanceId").GetString());

        var moveService = MutationService("preset-component-move");
        var moveRun = RunCliAsync(["preset", "component", "move", "focus", "clock-2", "0"], moveService.PipeName);
        var moveRequests = moveService.ServeAsync(2);
        var moveResult = await moveRun;
        var moved = await moveRequests;
        Assert.Equal(0, moveResult.ExitCode);
        Assert.Equal(0, moved[1].GetProperty("destinationIndex").GetInt32());
    }

    [Fact]
    public async Task GlobalPresetCanBeClearedExplicitly()
    {
        var service = MutationService("preset-global-set");
        var run = RunCliAsync(["preset", "global", "set", "none", "--json"], service.PipeName);
        var requests = service.ServeAsync(2);
        var result = await run;
        var observed = await requests;

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("preset-global-set", Operation(observed[1]));
        Assert.Equal(JsonValueKind.Null, observed[1].GetProperty("presetId").ValueKind);
    }

    [Fact]
    public async Task PresetCommandsRoundTripThroughAnIsolatedCompanionServer()
    {
        await using var server = await IsolatedCompanionServer.StartAsync();

        var create = await RunCliAsync(["preset", "create", "e2e", "CLI", "Integration"], server.PipeName);
        Assert.Equal(0, create.ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "create", "higher", "Higher", "Priority"], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "create", "global", "Global", "Fallback"], server.PipeName)).ExitCode);

        var catalogResult = await RunCliAsync(["catalog", "list", "--json"], server.PipeName);
        Assert.Equal(0, catalogResult.ExitCode);
        using var catalogJson = JsonDocument.Parse(catalogResult.StandardOutput);
        var componentKey = catalogJson.RootElement.EnumerateArray().Single(item => item.GetProperty("title").GetString() == "Test clock")
            .GetProperty("key").GetString()!;
        var hostKey = catalogJson.RootElement.EnumerateArray().Single(item => item.GetProperty("title").GetString() == "Verified test host")
            .GetProperty("key").GetString()!;
        var unverifiedHostKey = catalogJson.RootElement.EnumerateArray().Single(item => item.GetProperty("title").GetString() == "Unverified wallpaper")
            .GetProperty("key").GetString()!;

        Assert.Equal(0, (await RunCliAsync(["preset", "component", "add", "e2e", componentKey, "clock-a"], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "component", "add", "e2e", componentKey, "clock-b"], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "component", "add", "higher", componentKey, "clock-high"], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "component", "add", "global", componentKey, "clock-global"], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "component", "move", "e2e", "clock-b", "0"], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "component", "set-enabled", "e2e", "clock-b", "false"], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "component", "set-property", "e2e", "clock-a", "caption", "\"Desk\""], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "component", "set-layout", "e2e", "clock-a",
            "{\"mode\":\"manual\",\"anchor\":\"top-left\",\"x\":0.12,\"y\":0.1,\"width\":0.3,\"height\":0.2,\"scale\":1,\"rotation\":0,\"opacity\":1,\"zIndex\":4,\"locked\":false}"], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "host", "add", "e2e", hostKey], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "host", "add", "higher", hostKey], server.PipeName)).ExitCode);
        var deniedUnverified = await RunCliAsync(["preset", "host", "add", "higher", unverifiedHostKey], server.PipeName);
        Assert.NotEqual(0, deniedUnverified.ExitCode);
        Assert.Contains("--allow-unverified", deniedUnverified.StandardError);
        Assert.Equal(0, (await RunCliAsync(["preset", "host", "add", "higher", unverifiedHostKey, "--allow-unverified"], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "set-enabled", "e2e", "false"], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "move", "higher", "0"], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "rename", "higher", "Priority", "One"], server.PipeName)).ExitCode);

        var initialComposition = await RunCliAsync(["host", "composition", hostKey, "--json"], server.PipeName);
        Assert.Equal(0, initialComposition.ExitCode);
        using (var compositionJson = JsonDocument.Parse(initialComposition.StandardOutput))
        {
            Assert.Equal("higher", compositionJson.RootElement.GetProperty("presetId").GetString());
            Assert.Equal("clock-high", compositionJson.RootElement.GetProperty("instances")[0].GetProperty("instanceId").GetString());
        }
        Assert.Equal(0, (await RunCliAsync(["preset", "set-enabled", "e2e", "true"], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "move", "e2e", "0"], server.PipeName)).ExitCode);
        var reorderedComposition = await RunCliAsync(["host", "composition", hostKey, "--json"], server.PipeName);
        using (var compositionJson = JsonDocument.Parse(reorderedComposition.StandardOutput))
            Assert.Equal("e2e", compositionJson.RootElement.GetProperty("presetId").GetString());
        Assert.Equal(0, (await RunCliAsync(["preset", "global", "set", "global"], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "move", "global", "0"], server.PipeName)).ExitCode);
        var globalComposition = await RunCliAsync(["host", "composition", hostKey, "--json"], server.PipeName);
        using (var compositionJson = JsonDocument.Parse(globalComposition.StandardOutput))
            Assert.Equal("global", compositionJson.RootElement.GetProperty("presetId").GetString());
        Assert.Equal(0, (await RunCliAsync(["preset", "global", "set", "none"], server.PipeName)).ExitCode);
        var fallbackComposition = await RunCliAsync(["host", "composition", hostKey, "--json"], server.PipeName);
        using (var compositionJson = JsonDocument.Parse(fallbackComposition.StandardOutput))
            Assert.Equal("e2e", compositionJson.RootElement.GetProperty("presetId").GetString());

        var changed = await RunCliAsync(["preset", "list", "--json"], server.PipeName);
        Assert.Equal(0, changed.ExitCode);
        using (var changedJson = JsonDocument.Parse(changed.StandardOutput))
        {
            var presets = changedJson.RootElement.GetProperty("presets");
            var e2ePreset = presets.EnumerateArray().Single(preset => preset.GetProperty("id").GetString() == "e2e");
            var components = e2ePreset.GetProperty("components");
            Assert.Equal("clock-b", components[0].GetProperty("instanceId").GetString());
            Assert.False(components[0].GetProperty("enabled").GetBoolean());
            Assert.Equal("clock-a", components[1].GetProperty("instanceId").GetString());
            Assert.Equal("Desk", components[1].GetProperty("config").GetProperty("caption").GetString());
            Assert.Equal(0.12, components[1].GetProperty("layout").GetProperty("x").GetDouble());
            Assert.Equal(4, components[1].GetProperty("layout").GetProperty("zIndex").GetInt32());
            Assert.Equal("global", presets[0].GetProperty("id").GetString());
            Assert.True(e2ePreset.GetProperty("enabled").GetBoolean());
            var hostBindings = changedJson.RootElement.GetProperty("hostPresetIds").GetProperty(hostKey);
            Assert.Equal(new[] { "e2e", "higher" }, hostBindings.EnumerateArray().Select(id => id.GetString()));
            Assert.Equal(new[] { "higher" }, changedJson.RootElement.GetProperty("hostPresetIds").GetProperty(unverifiedHostKey)
                .EnumerateArray().Select(id => id.GetString()));
        }

        Assert.Equal(0, (await RunCliAsync(["preset", "component", "remove", "e2e", "clock-a"], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "delete", "e2e"], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "host", "remove", "higher", hostKey], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "host", "remove", "higher", unverifiedHostKey], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "delete", "higher"], server.PipeName)).ExitCode);
        Assert.Equal(0, (await RunCliAsync(["preset", "delete", "global"], server.PipeName)).ExitCode);

        var finalState = await RunCliAsync(["preset", "list", "--json"], server.PipeName);
        Assert.Equal(0, finalState.ExitCode);
        using var finalJson = JsonDocument.Parse(finalState.StandardOutput);
        Assert.Empty(finalJson.RootElement.GetProperty("presets").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, finalJson.RootElement.GetProperty("globalPresetId").ValueKind);
    }

    private const string ServiceStatus = """{"success":true,"requests":[],"service":{"processId":42,"version":"0.2.0","startedAt":"2026-01-01T00:00:00Z"}}""";
    private const string EmptyPresetMutationResponse = """{"success":true,"requests":[],"presets":{"presets":[],"globalPresetId":null,"hostPresetIds":{},"hostConfigs":{}}}""";

    private static FakeControlService MutationService(string operation) =>
        new(("service-status", ServiceStatus), (operation, EmptyPresetMutationResponse));

    private static string? Operation(JsonElement request) => request.GetProperty("operation").GetString();

    private static async Task<CliResult> RunCliAsync(params string[] arguments) => await RunCliAsync(arguments, null);

    private static async Task<CliResult> RunCliAsync(string[] arguments, string? pipeName)
    {
        var assembly = CliAssemblyPath();
        Assert.True(File.Exists(assembly), $"CLI assembly was not built: {assembly}");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(assembly)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(assembly);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (pipeName is not null) start.Environment["BAPC_CONTROL_PIPE"] = pipeName;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the BAPC CLI test process.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await process.WaitForExitAsync(timeout.Token);
        return new(process.ExitCode, await stdout, await stderr);
    }

    private static string CliAssemblyPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Companion.slnx")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Could not locate the Companion repository root.");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        return Path.Combine(directory.FullName, "src", "Companion.Cli", "bin", configuration, "net10.0", "bapc.dll");
    }

    private static string ServerAssemblyPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Companion.slnx")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Could not locate the Companion repository root.");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        return Path.Combine(directory.FullName, "src", "Companion.Server", "bin", configuration, "net10.0", "Bap.Companion.Server.dll");
    }

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class IsolatedCompanionServer : IAsyncDisposable
    {
        private readonly string directory;
        private readonly Process process;
        public string PipeName { get; }

        private IsolatedCompanionServer(string directory, string pipeName, Process process)
        {
            this.directory = directory;
            PipeName = pipeName;
            this.process = process;
        }

        public static async Task<IsolatedCompanionServer> StartAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"bapc-cli-e2e-{Guid.NewGuid():N}");
            var components = Path.Combine(directory, "components", "clock");
            Directory.CreateDirectory(components);
            await File.WriteAllTextAsync(Path.Combine(components, "project.json"), """
                { "type": "web", "title": "Test clock", "general": { "properties": {
                  "caption": { "type": "textinput", "text": "Caption", "value": "Clock" }
                } } }
                """);
            var host = Path.Combine(directory, "components", "host");
            Directory.CreateDirectory(host);
            await File.WriteAllTextAsync(Path.Combine(host, "project.json"), """{"type":"web","title":"Verified test host"}""");
            await File.WriteAllTextAsync(Path.Combine(host, "manifest.json"), """{"protocol":{"name":"bap","version":"0.1.0","profile":"core"},"id":"org.example.host","name":"Test host","version":"1.0.0","role":"host","capabilities":[]}""");
            var unverified = Path.Combine(directory, "components", "unverified");
            Directory.CreateDirectory(unverified);
            await File.WriteAllTextAsync(Path.Combine(unverified, "project.json"), """{"type":"web","title":"Unverified wallpaper"}""");
            await File.WriteAllTextAsync(Path.Combine(components, "manifest.json"), """
                { "protocol": { "name": "bap", "version": "0.1.0", "profile": "core" }, "id": "org.example.clock", "name": "Test clock", "version": "1.0.0", "role": "component", "capabilities": [], "entrypoints": { "default": "./index.html", "embedded": "./index.html" }, "modes": ["embedded"] }
                """);

            var serverAssembly = ServerAssemblyPath();
            Assert.True(File.Exists(serverAssembly), $"Server assembly was not built: {serverAssembly}");
            var pipeName = $"Bap.Cli.E2E.{Guid.NewGuid():N}";
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(serverAssembly)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(serverAssembly);
            start.ArgumentList.Add("--urls");
            start.ArgumentList.Add("http://127.0.0.1:0");
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            start.Environment["BAPC_CONTROL_PIPE"] = pipeName;
            start.Environment["BAP_SERVER_MUTEX_NAME"] = $"Local\\Bap.Cli.E2E.{Guid.NewGuid():N}";
            start.Environment["BAP_PRESETS_PATH"] = Path.Combine(directory, "presets.json");
            start.Environment["BAP_LOCAL_ROOTS"] = Path.Combine(directory, "components");
            start.Environment["BAP_WORKSHOP_ROOTS"] = "";
            start.Environment["BAP_DISCOVER_STEAM_WORKSHOP"] = "0";
            var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated Companion Server.");
            var server = new IsolatedCompanionServer(directory, pipeName, process);
            try
            {
                await server.WaitForReadyAsync();
                return server;
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        private async Task WaitForReadyAsync()
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (process.HasExited)
                    throw new InvalidOperationException($"Isolated Companion Server exited ({process.ExitCode}): {await process.StandardError.ReadToEndAsync()}");
                try
                {
                    await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(100);
                    return;
                }
                catch (TimeoutException) { await Task.Delay(50); }
                catch (IOException) { await Task.Delay(50); }
            }
            throw new TimeoutException("Isolated Companion Server did not create its control pipe within 12 seconds.");
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeControlService(string pipeName, params (string Operation, string Response)[] exchanges)
    {
        public string PipeName { get; } = pipeName;

        public FakeControlService(params (string Operation, string Response)[] exchanges)
            : this($"Bap.Cli.Test.{Guid.NewGuid():N}", exchanges) { }

        public async Task<IReadOnlyList<JsonElement>> ServeAsync(int count)
        {
            var observed = new List<JsonElement>(count);
            for (var index = 0; index < count; index++)
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                await pipe.WaitForConnectionAsync(timeout.Token);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe) { AutoFlush = true };
                using var request = JsonDocument.Parse(await reader.ReadLineAsync(timeout.Token) ?? "{}");
                var operation = request.RootElement.GetProperty("operation").GetString()!;
                observed.Add(request.RootElement.Clone());
                var exchange = exchanges[index];
                Assert.Equal(exchange.Operation, operation);
                await writer.WriteLineAsync(exchange.Response.AsMemory(), timeout.Token);
            }
            return observed;
        }
    }
}
