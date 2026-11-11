using Bap.Companion.Core;
using Xunit;

namespace Bap.Companion.Tests;

public sealed class PresetMutationTests
{
    [Fact]
    public void CreateAndDeletePresetKeepBindingsConsistent()
    {
        var state = State();
        var created = PresetSelection.CreatePreset(state, "evening", "  Evening  ");
        Assert.Equal("Evening", Assert.Single(created.Presets).Name);

        created = PresetSelection.SetGlobalPreset(created, "evening");
        created = PresetSelection.SetHostPreset(created, "local:host", "evening");
        var deleted = PresetSelection.DeletePreset(created, "evening");

        Assert.Empty(deleted.Presets);
        Assert.Null(deleted.GlobalPresetId);
        Assert.Empty(deleted.HostPresetIds);
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

    [Fact]
    public void HostBindingAndGlobalPresetCanBeSetAndClearedIndependently()
    {
        var state = State("daily", "focus");
        state = PresetSelection.SetGlobalPreset(state, "daily");
        state = PresetSelection.SetHostPreset(state, "workshop:host", "focus");

        Assert.Equal("daily", PresetSelection.Resolve(state, "local:host")?.Id);
        Assert.Equal("focus", PresetSelection.Resolve(state, "workshop:host")?.Id);

        state = PresetSelection.ClearHostPreset(state, "workshop:host");
        state = PresetSelection.SetGlobalPreset(state, null);
        Assert.Null(PresetSelection.Resolve(state, "workshop:host"));
    }

    private static PresetState State(params string[] ids) => new(
        ids.Select(id => new ComponentPreset(id, id, [], Grid: new GridLayoutSettings())).ToArray(),
        null,
        new Dictionary<string, string>(StringComparer.Ordinal));

    private static InstalledItem Component(string key, string id, int maxInstances) =>
        new(key, "local", "component", id, "C:/components", null, id, "1.0.0", null, null,
            MaxInstances: maxInstances);
}
