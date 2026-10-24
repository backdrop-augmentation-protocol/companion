using System.Diagnostics;
using Bap.Companion.Core;

namespace Bap.Companion.Desktop;

internal sealed class MainForm : Form
{
    private readonly InstalledCatalog catalog = InstalledCatalog.FromEnvironment();
    private readonly JsonPresetStore store = new(JsonPresetStore.DefaultPath);
    private readonly CompanionLog log;
    private readonly int port;
    private PresetState state = new([], null, new Dictionary<string, string>());
    private IReadOnlyList<InstalledItem> items = [];
    private readonly DataGridView hosts = Grid();
    private readonly DataGridView components = Grid();
    private readonly ListBox presets = new() { Width = 230, Height = 300 };
    private readonly ListBox selectedComponents = new() { Width = 340, Height = 300 };
    private readonly ComboBox availableComponents = new() { Width = 340, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox hostBinding = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox monitor = new() { Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label presetStatus = new() { AutoSize = true };
    private readonly Label serviceStatus = new() { AutoSize = true };

    public MainForm(CompanionLog log, int port)
    {
        this.log = log;
        this.port = port;
        Text = "BAP Companion";
        MinimumSize = new Size(900, 650);
        Size = new Size(1050, 760);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildCatalogPage());
        tabs.TabPages.Add(BuildPresetsPage());
        tabs.TabPages.Add(BuildSettingsPage());
        Controls.Add(tabs);
        Load += async (_, _) => await RefreshAsync();
    }

    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false
    };

    private TabPage BuildCatalogPage()
    {
        var page = new TabPage("Installed");
        hosts.Columns.Add("title", "Host / Web wallpaper"); hosts.Columns.Add("source", "Source");
        hosts.Columns.Add("id", "BAP ID"); hosts.Columns.Add("version", "Version");
        hosts.Columns.Add("status", "Status"); hosts.Columns.Add("workshop", "Workshop ID");
        components.Columns.Add("title", "Component"); components.Columns.Add("source", "Source");
        components.Columns.Add("id", "BAP ID"); components.Columns.Add("version", "Version");
        components.Columns.Add("status", "Status"); components.Columns.Add("workshop", "Workshop ID");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.Controls.Add(new Label { Text = "Installed hosts and Web wallpapers", Dock = DockStyle.Fill }, 0, 0);
        layout.Controls.Add(hosts, 0, 1);
        layout.Controls.Add(new Label { Text = "Installed components (local and subscribed copies remain separate)", Dock = DockStyle.Fill }, 0, 2);
        layout.Controls.Add(components, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        actions.Controls.Add(Button("Refresh", async () => await RefreshAsync()));
        for (var index = 0; index < Screen.AllScreens.Length; index++) monitor.Items.Add($"WE monitor {index}");
        monitor.SelectedIndex = 0;
        actions.Controls.Add(monitor);
        actions.Controls.Add(Button("Switch to selected host", async () => await SwitchHostAsync()));
        layout.Controls.Add(actions, 0, 4);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildPresetsPage()
    {
        var page = new TabPage("Presets");
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), WrapContents = false, AutoScroll = true };
        var left = new FlowLayoutPanel { Width = 250, Height = 580, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        left.Controls.Add(new Label { Text = "Presets", AutoSize = true });
        left.Controls.Add(presets);
        left.Controls.Add(Button("New preset", async () => await CreatePresetAsync()));
        left.Controls.Add(Button("Rename", async () => await RenamePresetAsync()));
        left.Controls.Add(Button("Delete", async () => await DeletePresetAsync()));
        presets.SelectedIndexChanged += (_, _) => RenderSelectedComponents();
        var middle = new FlowLayoutPanel { Width = 370, Height = 580, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        middle.Controls.Add(new Label { Text = "Components in selected preset (top to bottom = display order)", AutoSize = true });
        middle.Controls.Add(selectedComponents);
        middle.Controls.Add(availableComponents);
        middle.Controls.Add(Button("Add selected installed component", async () => await AddComponentAsync()));
        middle.Controls.Add(Button("Remove", async () => await RemoveComponentAsync()));
        middle.Controls.Add(Button("Move up", async () => await MoveComponentAsync(-1)));
        middle.Controls.Add(Button("Move down", async () => await MoveComponentAsync(1)));
        var right = new FlowLayoutPanel { Width = 300, Height = 580, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        right.Controls.Add(new Label { Text = "Where to apply", AutoSize = true });
        right.Controls.Add(Button("Set as global preset", async () => await SetBindingAsync(null)));
        right.Controls.Add(new Label { Text = "Host override", AutoSize = true });
        right.Controls.Add(hostBinding);
        right.Controls.Add(Button("Set for selected host", async () =>
        {
            var hostId = (hostBinding.SelectedItem as CatalogChoice)?.Item.BapId;
            if (hostId is null) { MessageBox.Show("Select a BAP host first."); return; }
            await SetBindingAsync(hostId);
        }));
        right.Controls.Add(Button("Clear selected host override", async () => await ClearBindingAsync()));
        right.Controls.Add(presetStatus);
        layout.Controls.Add(left); layout.Controls.Add(middle); layout.Controls.Add(right);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildSettingsPage()
    {
        var page = new TabPage("Settings");
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(20), WrapContents = false };
        var startup = new CheckBox { Text = "Start with Windows (minimized to tray)", AutoSize = true, Checked = DesktopSettings.AutoStartEnabled };
        startup.CheckedChanged += (_, _) =>
        {
            try { DesktopSettings.AutoStartEnabled = startup.Checked; }
            catch (Exception error) { MessageBox.Show(error.Message, "Startup setting", MessageBoxButtons.OK, MessageBoxIcon.Error); startup.Checked = DesktopSettings.AutoStartEnabled; }
        };
        panel.Controls.Add(startup);
        panel.Controls.Add(Button("Open current log", () => Process.Start(new ProcessStartInfo(log.Path) { UseShellExecute = true })));
        panel.Controls.Add(Button("Open log folder", () => Process.Start(new ProcessStartInfo(Path.GetDirectoryName(log.Path)!) { UseShellExecute = true })));
        panel.Controls.Add(new Label { Text = $"Service: http://127.0.0.1:{port}", AutoSize = true });
        panel.Controls.Add(serviceStatus);
        panel.Controls.Add(Button("Check service", async () => await CheckServiceAsync()));
        page.Controls.Add(panel);
        return page;
    }

    private static Button Button(string title, Action action)
    {
        var button = new Button { Text = title, AutoSize = true, MinimumSize = new Size(120, 30) };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button Button(string title, Func<Task> action) => Button(title, () => _ = RunActionAsync(action));

    private static async Task RunActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { MessageBox.Show(error.Message, "BAP Companion", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async Task RefreshAsync()
    {
        items = await catalog.DiscoverAsync(force: true);
        state = await store.ReadAsync();
        hosts.Rows.Clear(); components.Rows.Clear();
        foreach (var item in items)
        {
            var grid = item.Role == "component" ? components : hosts;
            var row = grid.Rows[grid.Rows.Add(item.Title, item.SourceKind, item.BapId ?? "—", item.Version ?? "—", item.Status, item.WorkshopId ?? "—")];
            row.Tag = item;
            foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = item.ProjectDirectory;
        }
        availableComponents.Items.Clear(); hostBinding.Items.Clear();
        foreach (var item in items.Where(item => item.Role == "component" && item.Status == "valid")) availableComponents.Items.Add(new CatalogChoice(item));
        foreach (var item in items.Where(item => item.Role == "host" && item.BapId is not null && item.Status != "invalid")) hostBinding.Items.Add(new CatalogChoice(item));
        if (availableComponents.Items.Count > 0) availableComponents.SelectedIndex = 0;
        if (hostBinding.Items.Count > 0) hostBinding.SelectedIndex = 0;
        RenderPresets();
        await CheckServiceAsync();
    }

    private ComponentPreset? SelectedPreset => (presets.SelectedItem as PresetChoice)?.Preset;

    private void RenderPresets(string? selectId = null)
    {
        selectId ??= SelectedPreset?.Id;
        presets.Items.Clear();
        foreach (var preset in state.Presets) presets.Items.Add(new PresetChoice(preset));
        var index = state.Presets.ToList().FindIndex(preset => preset.Id == selectId);
        if (index >= 0) presets.SelectedIndex = index;
        RenderSelectedComponents();
        var global = state.Presets.FirstOrDefault(preset => preset.Id == state.GlobalPresetId)?.Name ?? "All installed (default)";
        presetStatus.Text = $"Global: {global}\nHost overrides: {state.HostPresetIds.Count}";
    }

    private void RenderSelectedComponents()
    {
        selectedComponents.Items.Clear();
        foreach (var component in SelectedPreset?.Components ?? [])
        {
            var item = items.FirstOrDefault(candidate => candidate.BapId == component.ComponentId && (component.SourceKey is null || candidate.Key == component.SourceKey));
            selectedComponents.Items.Add(new ComponentChoice(component, $"{item?.Title ?? component.ComponentId} [{item?.SourceKind ?? "missing"}]"));
        }
    }

    private async Task SaveAsync(PresetState next, string? selectedId = null)
    {
        await store.WriteAsync(next);
        state = next;
        RenderPresets(selectedId);
    }

    private static string? AskName(string title, string value = "")
    {
        using var dialog = new Form { Text = title, Width = 360, Height = 150, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent };
        var input = new TextBox { Left = 12, Top = 12, Width = 320, Text = value };
        var ok = new Button { Text = "OK", Left = 250, Top = 48, DialogResult = DialogResult.OK };
        dialog.Controls.Add(input); dialog.Controls.Add(ok); dialog.AcceptButton = ok;
        return dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(input.Text) ? input.Text.Trim() : null;
    }

    private async Task CreatePresetAsync()
    {
        var name = AskName("New preset");
        if (name is null) return;
        var preset = new ComponentPreset(Guid.NewGuid().ToString("N"), name, []);
        await SaveAsync(state with { Presets = [.. state.Presets, preset] }, preset.Id);
    }

    private async Task RenamePresetAsync()
    {
        var preset = SelectedPreset;
        if (preset is null) return;
        var name = AskName("Rename preset", preset.Name);
        if (name is null) return;
        await ReplacePresetAsync(preset with { Name = name });
    }

    private async Task DeletePresetAsync()
    {
        var preset = SelectedPreset;
        if (preset is null) return;
        if (MessageBox.Show($"Delete preset '{preset.Name}'?", "BAP Companion", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        await SaveAsync(state with {
            Presets = state.Presets.Where(item => item.Id != preset.Id).ToArray(),
            GlobalPresetId = state.GlobalPresetId == preset.Id ? null : state.GlobalPresetId,
            HostPresetIds = state.HostPresetIds.Where(pair => pair.Value != preset.Id).ToDictionary()
        });
    }

    private async Task ReplacePresetAsync(ComponentPreset changed) => await SaveAsync(state with {
        Presets = state.Presets.Select(item => item.Id == changed.Id ? changed : item).ToArray()
    }, changed.Id);

    private async Task AddComponentAsync()
    {
        var preset = SelectedPreset; var item = (availableComponents.SelectedItem as CatalogChoice)?.Item;
        if (preset is null || item?.BapId is null) return;
        if (preset.Components.Any(component => component.ComponentId == item.BapId))
        {
            MessageBox.Show("Remove the existing entry before selecting another source for this component ID."); return;
        }
        await ReplacePresetAsync(preset with { Components = [.. preset.Components, new(item.BapId, item.Key)] });
    }

    private async Task RemoveComponentAsync()
    {
        var preset = SelectedPreset; var selected = (selectedComponents.SelectedItem as ComponentChoice)?.Component;
        if (preset is null || selected is null) return;
        await ReplacePresetAsync(preset with { Components = preset.Components.Where(component => component.ComponentId != selected.ComponentId).ToArray() });
    }

    private async Task MoveComponentAsync(int offset)
    {
        var preset = SelectedPreset; var index = selectedComponents.SelectedIndex;
        if (preset is null || index < 0 || index + offset < 0 || index + offset >= preset.Components.Count) return;
        var ordered = preset.Components.ToList();
        (ordered[index], ordered[index + offset]) = (ordered[index + offset], ordered[index]);
        await ReplacePresetAsync(preset with { Components = ordered });
        selectedComponents.SelectedIndex = index + offset;
    }

    private async Task SetBindingAsync(string? hostId)
    {
        var preset = SelectedPreset;
        if (preset is null) return;
        if (hostId is null) await SaveAsync(state with { GlobalPresetId = preset.Id }, preset.Id);
        else
        {
            var bindings = new Dictionary<string, string>(state.HostPresetIds) { [hostId] = preset.Id };
            await SaveAsync(state with { HostPresetIds = bindings }, preset.Id);
        }
    }

    private async Task ClearBindingAsync()
    {
        var hostId = (hostBinding.SelectedItem as CatalogChoice)?.Item.BapId;
        if (hostId is null) return;
        var bindings = new Dictionary<string, string>(state.HostPresetIds);
        bindings.Remove(hostId);
        await SaveAsync(state with { HostPresetIds = bindings });
    }

    private async Task SwitchHostAsync()
    {
        var host = hosts.SelectedRows.Count > 0 ? hosts.SelectedRows[0].Tag as InstalledItem : null;
        if (host is null) return;
        var confirmed = await WallpaperEngineControl.OpenAsync(host, monitor.SelectedIndex);
        MessageBox.Show(confirmed ? $"Wallpaper Engine switched to {host.Title}."
            : $"Switch command sent for {host.Title}, but Wallpaper Engine did not return a path to confirm it.", "BAP Companion");
    }

    private async Task CheckServiceAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await client.GetAsync($"http://127.0.0.1:{port}/healthz");
            serviceStatus.Text = response.IsSuccessStatusCode ? "Service: online" : $"Service: HTTP {(int)response.StatusCode}";
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { serviceStatus.Text = "Service: offline"; }
    }

    private sealed record CatalogChoice(InstalledItem Item)
    {
        public override string ToString() => $"{Item.Title} [{Item.SourceKind}: {Item.Key}]";
    }
    private sealed record PresetChoice(ComponentPreset Preset) { public override string ToString() => Preset.Name; }
    private sealed record ComponentChoice(PresetComponent Component, string Label) { public override string ToString() => Label; }
}
