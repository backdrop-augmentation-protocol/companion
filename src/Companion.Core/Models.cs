using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bap.Companion.Core;

public sealed record Session(string Id, string Token, DateTimeOffset ExpiresAt, string ClientKind, IReadOnlyCollection<string> GrantedPermissions);

public sealed record SessionRequest(string ClientName, string ClientKind, ProtocolDescriptor? Protocol = null);

public sealed record SessionResponse(string SessionId, string AccessToken, DateTimeOffset ExpiresAt);

public sealed record ProtocolDescriptor(string Name, string Version, string Profile);

public sealed record Identity(string Id, string Name, string Version);

public sealed record HostHelloPayload(string Role, Identity Identity, IReadOnlyCollection<string> ProtocolVersions, IReadOnlyCollection<string> Capabilities, IReadOnlyCollection<string> RequestedPermissions);

public sealed record CompanionHelloPayload(ProtocolDescriptor SelectedProtocol, string Role, Identity Identity, IReadOnlyCollection<string> Capabilities, IReadOnlyCollection<string> Permissions);

public sealed record ProtocolMessage(ProtocolDescriptor Protocol, string Kind, string Id, DateTimeOffset Timestamp, string? SessionId = null, string? CorrelationId = null, string? Method = null, string? Event = null, JsonElement? Payload = null, bool? Ok = null, JsonElement? Result = null, BapError? Error = null);

public sealed record BapError(string Code, string Message, bool? Retryable = null, Dictionary<string, object?>? Details = null);

public sealed record ComponentManifest(
    ProtocolDescriptor Protocol,
    string Id,
    string Version,
    string Name,
    string Role,
    IReadOnlyCollection<string> Capabilities,
    IReadOnlyDictionary<string, string> Entrypoints,
    IReadOnlyCollection<string> Modes,
    IReadOnlyCollection<string>? Permissions = null,
    IReadOnlyCollection<string>? ConfigScopes = null,
    string? ImplementationVersion = null,
    string? Description = null,
    [property: JsonIgnore]
    string? Source = null);

public sealed record ComponentDescriptor(ComponentManifest Manifest, string Location);

public sealed record CompanionConfig(
    Dictionary<string, object?> Global,
    Dictionary<string, Dictionary<string, object?>> Wallpapers,
    Dictionary<string, Dictionary<string, object?>>? Series = null,
    Dictionary<string, Dictionary<string, object?>>? Instances = null);

public sealed class CompanionOptions
{
    public ProtocolDescriptor Protocol { get; init; } = new("bap", "0.1.0", "core");
    public string CompanionId { get; init; } = "org.bap.companion";
    public string CompanionName { get; init; } = "BAP Companion";
    public string CompanionVersion { get; init; } = "0.1.0";
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(8);
}
