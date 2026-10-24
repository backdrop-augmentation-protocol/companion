using System.Text.Json;

namespace Bap.Companion.Core;

public sealed record PresetComponent(string ComponentId, string? SourceKey = null);
public sealed record ComponentPreset(string Id, string Name, IReadOnlyList<PresetComponent> Components);
public sealed record PresetState(
    IReadOnlyList<ComponentPreset> Presets,
    string? GlobalPresetId,
    IReadOnlyDictionary<string, string> HostPresetIds);

public static class PresetSelection
{
    public static ComponentPreset? Resolve(PresetState state, string hostId)
    {
        var selected = state.HostPresetIds.TryGetValue(hostId, out var hostPresetId)
            ? hostPresetId : state.GlobalPresetId;
        return state.Presets.FirstOrDefault(preset => preset.Id == selected);
    }

    public static IReadOnlyList<string> Validate(PresetState state)
    {
        var errors = new List<string>();
        if (state.Presets is null || state.HostPresetIds is null) return ["presets and hostPresetIds are required"];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in state.Presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Id) || !ids.Add(preset.Id)) errors.Add("preset IDs must be nonempty and unique");
            if (string.IsNullOrWhiteSpace(preset.Name)) errors.Add("preset name is required");
            if (preset.Components is null) { errors.Add("preset components are required"); continue; }
            var components = new HashSet<string>(StringComparer.Ordinal);
            foreach (var component in preset.Components)
            {
                if (string.IsNullOrWhiteSpace(component.ComponentId)) errors.Add("component ID is required");
                if (!components.Add(component.ComponentId)) errors.Add("component IDs must be unique within a preset");
            }
        }
        if (state.GlobalPresetId is not null && !ids.Contains(state.GlobalPresetId)) errors.Add("global preset does not exist");
        foreach (var (hostId, presetId) in state.HostPresetIds)
            if (string.IsNullOrWhiteSpace(hostId) || !ids.Contains(presetId)) errors.Add("host binding refers to a missing preset");
        return errors;
    }
}

public sealed class JsonPresetStore(string path)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BAP Companion", "data", "presets.json");

    public async Task<PresetState> ReadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await ReadUnlockedAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task WriteAsync(PresetState state, CancellationToken cancellationToken = default)
    {
        var errors = PresetSelection.Validate(state);
        if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors), nameof(state));
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, Options), cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { gate.Release(); }
    }

    private async Task<PresetState> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new([], null, new Dictionary<string, string>());
        var state = JsonSerializer.Deserialize<PresetState>(await File.ReadAllTextAsync(path, cancellationToken), Options)
            ?? throw new InvalidDataException("Preset file is empty");
        var errors = PresetSelection.Validate(state);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("; ", errors));
        return state;
    }
}
