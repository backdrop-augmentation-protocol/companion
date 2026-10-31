using System.IO.Pipes;
using System.Text.Json;
using Bap.Companion.Core;

namespace Bap.Companion.Desktop;

public sealed class HostAuthorizationClient(string pipeName)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<HostAuthorizationRequest>> GetPendingAsync(CancellationToken cancellationToken = default) =>
        (await SendAsync(new("snapshot"), cancellationToken)).Requests;

    public async Task<IReadOnlyList<TrustedHost>> GetTrustedHostsAsync(CancellationToken cancellationToken = default) =>
        (await SendAsync(new("trusted"), cancellationToken)).TrustedHosts ?? [];

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
