namespace Bap.Companion.Core;

/// <summary>
/// Untrusted information reported by a Host while requesting a Companion session.
/// These fields are for display and must not be treated as proof of installation source.
/// </summary>
public sealed record HostAuthorizationRequest(
    string SessionId,
    Identity ClaimedIdentity,
    IReadOnlyCollection<string> ClaimedCapabilities,
    IReadOnlyCollection<string> RequestedPermissions,
    DateTimeOffset RequestedAt);

public sealed record HostAuthorizationDecision(bool Approved, IReadOnlyCollection<string> GrantedPermissions);

public sealed record AuthorizationControlRequest(
    string Operation,
    string? SessionId = null,
    IReadOnlyCollection<string>? Permissions = null,
    bool Remember = false);

public sealed record AuthorizationControlResponse(
    bool Success,
    IReadOnlyList<HostAuthorizationRequest> Requests,
    string? Error = null,
    IReadOnlyList<TrustedHost>? TrustedHosts = null);

/// <summary>
/// Holds explicit human decisions and optional per-user records for self-reported Host IDs.
/// </summary>
public sealed class HostAuthorizationQueue(TrustedHostStore? trustedHosts = null)
{
    private static readonly HashSet<string> SupportedPermissions = new(StringComparer.Ordinal)
    {
        "component.discovery", "config.read", "config.write"
    };

    private readonly object gate = new();
    private readonly Dictionary<string, PendingDecision> pending = new(StringComparer.Ordinal);
    private readonly TrustedHostStore trustStore = trustedHosts ?? new TrustedHostStore(TrustedHostStore.DefaultPath);

    public IReadOnlyList<HostAuthorizationRequest> Snapshot()
    {
        lock (gate) return pending.Values.Select(item => item.Request).ToArray();
    }

    public IReadOnlyList<TrustedHost> TrustedHosts() => trustStore.List();
    public bool RevokeTrust(string id) => trustStore.Remove(id);

    public bool TryEnqueue(HostAuthorizationRequest request, out Task<HostAuthorizationDecision> decision)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SessionId)) throw new ArgumentException("A session ID is required.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.ClaimedIdentity);
        ArgumentNullException.ThrowIfNull(request.ClaimedCapabilities);
        ArgumentNullException.ThrowIfNull(request.RequestedPermissions);

        lock (gate)
        {
            if (pending.TryGetValue(request.SessionId, out var existing))
            {
                decision = existing.Completion.Task;
                return false;
            }

            var item = new PendingDecision(request, new(TaskCreationOptions.RunContinuationsAsynchronously));
            pending.Add(request.SessionId, item);
            decision = item.Completion.Task;
            return true;
        }
    }

    public bool Approve(string sessionId, IEnumerable<string> permissions, bool remember = false) =>
        Resolve(sessionId, approved: true, permissions, remember);

    public bool Deny(string sessionId) => Resolve(sessionId, approved: false, []);

    public bool Cancel(string sessionId)
    {
        PendingDecision? item;
        lock (gate)
        {
            if (!pending.Remove(sessionId, out item)) return false;
        }
        item.Completion.TrySetCanceled();
        return true;
    }

    private bool Resolve(string sessionId, bool approved, IEnumerable<string> permissions, bool remember = false)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        PendingDecision? item;
        lock (gate)
        {
            if (!pending.Remove(sessionId, out item)) return false;
        }

        var requested = new HashSet<string>(item.Request.RequestedPermissions, StringComparer.Ordinal);
        var granted = approved
            ? permissions.Where(permission => SupportedPermissions.Contains(permission) && requested.Contains(permission))
                .Distinct(StringComparer.Ordinal).ToArray()
            : [];
        if (approved && remember && granted.Length > 0)
            trustStore.Remember(item.Request.ClaimedIdentity, granted);
        item.Completion.TrySetResult(new(approved, granted));
        return true;
    }

    private sealed record PendingDecision(
        HostAuthorizationRequest Request,
        TaskCompletionSource<HostAuthorizationDecision> Completion);
}

public sealed record TrustedHost(string Id, string Name, string Version, IReadOnlyCollection<string> Permissions, DateTimeOffset ApprovedAt);

/// <summary>Per-Windows-user records for Host IDs and permissions previously approved by a person. IDs remain self-asserted and records never authorize a new session.</summary>
public sealed class TrustedHostStore(string path)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BAP Companion", "trusted-hosts.json");

    private readonly object gate = new();

    public TrustedHost? Find(string id)
    {
        lock (gate) return Read().FirstOrDefault(host => string.Equals(host.Id, id, StringComparison.Ordinal));
    }

    public IReadOnlyList<TrustedHost> List()
    {
        lock (gate) return Read();
    }

    public void Remember(Identity identity, IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var allowed = permissions.Distinct(StringComparer.Ordinal).ToArray();
        lock (gate)
        {
            var hosts = Read().Where(host => !string.Equals(host.Id, identity.Id, StringComparison.Ordinal)).ToList();
            hosts.Add(new(identity.Id, identity.Name, identity.Version, allowed, DateTimeOffset.UtcNow));
            Write(hosts);
        }
    }

    public bool Remove(string id)
    {
        lock (gate)
        {
            var hosts = Read().Where(host => !string.Equals(host.Id, id, StringComparison.Ordinal)).ToArray();
            if (hosts.Length == Read().Count) return false;
            Write(hosts);
            return true;
        }
    }

    private IReadOnlyList<TrustedHost> Read()
    {
        if (!File.Exists(path)) return [];
        try
        {
            return (System.Text.Json.JsonSerializer.Deserialize<List<TrustedHost>>(File.ReadAllText(path)) ?? [])
                .Where(host => !string.IsNullOrWhiteSpace(host.Id) && host.Permissions.All(permission =>
                    permission is "component.discovery" or "config.read" or "config.write"))
                .ToArray();
        }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private void Write(IEnumerable<TrustedHost> hosts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(hosts));
        File.Move(temp, path, overwrite: true);
    }
}
