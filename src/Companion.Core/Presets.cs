using System.Text.Json;
using System.Text.Json.Serialization;

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
public sealed record ComponentPreset(
    string Id,
    string Name,
    IReadOnlyList<PresetComponent> Components,
    string LayoutMode = "grid",
    GridLayoutSettings? Grid = null,
    long Revision = 0,
    DateTimeOffset? UpdatedAt = null,
    bool Enabled = true);
public sealed record PresetState(
    IReadOnlyList<ComponentPreset> Presets,
    string? GlobalPresetId,
    [property: JsonConverter(typeof(HostPresetIdsJsonConverter))] IReadOnlyDictionary<string, IReadOnlyList<string>> HostPresetIds,
    IReadOnlyDictionary<string, Dictionary<string, JsonElement>>? HostConfigs = null);

public sealed class HostPresetIdsJsonConverter : JsonConverter<IReadOnlyDictionary<string, IReadOnlyList<string>>>
{
    public override IReadOnlyDictionary<string, IReadOnlyList<string>> Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("hostPresetIds must be an object.");
        var bindings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var ids = property.Value.ValueKind switch
            {
                JsonValueKind.String => new[] { property.Value.GetString()! },
                JsonValueKind.Array => property.Value.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                    ? value.GetString()! : throw new JsonException("hostPresetIds values must be preset ID strings." )).ToArray(),
                _ => throw new JsonException("hostPresetIds values must be a preset ID or an array of preset IDs.")
            };
            if (ids.Any(string.IsNullOrWhiteSpace)) throw new JsonException("hostPresetIds values must not contain empty preset IDs.");
            bindings[property.Name] = ids;
        }
        return bindings;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, IReadOnlyList<string>> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (hostKey, presetIds) in value)
        {
            writer.WritePropertyName(hostKey);
            JsonSerializer.Serialize(writer, presetIds, options);
        }
        writer.WriteEndObject();
    }
}

public static class PresetSelection
{
    public static bool HostBindingsEqual(PresetState left, PresetState right) =>
        left.HostPresetIds.Count == right.HostPresetIds.Count && left.HostPresetIds.All(binding =>
            right.HostPresetIds.TryGetValue(binding.Key, out var values) && binding.Value.SequenceEqual(values, StringComparer.Ordinal));

    public static PresetState CreatePreset(PresetState state, string presetId, string name)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(presetId)) throw new ArgumentException("A preset ID is required.", nameof(presetId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A preset name is required.", nameof(name));
        if (state.Presets.Any(preset => preset.Id == presetId)) throw new InvalidOperationException($"Preset already exists: {presetId}");
        var preset = new ComponentPreset(presetId, name.Trim(), [], Grid: new GridLayoutSettings());
        return state with { Presets = [.. state.Presets, preset] };
    }

    public static PresetState DeletePreset(PresetState state, string presetId)
    {
        ArgumentNullException.ThrowIfNull(state);
        var preset = state.Presets.FirstOrDefault(candidate => candidate.Id == presetId)
            ?? throw new KeyNotFoundException($"Preset was not found: {presetId}");
        return state with
        {
            Presets = state.Presets.Where(candidate => candidate.Id != preset.Id).ToArray(),
            GlobalPresetId = state.GlobalPresetId == preset.Id ? null : state.GlobalPresetId,
            HostPresetIds = state.HostPresetIds.ToDictionary(binding => binding.Key,
                binding => (IReadOnlyList<string>)binding.Value.Where(id => id != preset.Id).ToArray(), StringComparer.Ordinal)
                .Where(binding => binding.Value.Count > 0).ToDictionary(binding => binding.Key, binding => binding.Value, StringComparer.Ordinal)
        };
    }

    public static PresetState AddComponent(PresetState state, string presetId, InstalledItem item, string instanceId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("A component instance ID is required.", nameof(instanceId));
        if (item.Role != "component" || item.Status != "valid" || string.IsNullOrWhiteSpace(item.BapId))
            throw new InvalidOperationException("The selected catalog item is not a valid BAP component.");
        var presetIndex = state.Presets.ToList().FindIndex(preset => preset.Id == presetId);
        if (presetIndex < 0) throw new KeyNotFoundException($"Preset was not found: {presetId}");
        var preset = state.Presets[presetIndex];
        if (preset.Components.Any(component => component.InstanceId == instanceId))
            throw new InvalidOperationException($"Component instance ID already exists in this preset: {instanceId}");

        var order = preset.Components.Count;
        var component = new PresetComponent(item.BapId, item.Key, instanceId, Layout: new ComponentLayout("automatic", Order: order, ZIndex: order));
        var updatedPreset = preset with { Components = [.. preset.Components, component] };
        if (item.MaxInstances is int maxInstances && updatedPreset.Components.Count(candidate => candidate.ComponentId == item.BapId) > maxInstances)
            throw new InvalidOperationException($"Component '{item.BapId}' exceeds its maximum instance count of {maxInstances}.");
        return ReplacePreset(state, presetIndex, updatedPreset);
    }

    public static PresetState RemoveComponent(PresetState state, string presetId, string instanceId)
    {
        ArgumentNullException.ThrowIfNull(state);
        var presetIndex = state.Presets.ToList().FindIndex(preset => preset.Id == presetId);
        if (presetIndex < 0) throw new KeyNotFoundException($"Preset was not found: {presetId}");
        var preset = state.Presets[presetIndex];
        if (!preset.Components.Any(component => component.InstanceId == instanceId))
            throw new KeyNotFoundException($"Component instance '{instanceId}' was not found in preset '{presetId}'.");
        return ReplacePreset(state, presetIndex, preset with
        {
            Components = preset.Components.Where(component => component.InstanceId != instanceId).ToArray()
        });
    }

    public static PresetState MoveComponent(PresetState state, string presetId, string instanceId, int destinationIndex)
    {
        ArgumentNullException.ThrowIfNull(state);
        var presetIndex = state.Presets.ToList().FindIndex(preset => preset.Id == presetId);
        if (presetIndex < 0) throw new KeyNotFoundException($"Preset was not found: {presetId}");
        var preset = state.Presets[presetIndex];
        var components = preset.Components.ToList();
        var sourceIndex = components.FindIndex(component => component.InstanceId == instanceId);
        if (sourceIndex < 0) throw new KeyNotFoundException($"Component instance '{instanceId}' was not found in preset '{presetId}'.");
        if (destinationIndex < 0 || destinationIndex >= components.Count)
            throw new ArgumentOutOfRangeException(nameof(destinationIndex), "Destination index must identify an existing position in the preset.");
        if (sourceIndex == destinationIndex) return state;
        var moving = components[sourceIndex];
        components.RemoveAt(sourceIndex);
        components.Insert(destinationIndex, moving);
        var reordered = components.Select((component, index) => component with
        {
            Layout = component.Layout is { Mode: "automatic" } layout
                ? layout with { Order = index, ZIndex = index }
                : component.Layout
        }).ToArray();
        return ReplacePreset(state, presetIndex, preset with { Components = reordered });
    }

    public static PresetState SetGlobalPreset(PresetState state, string? presetId)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (presetId is not null && !state.Presets.Any(preset => preset.Id == presetId))
            throw new KeyNotFoundException($"Preset was not found: {presetId}");
        return state with { GlobalPresetId = presetId };
    }

    public static PresetState AddHostToPreset(PresetState state, string presetId, string hostKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(hostKey)) throw new ArgumentException("A Host catalog key is required.", nameof(hostKey));
        if (!state.Presets.Any(preset => preset.Id == presetId)) throw new KeyNotFoundException($"Preset was not found: {presetId}");
        var bindings = CopyHostPresetIds(state);
        var assigned = bindings.GetValueOrDefault(hostKey, []);
        if (assigned.Contains(presetId, StringComparer.Ordinal)) return state;
        bindings[hostKey] = [.. assigned, presetId];
        return state with { HostPresetIds = bindings };
    }

    public static PresetState RemoveHostFromPreset(PresetState state, string presetId, string hostKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.HostPresetIds.TryGetValue(hostKey, out var assigned) || !assigned.Contains(presetId, StringComparer.Ordinal))
            throw new KeyNotFoundException($"Host '{hostKey}' is not included in preset '{presetId}'.");
        var bindings = CopyHostPresetIds(state);
        var remaining = assigned.Where(id => id != presetId).ToArray();
        if (remaining.Length == 0) bindings.Remove(hostKey);
        else bindings[hostKey] = remaining;
        return state with { HostPresetIds = bindings };
    }

    public static PresetState SetPresetEnabled(PresetState state, string presetId, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(state);
        var index = state.Presets.ToList().FindIndex(preset => preset.Id == presetId);
        if (index < 0) throw new KeyNotFoundException($"Preset was not found: {presetId}");
        if (state.Presets[index].Enabled == enabled) return state;
        var presets = state.Presets.ToArray();
        presets[index] = presets[index] with { Enabled = enabled };
        return state with { Presets = presets };
    }

    public static PresetState MovePreset(PresetState state, string presetId, int destinationIndex)
    {
        ArgumentNullException.ThrowIfNull(state);
        var presets = state.Presets.ToList();
        var sourceIndex = presets.FindIndex(preset => preset.Id == presetId);
        if (sourceIndex < 0) throw new KeyNotFoundException($"Preset was not found: {presetId}");
        if (destinationIndex < 0 || destinationIndex >= presets.Count)
            throw new ArgumentOutOfRangeException(nameof(destinationIndex), "Destination index must identify an existing preset position.");
        if (sourceIndex == destinationIndex) return state;
        var moving = presets[sourceIndex];
        presets.RemoveAt(sourceIndex);
        presets.Insert(destinationIndex, moving);
        return state with { Presets = presets };
    }

    private static Dictionary<string, IReadOnlyList<string>> CopyHostPresetIds(PresetState state) =>
        state.HostPresetIds.ToDictionary(binding => binding.Key,
            binding => (IReadOnlyList<string>)binding.Value.ToArray(), StringComparer.Ordinal);

    private static PresetState ReplacePreset(PresetState state, int presetIndex, ComponentPreset preset)
    {
        var presets = state.Presets.ToArray();
        presets[presetIndex] = preset;
        return state with { Presets = presets };
    }

    public static PresetState SetComponentEnabled(PresetState state, string presetId, string instanceId, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(presetId)) throw new ArgumentException("A preset ID is required.", nameof(presetId));
        if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("A component instance ID is required.", nameof(instanceId));
        var presetIndex = state.Presets.ToList().FindIndex(preset => preset.Id == presetId);
        if (presetIndex < 0) throw new KeyNotFoundException($"Preset was not found: {presetId}");
        var preset = state.Presets[presetIndex];
        var instanceIndex = preset.Components.ToList().FindIndex(component => component.InstanceId == instanceId);
        if (instanceIndex < 0) throw new KeyNotFoundException($"Component instance '{instanceId}' was not found in preset '{presetId}'.");
        if (preset.Components[instanceIndex].Enabled == enabled) return state;

        var components = preset.Components.ToArray();
        components[instanceIndex] = components[instanceIndex] with { Enabled = enabled };
        var presets = state.Presets.ToArray();
        presets[presetIndex] = preset with { Components = components };
        return state with { Presets = presets };
    }

    public static IReadOnlyList<string> ValidateInstanceLimits(ComponentPreset preset, IReadOnlyList<InstalledItem> items)
    {
        var errors = new List<string>();
        foreach (var group in preset.Components.GroupBy(component => component.ComponentId, StringComparer.Ordinal))
        {
            var limit = items.Where(item => item.Role == "component" && item.BapId == group.Key && item.MaxInstances is not null)
                .Select(item => item.MaxInstances!.Value).DefaultIfEmpty(int.MaxValue).Min();
            if (group.Count() > limit) errors.Add($"component '{group.Key}' exceeds its maximum instance count of {limit}");
        }
        return errors;
    }

    public static IReadOnlyList<string> ValidateInstanceLimitIncreases(PresetState current, PresetState next, IReadOnlyList<InstalledItem> items)
    {
        var errors = new List<string>();
        foreach (var preset in next.Presets)
        {
            var previous = current.Presets.FirstOrDefault(candidate => candidate.Id == preset.Id);
            var previousCounts = previous?.Components.GroupBy(component => component.ComponentId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal) ?? new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var group in preset.Components.GroupBy(component => component.ComponentId, StringComparer.Ordinal))
            {
                var limit = items.Where(item => item.Role == "component" && item.BapId == group.Key && item.MaxInstances is not null)
                    .Select(item => item.MaxInstances!.Value).DefaultIfEmpty(int.MaxValue).Min();
                if (group.Count() > limit && group.Count() > previousCounts.GetValueOrDefault(group.Key))
                    errors.Add($"component '{group.Key}' exceeds its maximum instance count of {limit}");
            }
        }
        return errors;
    }

    public static ComponentPreset? Resolve(PresetState state, string hostKey)
    {
        state.HostPresetIds.TryGetValue(hostKey, out var assigned);
        return state.Presets.FirstOrDefault(preset => preset.Enabled &&
            (preset.Id == state.GlobalPresetId || assigned?.Contains(preset.Id, StringComparer.Ordinal) == true));
    }

    public static PresetState MigrateInstalledHostBindings(PresetState state, IReadOnlyList<InstalledItem> items)
    {
        var bindings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (key, presetIds) in state.HostPresetIds)
        {
            if (items.Any(item => item.Role == "host" && item.Key == key))
            {
                bindings[key] = presetIds;
                continue;
            }
            var matches = items.Where(item => item.Role == "host" && item.BapId == key).ToArray();
            if (matches.Length == 0) bindings[key] = presetIds;
            else foreach (var item in matches)
            {
                var existing = bindings.GetValueOrDefault(item.Key, []);
                bindings[item.Key] = existing.Concat(presetIds).Distinct(StringComparer.Ordinal).ToArray();
            }
        }
        var existingConfigs = state.HostConfigs ?? new Dictionary<string, Dictionary<string, JsonElement>>();
        var configs = new Dictionary<string, Dictionary<string, JsonElement>>(existingConfigs, StringComparer.Ordinal);
        foreach (var (oldKey, _) in existingConfigs)
        {
            if (items.Any(item => item.Role == "host" && item.Key == oldKey)) continue;
            var matches = items.Where(item => item.Role == "host" && item.BapId == oldKey).ToArray();
            if (matches.Length == 1)
            {
                configs.TryAdd(matches[0].Key, configs[oldKey]);
                configs.Remove(oldKey);
            }
        }
        return state with { HostPresetIds = bindings, HostConfigs = configs };
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
            if (preset.LayoutMode is not ("manual" or "grid")) errors.Add("preset layout mode must be manual or grid");
            if (preset.LayoutMode == "grid" && (preset.Grid is null || preset.Grid.Columns is < 1 or > 32 || !InRange(preset.Grid.GapX, 0, 0.25) || !InRange(preset.Grid.GapY, 0, 0.25) || !InRange(preset.Grid.PaddingX, 0, 0.5) || !InRange(preset.Grid.PaddingY, 0, 0.5))) errors.Add("preset grid settings are invalid");
            if (preset.Components is null) { errors.Add("preset components are required"); continue; }
            var instances = new HashSet<string>(StringComparer.Ordinal);
            foreach (var component in preset.Components)
            {
                if (string.IsNullOrWhiteSpace(component.ComponentId)) errors.Add("component ID is required");
                if (string.IsNullOrWhiteSpace(component.InstanceId) || !instances.Add(component.InstanceId)) errors.Add("instance IDs must be nonempty and unique within a preset");
                if (component.Layout is null || !ValidateLayout(component.Layout)) errors.Add("component layout is invalid");
            }
        }
        if (state.GlobalPresetId is not null && !ids.Contains(state.GlobalPresetId)) errors.Add("global preset does not exist");
        foreach (var (hostId, presetIds) in state.HostPresetIds)
        {
            if (string.IsNullOrWhiteSpace(hostId) || presetIds is null || presetIds.Count == 0 ||
                presetIds.Any(presetId => !ids.Contains(presetId)) || presetIds.Distinct(StringComparer.Ordinal).Count() != presetIds.Count)
                errors.Add("host binding refers to a missing or duplicate preset");
        }
        return errors;
    }

    internal static bool ValidateLayout(ComponentLayout layout) => layout.Mode switch
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
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadUnlockedAsync(cancellationToken);
            state = ApplyRevisions(current, state);
            var errors = PresetSelection.Validate(state);
            if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors), nameof(state));
            await WriteUnlockedAsync(state, cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task<PresetState> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new([], null, new Dictionary<string, IReadOnlyList<string>>());
        var original = await File.ReadAllTextAsync(path, cancellationToken);
        var state = JsonSerializer.Deserialize<PresetState>(original, Options)
            ?? throw new InvalidDataException("Preset file is empty");
        var normalized = Normalize(state);
        var migrated = HasLegacyInstances(state) || HasScalarHostBindings(original);
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

    private static bool HasLegacyInstances(PresetState state) => state.Presets.Any(preset =>
        (preset.LayoutMode == "grid" && preset.Grid is null) || preset.UpdatedAt is null ||
        preset.Components.Any(component => component.InstanceId is null || component.Layout is null || component.Config is null));

    private static bool HasScalarHostBindings(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("hostPresetIds", out var bindings) && bindings.ValueKind == JsonValueKind.Object &&
            bindings.EnumerateObject().Any(binding => binding.Value.ValueKind == JsonValueKind.String);
    }

    private static PresetState Normalize(PresetState state)
    {
        var presets = state.Presets.Select(preset => preset with
        {
            LayoutMode = preset.LayoutMode is "manual" or "grid" ? preset.LayoutMode : "grid",
            Grid = preset.LayoutMode == "manual" ? preset.Grid : preset.Grid ?? new GridLayoutSettings(),
            UpdatedAt = preset.UpdatedAt ?? DateTimeOffset.UtcNow,
            Components = preset.Components.Select((component, index) => component with
            {
                InstanceId = string.IsNullOrWhiteSpace(component.InstanceId) ? Guid.NewGuid().ToString("N") : component.InstanceId,
                Layout = component.Layout ?? new ComponentLayout("automatic", Order: index, ZIndex: index),
                Config = component.Config ?? []
            }).ToArray()
        }).ToArray();
        var hostPresetIds = state.HostPresetIds ?? new Dictionary<string, IReadOnlyList<string>>();
        return state with
        {
            Presets = presets,
            HostPresetIds = hostPresetIds,
            HostConfigs = state.HostConfigs ?? new Dictionary<string, Dictionary<string, JsonElement>>()
        };
    }

    private static PresetState ApplyRevisions(PresetState current, PresetState next) => next with
    {
        Presets = next.Presets.Select(preset =>
        {
            var previous = current.Presets.FirstOrDefault(candidate => candidate.Id == preset.Id);
            if (previous is null) return preset with { Revision = Math.Max(0, preset.Revision), UpdatedAt = preset.UpdatedAt ?? DateTimeOffset.UtcNow };
            if (SameContent(previous, preset)) return preset with { Revision = previous.Revision, UpdatedAt = previous.UpdatedAt ?? preset.UpdatedAt };
            return preset with { Revision = Math.Max(previous.Revision + 1, preset.Revision), UpdatedAt = DateTimeOffset.UtcNow };
        }).ToArray()
    };

    private static bool SameContent(ComponentPreset left, ComponentPreset right) =>
        JsonSerializer.Serialize(left with { Revision = 0, UpdatedAt = null }, Options) ==
        JsonSerializer.Serialize(right with { Revision = 0, UpdatedAt = null }, Options);

    private async Task WriteUnlockedAsync(PresetState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, Options), cancellationToken);
        File.Move(temporary, path, true);
    }
}
