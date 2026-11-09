using System.Text.Json;

namespace Bap.Companion.Core;

public sealed record RuntimePresentation(bool Enabled, bool UserVisible);

public sealed record RuntimeComponentInstance(
    string InstanceId,
    string ComponentId,
    string? SourceKey,
    string State,
    ComponentLayout? Layout,
    RuntimePresentation? Presentation,
    Dictionary<string, JsonElement> Config,
    long ConfigRevision);

public sealed record RuntimeHostSnapshot(
    string SessionId,
    string HostId,
    string HostName,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<RuntimeComponentInstance> Instances);

public sealed record RuntimeInstanceCommand(
    string InstanceId,
    Dictionary<string, JsonElement>? Config = null,
    long? ConfigRevision = null,
    ComponentLayout? Layout = null);

/// <summary>Volatile live Host inventory and Companion-originated runtime commands.</summary>
public sealed class RuntimeDesktopStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, RuntimeHostSnapshot> snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, RuntimeInstanceCommand>> commands = new(StringComparer.Ordinal);

    public void Report(RuntimeHostSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.SessionId) || string.IsNullOrWhiteSpace(snapshot.HostId) ||
            string.IsNullOrWhiteSpace(snapshot.HostName) || snapshot.Instances is null)
            throw new ArgumentException("Runtime snapshot identity and instances are required.", nameof(snapshot));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var instances = snapshot.Instances.Select(instance =>
        {
            if (string.IsNullOrWhiteSpace(instance.InstanceId) || !ids.Add(instance.InstanceId) ||
                string.IsNullOrWhiteSpace(instance.ComponentId) || string.IsNullOrWhiteSpace(instance.State) ||
                instance.ConfigRevision < 0 || instance.Config is null)
                throw new ArgumentException("Runtime snapshot contains an invalid or duplicate instance.", nameof(snapshot));
            return instance with
            {
                Config = instance.Config.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
                Layout = instance.Layout is null ? null : instance.Layout with { },
                Presentation = instance.Presentation is null ? null : instance.Presentation with { }
            };
        }).ToArray();

        lock (gate)
        {
            snapshots[snapshot.SessionId] = snapshot with { UpdatedAt = DateTimeOffset.UtcNow, Instances = instances };
            if (commands.TryGetValue(snapshot.SessionId, out var pending))
            {
                foreach (var instance in instances)
                    if (pending.TryGetValue(instance.InstanceId, out var command) &&
                        command.ConfigRevision is { } revision && instance.ConfigRevision >= revision)
                        pending.Remove(instance.InstanceId);
            }
        }
    }

    public IReadOnlyList<RuntimeHostSnapshot> Snapshot()
    {
        lock (gate) return snapshots.Values.OrderBy(item => item.HostName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.SessionId, StringComparer.Ordinal).Select(Clone).ToArray();
    }

    public bool TrySetConfig(string sessionId, string instanceId, Dictionary<string, JsonElement> config, long revision)
    {
        lock (gate)
        {
            if (!snapshots.TryGetValue(sessionId, out var snapshot) || revision < 1 ||
                snapshot.Instances.All(instance => instance.InstanceId != instanceId)) return false;
            var latestRevision = commands.GetValueOrDefault(sessionId)?.GetValueOrDefault(instanceId)?.ConfigRevision ??
                snapshot.Instances.First(instance => instance.InstanceId == instanceId).ConfigRevision;
            if (revision <= latestRevision) return false;
            var perSession = commands.GetValueOrDefault(sessionId);
            if (perSession is null) commands[sessionId] = perSession = new(StringComparer.Ordinal);
            perSession[instanceId] = new(instanceId,
                config.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal), revision);
            return true;
        }
    }

    public IReadOnlyList<RuntimeInstanceCommand> Commands(string sessionId)
    {
        lock (gate) return commands.GetValueOrDefault(sessionId)?.Values.Select(command => command with
        {
            Config = command.Config?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
        }).ToArray() ?? [];
    }

    public void RemoveSession(string sessionId)
    {
        lock (gate) { snapshots.Remove(sessionId); commands.Remove(sessionId); }
    }

    private static RuntimeHostSnapshot Clone(RuntimeHostSnapshot snapshot) => snapshot with
    {
        Instances = snapshot.Instances.Select(instance => instance with
        {
            Config = instance.Config.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
            Layout = instance.Layout is null ? null : instance.Layout with { },
            Presentation = instance.Presentation is null ? null : instance.Presentation with { }
        }).ToArray()
    };
}
