using System.IO.Pipes;
using System.Text.Json;
using Bap.Companion.Core;

namespace Bap.Companion.Server;

internal sealed class AuthorizationPipeServer(
    HostAuthorizationQueue authorizations,
    ILogger<AuthorizationPipeServer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string? pipeName = Environment.GetEnvironmentVariable("BAP_AUTHORIZATION_PIPE");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 200 || pipeName.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            logger.LogError("Authorization control pipe is not configured; Host approvals will remain unavailable.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stoppingToken);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe) { AutoFlush = true };
                var line = await reader.ReadLineAsync(stoppingToken);
                var request = line is null ? null : JsonSerializer.Deserialize<AuthorizationControlRequest>(line, Json);
                var response = Handle(request);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json).AsMemory(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (IOException error) { logger.LogDebug(error, "Authorization UI pipe disconnected."); }
            catch (JsonException error) { logger.LogWarning(error, "Invalid authorization UI pipe message."); }
        }
    }

    private AuthorizationControlResponse Handle(AuthorizationControlRequest? request)
    {
        if (request is null) return new(false, [], "invalid request");
        return request.Operation switch
        {
            "snapshot" => new(true, authorizations.Snapshot()),
            "trusted" => new(true, [], TrustedHosts: authorizations.TrustedHosts()),
            "revoke" when !string.IsNullOrWhiteSpace(request.SessionId) => new(
                authorizations.RevokeTrust(request.SessionId), [], TrustedHosts: authorizations.TrustedHosts()),
            "approve" when !string.IsNullOrWhiteSpace(request.SessionId) => new(
                authorizations.Approve(request.SessionId, request.Permissions ?? [], request.Remember), []),
            "deny" when !string.IsNullOrWhiteSpace(request.SessionId) => new(
                authorizations.Deny(request.SessionId), []),
            _ => new(false, [], "unsupported operation")
        };
    }
}
