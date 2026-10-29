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
    private readonly Lock gate = new();

    public Session Create(string clientKind, TimeSpan lifetime)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var session = new Session(Guid.NewGuid().ToString("N"), token, DateTimeOffset.UtcNow.Add(lifetime), clientKind, []);
        lock (gate) sessions[token] = session;
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
            foreach (var token in tokens) sessions.Remove(token);
            return tokens.Length;
        }
    }
}
