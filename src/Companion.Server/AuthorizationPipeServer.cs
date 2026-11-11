using System.IO.Pipes;
using System.Text.Json;
using Bap.Companion.Core;

namespace Bap.Companion.Server;

internal sealed class AuthorizationPipeServer(
    HostAuthorizationQueue authorizations,
    ISessionStore sessions,
    RuntimeDesktopStore runtime,
    InstalledCatalog catalog,
    JsonPresetStore presets,
    WallpaperEngineSwitcher wallpaperSwitcher,
    IHostApplicationLifetime applicationLifetime,
    ILogger<AuthorizationPipeServer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset ProcessStartedAt = DateTimeOffset.UtcNow;
    private const string PipeName = CompanionControlChannel.PipeName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stoppingToken);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe) { AutoFlush = true };
                var line = await reader.ReadLineAsync(stoppingToken);
                var request = line is null ? null : JsonSerializer.Deserialize<AuthorizationControlRequest>(line, Json);
                var response = await HandleAsync(request, stoppingToken);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json).AsMemory(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (IOException error) { logger.LogDebug(error, "Authorization UI pipe disconnected."); }
            catch (JsonException error) { logger.LogWarning(error, "Invalid authorization UI pipe message."); }
        }
    }

    private async Task<AuthorizationControlResponse> HandleAsync(AuthorizationControlRequest? request, CancellationToken cancellationToken)
    {
        if (request is null) return new(false, [], "invalid request");
        if (request.Operation == "service-status")
            return new(true, [], Service: new(Environment.ProcessId, typeof(AuthorizationPipeServer).Assembly.GetName().Version?.ToString() ?? "unknown", ProcessStartedAt));
        if (request.Operation == "service-stop")
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(250);
                applicationLifetime.StopApplication();
            }, CancellationToken.None);
            return new(true, []);
        }
        if (request.Operation == "catalog-list")
            return new(true, [], CatalogItems: await catalog.DiscoverAsync(cancellationToken));
        if (request.Operation == "presets")
            return new(true, [], Presets: await presets.ReadAsync(cancellationToken));
        if (request.Operation == "preset-component-enabled" && !string.IsNullOrWhiteSpace(request.PresetId) &&
            !string.IsNullOrWhiteSpace(request.InstanceId) && request.ComponentEnabled is not null)
        {
            try
            {
                var state = await presets.ReadAsync(cancellationToken);
                var updated = PresetSelection.SetComponentEnabled(state, request.PresetId, request.InstanceId, request.ComponentEnabled.Value);
                await presets.WriteAsync(updated, cancellationToken);
                return new(true, [], Presets: await presets.ReadAsync(cancellationToken));
            }
            catch (Exception error) when (error is ArgumentException or KeyNotFoundException or IOException or InvalidDataException)
            {
                return new(false, [], error.Message);
            }
        }
        if (request.Operation == "host-composition" && !string.IsNullOrWhiteSpace(request.HostKey) && request.HostKey.Length <= 256)
        {
            var state = await presets.ReadAsync(cancellationToken);
            var items = await catalog.DiscoverAsync(cancellationToken);
            if (!items.Any(item => item.Key == request.HostKey && item.Role == "host"))
                return new(false, [], $"unknown Host catalog key: {request.HostKey}");
            return new(true, [], Composition: EffectiveComponents.ResolveComposition(state, request.HostKey, items));
        }
        if (request.Operation == "refresh-catalog")
        {
            var items = await catalog.DiscoverAsync(cancellationToken, force: true);
            logger.LogInformation("Installed catalog refreshed from Companion UI; discovered {ItemCount} items", items.Count);
            return new(true, []);
        }
        if (request.Operation == "wallpaper-open" && !string.IsNullOrWhiteSpace(request.HostKey) && request.MonitorIndex is >= 0 and <= 31)
        {
            try
            {
                var result = await wallpaperSwitcher.OpenAsync(request.HostKey, request.MonitorIndex.Value, cancellationToken);
                return new(true, [], WallpaperSwitch: result);
            }
            catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or ArgumentException)
            {
                logger.LogWarning(error, "Wallpaper switch request failed for catalog key {HostKey}", request.HostKey);
                return new(false, [], error.Message);
            }
        }
        return request.Operation switch
        {
            "snapshot" => new(true, authorizations.Snapshot()),
            "trusted" => new(true, [], TrustedHosts: authorizations.TrustedHosts()),
            "runtime-snapshot" => new(true, [], RuntimeHosts: runtime.Snapshot()),
            "runtime-config" when !string.IsNullOrWhiteSpace(request.SessionId) &&
                                  !string.IsNullOrWhiteSpace(request.InstanceId) &&
                                  request.Config is not null && request.ConfigRevision is > 0 => SetRuntimeConfig(request),
            "runtime-layout" when !string.IsNullOrWhiteSpace(request.SessionId) &&
                                  !string.IsNullOrWhiteSpace(request.InstanceId) &&
                                  request.Layout is not null => SetRuntimeLayout(request),
            "revoke" when !string.IsNullOrWhiteSpace(request.SessionId) => Revoke(request.SessionId),
            "approve" when !string.IsNullOrWhiteSpace(request.SessionId) => new(
                authorizations.Approve(request.SessionId, request.Permissions ?? [], request.Remember), []),
            "deny" when !string.IsNullOrWhiteSpace(request.SessionId) => new(
                authorizations.Deny(request.SessionId), []),
            _ => new(false, [], "unsupported operation")
        };
    }

    private AuthorizationControlResponse SetRuntimeConfig(AuthorizationControlRequest request)
    {
        var success = runtime.TrySetConfig(request.SessionId!, request.InstanceId!, request.Config!, request.ConfigRevision!.Value);
        return new(success, [], success ? null : "runtime instance was not found or revision is stale");
    }

    private AuthorizationControlResponse SetRuntimeLayout(AuthorizationControlRequest request)
    {
        var success = runtime.TrySetLayout(request.SessionId!, request.InstanceId!, request.Layout!);
        return new(success, [], success ? null : "runtime instance was not found or layout is invalid or unchanged");
    }

    private AuthorizationControlResponse Revoke(string hostId)
    {
        var trustRemoved = authorizations.RevokeTrust(hostId);
        var sessionsRevoked = sessions.RevokeHost(hostId);
        logger.LogInformation("Host authorization revoked for {HostId}; removedTrust={TrustRemoved}, invalidatedSessions={SessionCount}", hostId, trustRemoved, sessionsRevoked);
        return new(trustRemoved || sessionsRevoked > 0, [], TrustedHosts: authorizations.TrustedHosts());
    }
}
