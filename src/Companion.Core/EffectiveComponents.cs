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
    Dictionary<string, JsonElement> Config);

public static class EffectiveComponents
{
    public static IReadOnlyList<EffectiveComponent> Resolve(PresetState state, string hostId, IReadOnlyList<InstalledItem> items)
    {
        var preset = PresetSelection.Resolve(state, hostId);
        var selections = preset?.Components ?? DefaultSelections(items);
        return selections.Select((selection, index) =>
        {
            var instanceId = selection.InstanceId ?? DefaultInstanceId(selection.ComponentId, selection.SourceKey);
            var layout = selection.Layout ?? new ComponentLayout("automatic", Order: index, ZIndex: index);
            var config = selection.Config ?? [];
            var matches = items.Where(item => item.Role == "component" && item.BapId == selection.ComponentId).ToArray();
            var item = selection.SourceKey is not null
                ? matches.FirstOrDefault(candidate => candidate.Key == selection.SourceKey)
                : matches.OrderBy(candidate => candidate.Status == "valid" ? 0 : 1)
                    .ThenBy(candidate => candidate.SourceKind == "workshop" ? 0 : 1)
                    .ThenBy(candidate => candidate.Key, StringComparer.Ordinal).FirstOrDefault();
            if (item is null) return new EffectiveComponent(instanceId, selection.ComponentId, selection.SourceKey, selection.ComponentId,
                selection.Enabled ? "missing" : "disabled", null, selection.Enabled, selection.UserVisible, layout, config);
            var embedded = item.ComponentManifest?.Entrypoints.GetValueOrDefault("embedded");
            var status = !selection.Enabled ? "disabled" : embedded is null ? "unavailable" : "available";
            return new EffectiveComponent(instanceId, selection.ComponentId, item.Key, item.Title, status, embedded,
                selection.Enabled, selection.UserVisible, layout, config);
        }).ToArray();
    }

    private static IReadOnlyList<PresetComponent> DefaultSelections(IReadOnlyList<InstalledItem> items) => items
        .Where(item => item.Role == "component" && item.BapId is not null)
        .GroupBy(item => item.BapId!, StringComparer.Ordinal)
        .Select((group, index) =>
        {
            var selected = group.OrderBy(candidate => candidate.Status == "valid" ? 0 : 1)
                .ThenBy(candidate => candidate.SourceKind == "workshop" ? 0 : 1)
                .ThenBy(candidate => candidate.Key, StringComparer.Ordinal).First();
            return new PresetComponent(group.Key, selected.Key, DefaultInstanceId(group.Key, selected.Key),
                Layout: new ComponentLayout("automatic", Order: index, ZIndex: index), Config: []);
        }).ToArray();

    private static string DefaultInstanceId(string componentId, string? sourceKey)
    {
        var input = Encoding.UTF8.GetBytes($"{componentId}\n{sourceKey ?? "default"}");
        return "default-" + Convert.ToHexString(SHA256.HashData(input))[..24].ToLowerInvariant();
    }
}
