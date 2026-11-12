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
    HostPropertyController hostProperties,
    IHostApplicationLifetime applicationLifetime,
    ILogger<AuthorizationPipeServer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset ProcessStartedAt = DateTimeOffset.UtcNow;
    private static readonly string PipeName = Environment.GetEnvironmentVariable("BAPC_CONTROL_PIPE") ?? CompanionControlChannel.PipeName;

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
        if (request.Operation.StartsWith("preset-", StringComparison.Ordinal))
            return await MutatePresetAsync(request, cancellationToken);
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
        if (request.Operation == "host-property-set")
            return await hostProperties.SetAsync(request, cancellationToken);
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

    private async Task<AuthorizationControlResponse> MutatePresetAsync(AuthorizationControlRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var state = await presets.ReadAsync(cancellationToken);
            PresetState updated;
            switch (request.Operation)
            {
                case "preset-create" when ValidText(request.PresetId, 128) && ValidText(request.Name, 128):
                    updated = PresetSelection.CreatePreset(state, request.PresetId!, request.Name!);
                    break;
                case "preset-delete" when ValidText(request.PresetId, 128):
                    updated = PresetSelection.DeletePreset(state, request.PresetId!);
                    break;
                case "preset-rename" when ValidText(request.PresetId, 128) && ValidText(request.Name, 128):
                    updated = PresetSelection.RenamePreset(state, request.PresetId!, request.Name!);
                    break;
                case "preset-component-add" when ValidText(request.PresetId, 128) && ValidText(request.ComponentKey, 256) && ValidText(request.InstanceId, 128):
                {
                    var catalogItems = await catalog.DiscoverAsync(cancellationToken);
                    var component = catalogItems.FirstOrDefault(item => item.Key == request.ComponentKey);
                    if (component is null) return new(false, [], $"component catalog key was not found: {request.ComponentKey}");
                    updated = PresetSelection.AddComponent(state, request.PresetId!, component, request.InstanceId!);
                    var limitErrors = PresetSelection.ValidateInstanceLimitIncreases(state, updated, catalogItems);
                    if (limitErrors.Count > 0) return new(false, [], string.Join("; ", limitErrors));
                    break;
                }
                case "preset-component-remove" when ValidText(request.PresetId, 128) && ValidText(request.InstanceId, 128):
                    updated = PresetSelection.RemoveComponent(state, request.PresetId!, request.InstanceId!);
                    break;
                case "preset-component-property" when ValidText(request.PresetId, 128) && ValidText(request.InstanceId, 128) &&
                                                        ValidText(request.PropertyKey, 128) && request.PropertyValue is not null:
                {
                    var preset = state.Presets.FirstOrDefault(candidate => candidate.Id == request.PresetId);
                    var instance = preset?.Components.FirstOrDefault(candidate => candidate.InstanceId == request.InstanceId);
                    if (instance is null) return new(false, [], $"component instance was not found in preset: {request.InstanceId}");
                    var catalogItems = await catalog.DiscoverAsync(cancellationToken);
                    var component = catalogItems.FirstOrDefault(item => item.Role == "component" && item.BapId == instance.ComponentId &&
                        (instance.SourceKey is null || item.Key == instance.SourceKey));
                    if (component is null) return new(false, [], $"installed component was not found: {instance.ComponentId}");
                    var definitions = component.Properties ?? [];
                    var definition = definitions.FirstOrDefault(property => property.Key == request.PropertyKey);
                    if (definition is null) return new(false, [], $"component property was not found: {request.PropertyKey}");
                    if (!definition.ConditionSupported || !WallpaperPropertyReader.IsValidValue(definition, request.PropertyValue!.Value))
                        return new(false, [], $"component property value is invalid or not currently editable: {request.PropertyKey}");
                    var config = instance.Config ?? [];
                    var effectiveValues = definitions.ToDictionary(property => property.Key,
                        property => config.TryGetValue(property.Key, out var configured) && WallpaperPropertyReader.IsValidValue(property, configured)
                            ? configured : property.DefaultValue, StringComparer.Ordinal);
                    if (!WallpaperPropertyReader.IsConditionMet(definition, effectiveValues))
                        return new(false, [], $"component property is currently hidden by its display condition: {request.PropertyKey}");
                    updated = PresetSelection.SetComponentProperty(state, request.PresetId!, request.InstanceId!, request.PropertyKey!, request.PropertyValue.Value);
                    break;
                }
                case "preset-component-layout" when ValidText(request.PresetId, 128) && ValidText(request.InstanceId, 128) && request.Layout is not null:
                    updated = PresetSelection.SetComponentLayout(state, request.PresetId!, request.InstanceId!, request.Layout);
                    break;
                case "preset-component-move" when ValidText(request.PresetId, 128) && ValidText(request.InstanceId, 128) && request.DestinationIndex is >= 0 and <= 10000:
                    updated = PresetSelection.MoveComponent(state, request.PresetId!, request.InstanceId!, request.DestinationIndex.Value);
                    break;
                case "preset-global-set" when request.PresetId is null || ValidText(request.PresetId, 128):
                    updated = PresetSelection.SetGlobalPreset(state, request.PresetId);
                    break;
                case "preset-host-add" when ValidText(request.PresetId, 128) && ValidText(request.HostKey, 256):
                {
                    var catalogItems = await catalog.DiscoverAsync(cancellationToken);
                    var host = catalogItems.FirstOrDefault(item => item.Key == request.HostKey && item.Role == "host");
                    if (host is null) return new(false, [], $"Host catalog key was not found: {request.HostKey}");
                    if ((host.BapId is null || host.Status != "valid") && !request.AllowUnverifiedHost)
                        return new(false, [], "Host is not BAP-verified; retry with --allow-unverified after reviewing it.");
                    updated = PresetSelection.AddHostToPreset(state, request.PresetId!, request.HostKey!);
                    break;
                }
                case "preset-host-remove" when ValidText(request.PresetId, 128) && ValidText(request.HostKey, 512):
                    updated = PresetSelection.RemoveHostFromPreset(state, request.PresetId!, request.HostKey!);
                    break;
                case "preset-enabled" when ValidText(request.PresetId, 128) && request.PresetEnabled is not null:
                    updated = PresetSelection.SetPresetEnabled(state, request.PresetId!, request.PresetEnabled.Value);
                    break;
                case "preset-move" when ValidText(request.PresetId, 128) && request.DestinationIndex is >= 0 and <= 10000:
                    updated = PresetSelection.MovePreset(state, request.PresetId!, request.DestinationIndex.Value);
                    break;
                default:
                    return new(false, [], "invalid preset mutation request");
            }

            await presets.WriteAsync(updated, cancellationToken);
            return new(true, [], Presets: await presets.ReadAsync(cancellationToken));
        }
        catch (Exception error) when (error is ArgumentException or KeyNotFoundException or InvalidOperationException or IOException or InvalidDataException)
        {
            return new(false, [], error.Message);
        }
    }

    private static bool ValidText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;

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
