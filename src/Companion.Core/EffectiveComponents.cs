namespace Bap.Companion.Core;

public sealed record EffectiveComponent(string ComponentId, string? SourceKey, string Name, string Status, string? EmbeddedPath);

public static class EffectiveComponents
{
    public static IReadOnlyList<EffectiveComponent> Resolve(PresetState state, string hostId, IReadOnlyList<InstalledItem> items)
    {
        var preset = PresetSelection.Resolve(state, hostId);
        var selections = preset?.Components ?? items.Where(item => item.Role == "component" && item.BapId is not null)
            .GroupBy(item => item.BapId!, StringComparer.Ordinal)
            .Select(group => new PresetComponent(group.Key)).ToArray();
        return selections.Select(selection =>
        {
            var matches = items.Where(item => item.Role == "component" && item.BapId == selection.ComponentId).ToArray();
            var item = selection.SourceKey is not null
                ? matches.FirstOrDefault(candidate => candidate.Key == selection.SourceKey)
                : matches.OrderBy(candidate => candidate.Status == "valid" ? 0 : 1)
                    .ThenBy(candidate => candidate.SourceKind == "workshop" ? 0 : 1)
                    .ThenBy(candidate => candidate.Key, StringComparer.Ordinal).FirstOrDefault();
            if (item is null) return new EffectiveComponent(selection.ComponentId, selection.SourceKey, selection.ComponentId, "missing", null);
            var embedded = item.ComponentManifest?.Entrypoints.GetValueOrDefault("embedded");
            return new EffectiveComponent(selection.ComponentId, item.Key, item.Title,
                embedded is null ? "unavailable" : "available", embedded);
        }).ToArray();
    }
}
