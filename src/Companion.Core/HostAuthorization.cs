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
    IReadOnlyCollection<string>? Permissions = null);

public sealed record AuthorizationControlResponse(
    bool Success,
    IReadOnlyList<HostAuthorizationRequest> Requests,
    string? Error = null);

/// <summary>
/// Holds explicit, session-scoped human decisions in memory. It does not persist trust.
/// </summary>
public sealed class HostAuthorizationQueue
{
    private static readonly HashSet<string> SupportedPermissions = new(StringComparer.Ordinal)
    {
        "component.discovery", "config.read", "config.write"
    };

    private readonly object gate = new();
    private readonly Dictionary<string, PendingDecision> pending = new(StringComparer.Ordinal);

    public IReadOnlyList<HostAuthorizationRequest> Snapshot()
    {
        lock (gate) return pending.Values.Select(item => item.Request).ToArray();
    }

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

    public bool Approve(string sessionId, IEnumerable<string> permissions) =>
        Resolve(sessionId, approved: true, permissions);

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

    private bool Resolve(string sessionId, bool approved, IEnumerable<string> permissions)
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
        item.Completion.TrySetResult(new(approved, granted));
        return true;
    }

    private sealed record PendingDecision(
        HostAuthorizationRequest Request,
        TaskCompletionSource<HostAuthorizationDecision> Completion);
}
