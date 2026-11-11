using Bap.Companion.Core;
using System.Text.Json;
using Xunit;

namespace Bap.Companion.Tests;

public sealed class PresetMutationTests
{
    [Fact]
    public void CreateAndDeletePresetClearsGlobalReference()
    {
        var state = State();
        var created = PresetSelection.CreatePreset(state, "evening", "  Evening  ");
        Assert.Equal("Evening", Assert.Single(created.Presets).Name);

        created = PresetSelection.SetGlobalPreset(created, "evening");
        var deleted = PresetSelection.DeletePreset(created, "evening");

        Assert.Empty(deleted.Presets);
        Assert.Null(deleted.GlobalPresetId);
    }

    [Fact]
    public void HostCanBelongToMultiplePresetsAndFirstEnabledPresetWins()
    {
        var state = State("first", "second", "global");
        state = PresetSelection.AddHostToPreset(state, "first", "workshop:host");
        state = PresetSelection.AddHostToPreset(state, "second", "workshop:host");
        state = PresetSelection.SetGlobalPreset(state, "global");

        Assert.Equal("first", PresetSelection.Resolve(state, "workshop:host")?.Id);
        state = PresetSelection.SetPresetEnabled(state, "first", false);
        Assert.Equal("second", PresetSelection.Resolve(state, "workshop:host")?.Id);
        state = PresetSelection.MovePreset(state, "global", 0);
        Assert.Equal("global", PresetSelection.Resolve(state, "workshop:host")?.Id);

        state = PresetSelection.RemoveHostFromPreset(state, "second", "workshop:host");
        Assert.Equal(["first"], state.HostPresetIds["workshop:host"]);
        Assert.Equal("global", PresetSelection.Resolve(state, "workshop:host")?.Id);
    }

    [Fact]
    public async Task LegacySingleHostBindingIsMigratedAndBackedUp()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bap-preset-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "presets.json");
        await File.WriteAllTextAsync(path, """{"presets":[{"id":"legacy","name":"Legacy","components":[]}],"globalPresetId":null,"hostPresetIds":{"workshop:host":"legacy"}}""");
        try
        {
            var state = await new JsonPresetStore(path).ReadAsync();
            Assert.Equal(["legacy"], state.HostPresetIds["workshop:host"]);
            Assert.True(state.Presets[0].Enabled);
            Assert.Single(Directory.GetFiles(directory, "presets.json.pre-instance-migration-*.bak"));
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(JsonValueKind.Array, saved.RootElement.GetProperty("hostPresetIds").GetProperty("workshop:host").ValueKind);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ComponentAddRemoveAndMovePreserveInstanceAndAutomaticOrder()
    {
        var state = State("daily");
        var item = Component("local:clock", "org.example.clock", maxInstances: 3);
        state = PresetSelection.AddComponent(state, "daily", item, "clock-a");
        state = PresetSelection.AddComponent(state, "daily", item, "clock-b");
        state = PresetSelection.MoveComponent(state, "daily", "clock-b", 0);

        var components = state.Presets[0].Components;
        Assert.Equal(["clock-b", "clock-a"], components.Select(component => component.InstanceId));
        Assert.Equal([0, 1], components.Select(component => component.Layout?.Order));

        state = PresetSelection.RemoveComponent(state, "daily", "clock-b");
        Assert.Equal("clock-a", Assert.Single(state.Presets[0].Components).InstanceId);
    }

    [Fact]
    public void ComponentAddRejectsInstanceLimitAndNonComponentItems()
    {
        var state = State("daily");
        var item = Component("local:clock", "org.example.clock", maxInstances: 1);
        state = PresetSelection.AddComponent(state, "daily", item, "clock-a");

        Assert.Throws<InvalidOperationException>(() => PresetSelection.AddComponent(state, "daily", item, "clock-b"));
        Assert.Throws<InvalidOperationException>(() => PresetSelection.AddComponent(state, "daily", item with { Role = "host" }, "host-a"));
        Assert.Single(state.Presets[0].Components);
    }

    private static PresetState State(params string[] ids) => new(
        ids.Select(id => new ComponentPreset(id, id, [], Grid: new GridLayoutSettings())).ToArray(),
        null,
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

    private static InstalledItem Component(string key, string id, int maxInstances) =>
        new(key, "local", "component", id, "C:/components", null, id, "1.0.0", null, null,
            MaxInstances: maxInstances);
}
