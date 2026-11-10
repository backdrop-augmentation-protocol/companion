using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bap.Companion.Core;

public sealed record EffectiveComponent(
    string InstanceId,
    string ComponentId,
    string? SourceKey,
    string Name,
    string Status,
    string? EmbeddedPath,
    bool Enabled,
    bool UserVisible,
    ComponentLayout Layout,
    Dictionary<string, JsonElement> Config,
    long ConfigRevision = 0);

public sealed record EffectiveComposition(
    string PresetId,
    long Revision,
    DateTimeOffset UpdatedAt,
    string LayoutMode,
    GridLayoutSettings? Grid,
    IReadOnlyList<EffectiveComponent> Instances);

public static class EffectiveComponents
{
    public static EffectiveComposition ResolveComposition(PresetState state, string hostId, IReadOnlyList<InstalledItem> items)
    {
        var preset = PresetSelection.Resolve(state, hostId);
        return new EffectiveComposition(
            preset?.Id ?? "unassigned",
            preset?.Revision ?? 0,
            preset?.UpdatedAt ?? DateTimeOffset.UnixEpoch,
            preset?.LayoutMode ?? "grid",
            preset is null || preset.LayoutMode == "grid" ? preset?.Grid ?? new GridLayoutSettings() : null,
            Resolve(state, hostId, items));
    }

    public static IReadOnlyList<EffectiveComponent> Resolve(PresetState state, string hostId, IReadOnlyList<InstalledItem> items)
    {
        var preset = PresetSelection.Resolve(state, hostId);
        var selections = preset?.Components ?? Array.Empty<PresetComponent>();
        var limits = items.Where(item => item.Role == "component" && item.BapId is not null && item.MaxInstances is not null)
            .GroupBy(item => item.BapId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Min(item => item.MaxInstances!.Value), StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        return selections.Select((selection, index) =>
        {
            var occurrence = occurrences.GetValueOrDefault(selection.ComponentId);
            occurrences[selection.ComponentId] = occurrence + 1;
            var exceedsLimit = limits.TryGetValue(selection.ComponentId, out var limit) && occurrence >= limit;
            var instanceId = selection.InstanceId ?? DefaultInstanceId(selection.ComponentId, selection.SourceKey);
            var layout = selection.Layout ?? new ComponentLayout("automatic", Order: index, ZIndex: index);
            var config = selection.Config ?? [];
            var configRevision = preset?.Revision ?? 0;
            var matches = items.Where(item => item.Role == "component" && item.BapId == selection.ComponentId).ToArray();
            var item = selection.SourceKey is not null
                ? matches.FirstOrDefault(candidate => candidate.Key == selection.SourceKey)
                : matches.OrderBy(candidate => candidate.Status == "valid" ? 0 : 1)
                    .ThenBy(candidate => candidate.SourceKind == "workshop" ? 0 : 1)
                    .ThenBy(candidate => candidate.Key, StringComparer.Ordinal).FirstOrDefault();
            if (item is null) return new EffectiveComponent(instanceId, selection.ComponentId, selection.SourceKey, selection.ComponentId,
                selection.Enabled ? "missing" : "disabled", null, selection.Enabled, selection.UserVisible, layout, config, configRevision);
            var embedded = item.ComponentManifest?.Entrypoints.GetValueOrDefault("embedded");
            var status = exceedsLimit ? "unavailable" : !selection.Enabled ? "disabled" : embedded is null ? "unavailable" : "available";
            return new EffectiveComponent(instanceId, selection.ComponentId, item.Key, item.Title, status, embedded,
                selection.Enabled, selection.UserVisible, layout, ResolveConfig(item, selection.Config), configRevision);
        }).ToArray();
    }

    private static Dictionary<string, JsonElement> ResolveConfig(InstalledItem item, Dictionary<string, JsonElement>? overrides)
    {
        var effective = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var definition in item.Properties ?? [])
        {
            var value = overrides is not null && overrides.TryGetValue(definition.Key, out var configured) &&
                WallpaperPropertyReader.IsValidValue(definition, configured) ? configured : definition.DefaultValue;
            effective[definition.Key] = value.Clone();
        }
        return effective;
    }

    private static string DefaultInstanceId(string componentId, string? sourceKey)
    {
        var input = Encoding.UTF8.GetBytes($"{componentId}\n{sourceKey ?? "default"}");
        return "default-" + Convert.ToHexString(SHA256.HashData(input))[..24].ToLowerInvariant();
    }
}
