namespace Bap.Companion.Core;

public sealed record Session(string Id, string Token, DateTimeOffset ExpiresAt, string ClientKind);

public sealed record SessionRequest(string ClientName, string ClientKind);

public sealed record SessionResponse(string SessionId, string AccessToken, DateTimeOffset ExpiresAt);

public sealed record HostHello(string ProtocolVersion, string HostId, string? HostName, IReadOnlyCollection<string>? Capabilities);

public sealed record CompanionHello(string ProtocolVersion, string CompanionVersion, string SessionId, IReadOnlyCollection<string> Capabilities);

public sealed record ProtocolMessage(string Type, string? Id, object? Payload);

public sealed record ComponentManifest(
    string Id,
    string Version,
    string Name,
    string EntryPoint,
    IReadOnlyCollection<string> Modes,
    IReadOnlyCollection<string>? Capabilities = null,
    string? Source = null);

public sealed record ComponentDescriptor(ComponentManifest Manifest, string Location);

public sealed record CompanionConfig(
    Dictionary<string, object?> Global,
    Dictionary<string, Dictionary<string, object?>> Wallpapers);

public sealed class CompanionOptions
{
    public string ProtocolVersion { get; init; } = "0.1";
    public string CompanionVersion { get; init; } = "0.1.0";
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(8);
}
