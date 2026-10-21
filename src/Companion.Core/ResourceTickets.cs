using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Bap.Companion.Core;

public sealed class ResourceTickets
{
    private readonly ConcurrentDictionary<string, (string ComponentId, DateTimeOffset ExpiresAt)> tickets = new();

    public string Create(string componentId, TimeSpan lifetime)
    {
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        tickets[ticket] = (componentId, DateTimeOffset.UtcNow.Add(lifetime));
        return ticket;
    }

    public bool Accepts(string ticket, string componentId) =>
        tickets.TryGetValue(ticket, out var entry) &&
        entry.ExpiresAt > DateTimeOffset.UtcNow &&
        string.Equals(entry.ComponentId, componentId, StringComparison.Ordinal);
}
