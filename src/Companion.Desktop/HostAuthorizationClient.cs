using System.IO.Pipes;
using System.Text.Json;
using Bap.Companion.Core;

namespace Bap.Companion.Desktop;

public sealed class HostAuthorizationClient(string pipeName = CompanionControlChannel.PipeName)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<HostAuthorizationRequest>> GetPendingAsync(CancellationToken cancellationToken = default) =>
        (await SendAsync(new("snapshot"), cancellationToken)).Requests;

    public async Task<CompanionServiceStatus> GetServiceStatusAsync(CancellationToken cancellationToken = default) =>
        (await SendAsync(new("service-status"), cancellationToken)).Service
            ?? throw new InvalidDataException("Companion service did not return its status.");

    public async Task StopServiceAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(new("service-stop"), cancellationToken);
        if (!response.Success) throw new IOException(response.Error ?? "Companion service rejected the stop request.");
    }

    public async Task<IReadOnlyList<InstalledItem>> GetCatalogAsync(CancellationToken cancellationToken = default) =>
        (await SendAsync(new("catalog-list"), cancellationToken)).CatalogItems ?? [];

    public async Task<PresetState> GetPresetsAsync(CancellationToken cancellationToken = default) =>
        (await SendAsync(new("presets"), cancellationToken)).Presets
            ?? throw new InvalidDataException("Companion service did not return preset state.");

    public async Task<EffectiveComposition> GetHostCompositionAsync(string hostKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hostKey)) throw new ArgumentException("A Host catalog key is required.", nameof(hostKey));
        var response = await SendAsync(new("host-composition", HostKey: hostKey), cancellationToken);
        return response.Composition ?? throw new IOException(response.Error ?? "Companion service did not return the Host composition.");
    }

    public async Task<IReadOnlyList<TrustedHost>> GetTrustedHostsAsync(CancellationToken cancellationToken = default) =>
        (await SendAsync(new("trusted"), cancellationToken)).TrustedHosts ?? [];

    public async Task<IReadOnlyList<RuntimeHostSnapshot>> GetRuntimeSnapshotsAsync(CancellationToken cancellationToken = default) =>
        (await SendAsync(new("runtime-snapshot"), cancellationToken)).RuntimeHosts ?? [];

    public async Task SetRuntimeConfigAsync(string sessionId, string instanceId,
        Dictionary<string, JsonElement> config, long revision, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(new("runtime-config", sessionId, InstanceId: instanceId,
            Config: config, ConfigRevision: revision), cancellationToken);
        if (!response.Success) throw new IOException(response.Error ?? "Companion could not update the runtime component configuration.");
    }

    public async Task SetRuntimeLayoutAsync(string sessionId, string instanceId, ComponentLayout layout,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(new("runtime-layout", sessionId, InstanceId: instanceId, Layout: layout), cancellationToken);
        if (!response.Success) throw new IOException(response.Error ?? "Companion could not update the runtime component layout.");
    }

    public async Task RefreshCatalogAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(new("refresh-catalog"), cancellationToken);
        if (!response.Success) throw new IOException(response.Error ?? "Companion could not refresh the installed catalog.");
    }

    public async Task<WallpaperSwitchReceipt> SwitchWallpaperAsync(string hostKey, int monitorIndex = 0,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(new("wallpaper-open", HostKey: hostKey, MonitorIndex: monitorIndex), cancellationToken);
        return response.WallpaperSwitch ?? throw new IOException(response.Error ?? "Companion could not switch the Wallpaper Engine wallpaper.");
    }

    public async Task<bool> RevokeTrustAsync(string id, CancellationToken cancellationToken = default) =>
        (await SendAsync(new("revoke", id), cancellationToken)).Success;

    public async Task<bool> DecideAsync(string sessionId, bool approve, IReadOnlyCollection<string> permissions,
        bool remember = false,
        CancellationToken cancellationToken = default)
    {
        var request = approve
            ? new AuthorizationControlRequest("approve", sessionId, permissions, remember)
            : new AuthorizationControlRequest("deny", sessionId);
        return (await SendAsync(request, cancellationToken)).Success;
    }

    private async Task<AuthorizationControlResponse> SendAsync(
        AuthorizationControlRequest request, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(1500, cancellationToken);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, Json).AsMemory(), cancellationToken);
        var response = await reader.ReadLineAsync(cancellationToken);
        return response is null
            ? throw new IOException("Authorization service closed the control pipe without a response.")
            : JsonSerializer.Deserialize<AuthorizationControlResponse>(response, Json)
                ?? throw new InvalidDataException("Authorization service returned an invalid response.");
    }
}
