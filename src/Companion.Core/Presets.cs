using System.Text.Json;

namespace Bap.Companion.Core;

public sealed record ComponentLayout(
    string Mode,
    string? Anchor = null,
    double? X = null,
    double? Y = null,
    double? Width = null,
    double? Height = null,
    int? Order = null,
    double Scale = 1,
    double Rotation = 0,
    double Opacity = 1,
    int ZIndex = 0,
    bool Locked = false);

public sealed record GridLayoutSettings(int Columns = 2, double GapX = 0.02, double GapY = 0.02, double PaddingX = 0.04, double PaddingY = 0.04);
public sealed record PresetComponent(
    string ComponentId,
    string? SourceKey = null,
    string? InstanceId = null,
    bool Enabled = true,
    bool UserVisible = true,
    ComponentLayout? Layout = null,
    Dictionary<string, JsonElement>? Config = null);
public sealed record ComponentPreset(string Id, string Name, IReadOnlyList<PresetComponent> Components);
public sealed record PresetState(
    IReadOnlyList<ComponentPreset> Presets,
    string? GlobalPresetId,
    IReadOnlyDictionary<string, string> HostPresetIds,
    string LayoutMode = "grid",
    GridLayoutSettings? Grid = null);

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
            var instances = new HashSet<string>(StringComparer.Ordinal);
            foreach (var component in preset.Components)
            {
                if (string.IsNullOrWhiteSpace(component.ComponentId)) errors.Add("component ID is required");
                if (string.IsNullOrWhiteSpace(component.InstanceId) || !instances.Add(component.InstanceId)) errors.Add("instance IDs must be nonempty and unique within a preset");
                if (component.Layout is null || !ValidateLayout(component.Layout)) errors.Add("component layout is invalid");
            }
        }
        if (state.LayoutMode is not ("manual" or "grid")) errors.Add("layout mode must be manual or grid");
        if (state.LayoutMode == "grid" && (state.Grid is null || state.Grid.Columns is < 1 or > 32 || !InRange(state.Grid.GapX, 0, 0.25) || !InRange(state.Grid.GapY, 0, 0.25) || !InRange(state.Grid.PaddingX, 0, 0.5) || !InRange(state.Grid.PaddingY, 0, 0.5))) errors.Add("grid settings are invalid");
        if (state.GlobalPresetId is not null && !ids.Contains(state.GlobalPresetId)) errors.Add("global preset does not exist");
        foreach (var (hostId, presetId) in state.HostPresetIds)
            if (string.IsNullOrWhiteSpace(hostId) || !ids.Contains(presetId)) errors.Add("host binding refers to a missing preset");
        return errors;
    }

    private static bool ValidateLayout(ComponentLayout layout) => layout.Mode switch
    {
        "automatic" => layout.Order is >= 0 and <= 10000 && InRange(layout.Scale, 0.1, 5) && InRange(layout.Rotation, -360, 360) && InRange(layout.Opacity, 0, 1) && layout.ZIndex is >= -100000 and <= 100000,
        "manual" => layout.Anchor is "top-left" or "top" or "top-right" or "left" or "center" or "right" or "bottom-left" or "bottom" or "bottom-right" &&
            layout.X is not null && InRange(layout.X.Value, -2, 2) && layout.Y is not null && InRange(layout.Y.Value, -2, 2) &&
            layout.Width is > 0 and <= 2 && layout.Height is > 0 and <= 2 && InRange(layout.Scale, 0.1, 5) &&
            InRange(layout.Rotation, -360, 360) && InRange(layout.Opacity, 0, 1) && layout.ZIndex is >= -100000 and <= 100000,
        _ => false
    };

    private static bool InRange(double value, double minimum, double maximum) => double.IsFinite(value) && value >= minimum && value <= maximum;
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
        state = Normalize(state);
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
        var original = await File.ReadAllTextAsync(path, cancellationToken);
        var state = JsonSerializer.Deserialize<PresetState>(original, Options)
            ?? throw new InvalidDataException("Preset file is empty");
        var normalized = Normalize(state);
        var migrated = !ReferenceEquals(state, normalized) && HasLegacyInstances(state);
        state = normalized;
        var errors = PresetSelection.Validate(state);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("; ", errors));
        if (migrated)
        {
            var backup = path + ".pre-instance-migration-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + ".bak";
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            await File.WriteAllTextAsync(backup, original, cancellationToken);
            await WriteUnlockedAsync(state, cancellationToken);
        }
        return state;
    }

    private static bool HasLegacyInstances(PresetState state) => state.Grid is null || state.Presets.Any(preset => preset.Components.Any(component => component.InstanceId is null || component.Layout is null || component.Config is null));

    private static PresetState Normalize(PresetState state)
    {
        var presets = state.Presets.Select(preset => preset with
        {
            Components = preset.Components.Select((component, index) => component with
            {
                InstanceId = string.IsNullOrWhiteSpace(component.InstanceId) ? Guid.NewGuid().ToString("N") : component.InstanceId,
                Layout = component.Layout ?? new ComponentLayout("automatic", Order: index, ZIndex: index),
                Config = component.Config ?? []
            }).ToArray()
        }).ToArray();
        var mode = state.LayoutMode is "manual" or "grid" ? state.LayoutMode : "grid";
        return state with { Presets = presets, LayoutMode = mode, Grid = mode == "grid" ? state.Grid ?? new GridLayoutSettings() : state.Grid };
    }

    private async Task WriteUnlockedAsync(PresetState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, Options), cancellationToken);
        File.Move(temporary, path, true);
    }
}
