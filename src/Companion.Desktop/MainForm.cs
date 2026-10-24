using System.Diagnostics;
using Bap.Companion.Core;

namespace Bap.Companion.Desktop;

internal sealed class MainForm : Form
{
    private readonly InstalledCatalog catalog = InstalledCatalog.FromEnvironment();
    private readonly JsonPresetStore store = new(JsonPresetStore.DefaultPath);
    private readonly CompanionLog log;
    private readonly int port;
    private readonly DesktopSettings settings;
    private PresetState state = new([], null, new Dictionary<string, string>());
    private IReadOnlyList<InstalledItem> items = [];
    private readonly CatalogView catalogView;
    private readonly ListBox presets = new() { Width = 230, Height = 300 };
    private readonly ListBox selectedComponents = new() { Width = 340, Height = 300 };
    private readonly ComboBox availableComponents = new() { Width = 340, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox hostBinding = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label presetStatus = new() { AutoSize = true };
    private readonly Label serviceStatus = new() { AutoSize = true };
    private readonly CheckBox startup = new() { AutoSize = true };
    private readonly ComboBox language = new() { Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };

    public MainForm(CompanionLog log, int port, DesktopSettings settings)
    {
        this.log = log;
        this.port = port;
        this.settings = settings;
        catalogView = new CatalogView(key => L(key), async (item, display) => await RunActionAsync(() => SwitchHostAsync(item, display)));
        Text = "BAP Companion";
        Font = new Font("Segoe UI", 10);
        BackColor = UiTheme.Background;
        MinimumSize = new Size(1100, 700);
        Size = new Size(1280, 800);
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill, DrawMode = TabDrawMode.OwnerDrawFixed,
            SizeMode = TabSizeMode.Fixed, ItemSize = new Size(160, 42),
            Padding = new Point(16, 6), BackColor = UiTheme.Background
        };
        tabs.DrawItem += (_, e) =>
        {
            var selected = e.Index == tabs.SelectedIndex;
            using var background = new SolidBrush(selected ? UiTheme.Surface : UiTheme.Background);
            e.Graphics.FillRectangle(background, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, Font, e.Bounds,
                selected ? UiTheme.Accent : UiTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (selected)
            {
                using var accent = new SolidBrush(UiTheme.Accent);
                e.Graphics.FillRectangle(accent, e.Bounds.Left + 12, e.Bounds.Bottom - 3, e.Bounds.Width - 24, 3);
            }
        };
        tabs.TabPages.Add(BuildCatalogPage());
        tabs.TabPages.Add(BuildPresetsPage());
        tabs.TabPages.Add(BuildSettingsPage());
        Controls.Add(tabs);
        settings.LanguageChanged += (_, _) => { ApplyLanguage(); _ = RunActionAsync(RefreshAsync); };
        ApplyLanguage();
        Load += async (_, _) => await RefreshAsync();
    }

    private string L(string key, params object[] args) => UiStrings.Get(key, settings.Language, args);

    private void ApplyLanguage()
    {
        foreach (var control in Descendants(this))
            if (control.Tag is string key) control.Text = L(key);
        RenderPresets();
        catalogView.RefreshLanguage();
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static T Labelled<T>(T control, string key) where T : Control { control.Tag = key; return control; }

    private TabPage BuildCatalogPage()
    {
        var page = Labelled(new TabPage(), "installed");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(16), BackColor = UiTheme.Background };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(Labelled(UiTheme.Heading(), "installed"), 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, BackColor = UiTheme.Background };
        actions.Controls.Add(Labelled(UiTheme.Caption(), "catalogHint"));
        actions.Controls.Add(Labelled(Button("", async () => await RefreshAsync()), "refresh"));
        layout.Controls.Add(actions, 0, 1);
        layout.Controls.Add(catalogView, 0, 2);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildPresetsPage()
    {
        var page = Labelled(new TabPage { BackColor = UiTheme.Background }, "presets");
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), WrapContents = false, AutoScroll = true, BackColor = UiTheme.Background };
        var left = new FlowLayoutPanel { Width = 250, Height = 600, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = UiTheme.Surface, Padding = new Padding(12), Margin = new Padding(6) };
        left.Controls.Add(Labelled(UiTheme.Heading(), "presets"));
        left.Controls.Add(presets);
        left.Controls.Add(Labelled(Button("", async () => await CreatePresetAsync()), "newPreset"));
        left.Controls.Add(Labelled(Button("", async () => await RenamePresetAsync()), "rename"));
        left.Controls.Add(Labelled(Button("", async () => await DeletePresetAsync()), "delete"));
        presets.SelectedIndexChanged += (_, _) => RenderSelectedComponents();
        var middle = new FlowLayoutPanel { Width = 370, Height = 600, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = UiTheme.Surface, Padding = new Padding(12), Margin = new Padding(6) };
        middle.Controls.Add(Labelled(UiTheme.Caption(), "selectedComponents"));
        middle.Controls.Add(selectedComponents);
        middle.Controls.Add(availableComponents);
        middle.Controls.Add(Labelled(Button("", async () => await AddComponentAsync()), "addComponent"));
        middle.Controls.Add(Labelled(Button("", async () => await RemoveComponentAsync()), "remove"));
        middle.Controls.Add(Labelled(Button("", async () => await MoveComponentAsync(-1)), "moveUp"));
        middle.Controls.Add(Labelled(Button("", async () => await MoveComponentAsync(1)), "moveDown"));
        var right = new FlowLayoutPanel { Width = 300, Height = 600, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = UiTheme.Surface, Padding = new Padding(12), Margin = new Padding(6) };
        right.Controls.Add(Labelled(UiTheme.Heading(), "scope"));
        right.Controls.Add(Labelled(Button("", async () => await SetBindingAsync(null)), "setGlobal"));
        right.Controls.Add(Labelled(UiTheme.Caption(), "hostOverride"));
        right.Controls.Add(hostBinding);
        right.Controls.Add(Labelled(Button("", async () =>
        {
            var hostId = (hostBinding.SelectedItem as CatalogChoice)?.Item.BapId;
            if (hostId is null) { MessageBox.Show(L("selectHostFirst")); return; }
            await SetBindingAsync(hostId);
        }), "setHost"));
        right.Controls.Add(Labelled(Button("", async () => await ClearBindingAsync()), "clearHost"));
        right.Controls.Add(presetStatus);
        layout.Controls.Add(left); layout.Controls.Add(middle); layout.Controls.Add(right);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildSettingsPage()
    {
        var page = Labelled(new TabPage { BackColor = UiTheme.Background }, "settings");
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(28), WrapContents = false, BackColor = UiTheme.Surface };
        startup.Tag = "startup";
        startup.Checked = settings.AutoStartEnabled;
        startup.CheckedChanged += (_, _) =>
        {
            try { settings.SetAutoStart(startup.Checked); }
            catch (Exception error) { MessageBox.Show(error.Message, L("settings"), MessageBoxButtons.OK, MessageBoxIcon.Error); startup.Checked = settings.AutoStartEnabled; }
        };
        panel.Controls.Add(startup);
        panel.Controls.Add(Labelled(UiTheme.Heading(), "language"));
        language.Items.AddRange(["English", "简体中文"]);
        language.SelectedIndex = settings.Language == "zh-CN" ? 1 : 0;
        language.SelectedIndexChanged += (_, _) => settings.SetLanguage(language.SelectedIndex == 1 ? "zh-CN" : "en");
        panel.Controls.Add(language);
        panel.Controls.Add(Labelled(UiTheme.Caption(), "languageHint"));
        panel.Controls.Add(Labelled(UiTheme.Heading(), "logs"));
        panel.Controls.Add(Labelled(Button("", () => Process.Start(new ProcessStartInfo(log.Path) { UseShellExecute = true })), "openLog"));
        panel.Controls.Add(Labelled(Button("", () => Process.Start(new ProcessStartInfo(Path.GetDirectoryName(log.Path)!) { UseShellExecute = true })), "openLogFolder"));
        panel.Controls.Add(new Label { Text = $"Service: http://127.0.0.1:{port}", AutoSize = true });
        panel.Controls.Add(serviceStatus);
        panel.Controls.Add(Labelled(Button("", async () => await CheckServiceAsync()), "checkService"));
        if (settings.StartupError is not null) panel.Controls.Add(new Label { Text = L("startupError", settings.StartupError), AutoSize = true, ForeColor = Color.Firebrick });
        page.Controls.Add(panel);
        return page;
    }

    private static Button Button(string title, Action action)
    {
        var button = UiTheme.Button(title);
        button.Click += (_, _) => action();
        return button;
    }

    private static Button Button(string title, Func<Task> action) => Button(title, (Action)(() => { _ = RunActionAsync(action); }));

    private static async Task RunActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { MessageBox.Show(error.Message, "BAP Companion", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async Task RefreshAsync()
    {
        items = await catalog.DiscoverAsync(force: true);
        state = await store.ReadAsync();
        catalogView.SetItems(items);
        availableComponents.Items.Clear(); hostBinding.Items.Clear();
        foreach (var item in items.Where(item => item.Role == "component" && item.Status == "valid")) availableComponents.Items.Add(new CatalogChoice(item, L(item.SourceKind)));
        foreach (var item in items.Where(item => item.Role == "host" && item.BapId is not null && item.Status != "invalid")) hostBinding.Items.Add(new CatalogChoice(item, L(item.SourceKind)));
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
        var global = state.Presets.FirstOrDefault(preset => preset.Id == state.GlobalPresetId)?.Name ?? L("allInstalled");
        presetStatus.Text = $"{L("global")}: {global}\n{L("overrides")}: {state.HostPresetIds.Count}";
    }

    private void RenderSelectedComponents()
    {
        selectedComponents.Items.Clear();
        foreach (var component in SelectedPreset?.Components ?? [])
        {
            var item = items.FirstOrDefault(candidate => candidate.BapId == component.ComponentId && (component.SourceKey is null || candidate.Key == component.SourceKey));
            selectedComponents.Items.Add(new ComponentChoice(component, $"{item?.Title ?? component.ComponentId} [{L(item?.SourceKind ?? "missing")}]"));
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
        var name = AskName(L("newPreset"));
        if (name is null) return;
        var preset = new ComponentPreset(Guid.NewGuid().ToString("N"), name, []);
        await SaveAsync(state with { Presets = [.. state.Presets, preset] }, preset.Id);
    }

    private async Task RenamePresetAsync()
    {
        var preset = SelectedPreset;
        if (preset is null) return;
        var name = AskName(L("rename"), preset.Name);
        if (name is null) return;
        await ReplacePresetAsync(preset with { Name = name });
    }

    private async Task DeletePresetAsync()
    {
        var preset = SelectedPreset;
        if (preset is null) return;
        if (MessageBox.Show(L("deletePreset", preset.Name), "BAP Companion", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
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
            MessageBox.Show(L("duplicateComponent")); return;
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

    private async Task SwitchHostAsync(InstalledItem host, int display)
    {
        var confirmed = await WallpaperEngineControl.OpenAsync(host, display);
        MessageBox.Show(confirmed ? L("switchConfirmed", host.Title)
            : L("switchUnconfirmed", host.Title), "BAP Companion");
    }

    private async Task CheckServiceAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await client.GetAsync($"http://127.0.0.1:{port}/healthz");
            serviceStatus.Text = response.IsSuccessStatusCode ? L("serviceOnline") : $"{L("status")}: HTTP {(int)response.StatusCode}";
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { serviceStatus.Text = L("serviceOffline"); }
    }

    private sealed record CatalogChoice(InstalledItem Item, string Source)
    {
        public override string ToString() => $"{Item.Title} [{Source}: {Item.Key}]";
    }
    private sealed record PresetChoice(ComponentPreset Preset) { public override string ToString() => Preset.Name; }
    private sealed record ComponentChoice(PresetComponent Component, string Label) { public override string ToString() => Label; }
}
