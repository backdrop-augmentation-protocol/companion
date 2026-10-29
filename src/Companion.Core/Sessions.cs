using System.Security.Cryptography;

namespace Bap.Companion.Core;

public interface ISessionStore
{
    Session Create(string clientKind, TimeSpan lifetime);
    bool TryGet(string token, out Session session);
    bool BindHost(string token, string hostId);
    bool SetPermissions(string token, IReadOnlyCollection<string> permissions);
    void ClearPermissions(string token);
    int RevokeHost(string hostId);
}

public sealed class InMemorySessionStore : ISessionStore
{
    private readonly Dictionary<string, Session> sessions = [];
    private readonly Dictionary<string, CancellationTokenSource> revocations = [];
    private readonly Lock gate = new();

    public Session Create(string clientKind, TimeSpan lifetime)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var revocation = new CancellationTokenSource();
        var session = new Session(Guid.NewGuid().ToString("N"), token, DateTimeOffset.UtcNow.Add(lifetime), clientKind, [], RevocationToken: revocation.Token);
        lock (gate)
        {
            sessions[token] = session;
            revocations[token] = revocation;
        }
        return session;
    }

    public bool TryGet(string token, out Session session)
    {
        lock (gate)
        {
            if (sessions.TryGetValue(token, out session!) && session.ExpiresAt > DateTimeOffset.UtcNow) return true;
            session = null!;
            return false;
        }
    }

    public bool SetPermissions(string token, IReadOnlyCollection<string> permissions)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(token, out var session) || session.ExpiresAt <= DateTimeOffset.UtcNow) return false;
            sessions[token] = session with { GrantedPermissions = permissions.Distinct(StringComparer.Ordinal).ToArray() };
            return true;
        }
    }

    public bool BindHost(string token, string hostId)
    {
        if (string.IsNullOrWhiteSpace(hostId)) return false;
        lock (gate)
        {
            if (!sessions.TryGetValue(token, out var session) || session.ExpiresAt <= DateTimeOffset.UtcNow) return false;
            sessions[token] = session with { HostId = hostId };
            return true;
        }
    }

    public void ClearPermissions(string token)
    {
        lock (gate)
        {
            if (sessions.TryGetValue(token, out var session)) sessions[token] = session with { GrantedPermissions = [] };
        }
    }

    public int RevokeHost(string hostId)
    {
        lock (gate)
        {
            var tokens = sessions.Values
                .Where(session => string.Equals(session.HostId, hostId, StringComparison.Ordinal))
                .Select(session => session.Token)
                .ToArray();
            foreach (var token in tokens)
            {
                sessions.Remove(token);
                if (revocations.Remove(token, out var revocation))
                {
                    revocation.Cancel();
                    revocation.Dispose();
                }
            }
            return tokens.Length;
        }
    }
}
