using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Bap.Companion.Core;
using Bap.Companion.Desktop;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using Color = System.Windows.Media.Color;

namespace Bap.Companion.Desktop.Wpf;

public partial class MainWindow : Window
{
    private readonly InstalledCatalog catalog = InstalledCatalog.FromEnvironment();
    private readonly SteamWorkshopCreatorLookup creatorLookup;
    private readonly JsonPresetStore store;
    private readonly CompanionLog log;
    private readonly int port;
    private readonly DesktopSettings settings;
    private PresetState state = new([], null, new Dictionary<string, string>());
    private IReadOnlyList<InstalledItem> items = [];
    private InstalledItem? selectedItem;
    private bool uiReady;
    private bool changingCatalogSelection;
    private bool changingDisplayChoice;
    private bool showingPresetHosts;
    private readonly HostAuthorizationClient? authorizationClient;
    private readonly DispatcherTimer authorizationTimer;
    private bool showingAuthorizationPrompt;
    private int creatorLookupGeneration;
    private bool renderingPropertyEditors;
    private bool renderingComponentChoices;
    private bool renderingPresets;
    private string? selectedComponentInstanceId;

    internal MainWindow(CompanionLog log, int port, DesktopSettings settings, HostAuthorizationClient? authorizationClient = null,
        SteamWorkshopCreatorLookup? creatorLookup = null, JsonPresetStore? presetStore = null)
    {
        this.log = log;
        this.port = port;
        this.settings = settings;
        this.authorizationClient = authorizationClient;
        this.creatorLookup = creatorLookup ?? new SteamWorkshopCreatorLookup();
        store = presetStore ?? new JsonPresetStore(JsonPresetStore.DefaultPath);
        InitializeComponent();
        StartupCheck.FocusVisualStyle = (Style)FindResource("NoFocusVisual");
        LanguageChoice.SelectedIndex = settings.Language == "zh-CN" ? 1 : 0;
        StartupCheck.IsChecked = settings.AutoStartEnabled;
        ServiceAddress.Text = $"Service: http://127.0.0.1:{port}";
        if (settings.StartupError is not null) StartupError.Text = L("startupError", settings.StartupError);
        settings.LanguageChanged += OnLanguageChanged;
        ApplyLanguage();
        SelectPage(InstalledPage, InstalledNav);
        uiReady = true;
        StateChanged += (_, _) => UpdateMaximizeButton();
        Loaded += async (_, _) => await RunActionAsync(async () =>
        {
            await RefreshAsync();
            await RefreshTrustedHostsAsync();
        });
        authorizationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        authorizationTimer.Tick += AuthorizationTimer_Tick;
        if (authorizationClient is not null) authorizationTimer.Start();
        Closed += (_, _) => authorizationTimer.Stop();
    }

    private async void AuthorizationTimer_Tick(object? sender, EventArgs e)
    {
        if (authorizationClient is null || showingAuthorizationPrompt) return;
        showingAuthorizationPrompt = true;
        try
        {
            var request = (await authorizationClient.GetPendingAsync()).FirstOrDefault();
            if (request is null) return;
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            var approved = HostApprovalDialog.Show(this, request, settings.Language, out var permissions, out var remember);
            var applied = await authorizationClient.DecideAsync(request.SessionId, approved, permissions, remember);
            if (!applied) log.Write("authorization", $"Request {request.SessionId} was no longer pending when the decision was submitted.");
            else log.Write("authorization", $"Host request {request.SessionId} {(approved ? "approved" : "denied")}{(remember ? " and remembered on this device" : " for this session")}.");
        }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException or System.Text.Json.JsonException)
        {
            log.Write("authorization", error.Message);
        }
        finally { showingAuthorizationPrompt = false; }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeButton()
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    private string L(string key, params object[] args) => UiStrings.Get(key, settings.Language, args);
    private void OnLanguageChanged(object? sender, EventArgs e) => ApplyLanguage();

    private void ApplyLanguage()
    {
        AppSubtitle.Text = L("appSubtitle");
        InstalledNav.Content = L("installed"); PresetsNav.Content = L("presets"); SettingsNav.Content = L("settings");
        InstalledTitle.Text = L("installed"); CatalogHint.Text = L("catalogHint"); RefreshButton.Content = L("refresh");
        HostsHeading.Text = L("hosts"); ComponentsHeading.Text = L("components");
        NoHosts.Text = L("noHosts"); NoComponents.Text = L("noComponents");
        DetailsHeading.Text = L("details"); DetailEmpty.Text = L("selectItem");
        LocationLabel.Text = L("location"); MonitorLabel.Text = L("switchDisplay"); MonitorHint.Text = L("switchDisplayHint");
        SwitchHostButton.Content = L("switchHost");
        PresetsTitle.Text = L("presets"); PresetIntro.Text = L("presetIntro");
        NewPresetButton.Content = L("newPreset"); RenameButton.Content = L("rename"); DeleteButton.Content = L("delete");
        SelectedComponentsHeading.Text = L(showingPresetHosts ? "hostsUsingPreset" : "selectedComponents");
        PropertyHeading.Text = settings.Language == "zh-CN" ? "组件属性" : "Component properties";
        if (SelectedComponents is not null) RenderPropertyEditors();
        ComponentsModeButton.Content = L("presetComponentsTab"); HostsModeButton.Content = L("presetHostsTab");
        AddComponentButton.Content = L("addComponent"); AddHostButton.Content = L("addHost");
        RemoveComponentButton.Content = L("remove"); RemoveHostButton.Content = L("removeHost");
        MoveUpButton.Content = L("moveUp"); MoveDownButton.Content = L("moveDown");
        SetGlobalButton.Content = L("setGlobal");
        ComponentsEmpty.Text = L("noComponentsInPreset"); HostsEmpty.Text = L("noHostsUsePreset");
        SetPresetListMode(showingPresetHosts);
        if (PresetList is not null) RenderPresets();
        SettingsTitle.Text = L("settings"); LanguageHeading.Text = L("language"); LanguageHint.Text = L("languageHint");
        StartupCheck.Content = L("startup"); LogsHeading.Text = L("logs");
        OpenLogButton.Content = L("openLog"); OpenLogFolderButton.Content = L("openLogFolder");
        CheckServiceButton.Content = L("checkService");
#if false
        TrustedHostsHeading.Text = settings.Language == "zh-CN" ? "宸茶浣忕殑瀹夸富" : "Remembered Hosts";
        TrustedHostsHint.Text = settings.Language == "zh-CN"
            ? "杩欎簺鏍囪瘑鐢卞绾歌嚜琛屽０鏄庯紝骞撮潪鍙戣鑰呴獙璇併€傚垹闄ゆ潯鐩悗锛屼笅娆¤繛鎺ラ渶閲嶆柊鎵瑰噯銆?
            : "These IDs are self-reported and not publisher-verified. Saved entries never bypass approval for a new session; removing one also revokes active permissions.";
        RevokeTrustButton.Content = settings.Language == "zh-CN" ? "蹇樿閫変腑瀹夸富" : "Forget selected Host";
#endif
        TrustedHostsHeading.Text = settings.Language == "zh-CN" ? "\u5df2\u8bb0\u4f4f\u7684\u5bbf\u4e3b" : "Remembered Hosts";
        TrustedHostsHint.Text = settings.Language == "zh-CN"
            ? "\u8fd9\u4e9b\u6807\u8bc6\u7531\u5bbf\u4e3b\u81ea\u884c\u58f0\u660e\uff0c\u672a\u9a8c\u8bc1\u53d1\u884c\u8005\u3002\u4fdd\u5b58\u6761\u76ee\u4e0d\u4f1a\u8df3\u8fc7\u65b0\u4f1a\u8bdd\u5ba1\u6279\uff1b\u79fb\u9664\u6761\u76ee\u4f1a\u540c\u65f6\u64a4\u9500\u6d3b\u8dc3\u6743\u9650\u3002"
            : "These IDs are self-reported and not publisher-verified. Saved entries never bypass approval for a new session; removing one also revokes active permissions.";
        RevokeTrustButton.Content = settings.Language == "zh-CN" ? "\u5fd8\u8bb0\u9009\u4e2d\u5bbf\u4e3b" : "Forget selected Host";
        if (settings.StartupError is not null) StartupError.Text = L("startupError", settings.StartupError);
        changingDisplayChoice = true;
        try
        {
            MonitorChoice.Items.Clear();
            for (var index = 0; index < Forms.Screen.AllScreens.Length; index++) MonitorChoice.Items.Add($"{L("monitor")} {index + 1}");
            if (MonitorChoice.Items.Count > 0) MonitorChoice.SelectedIndex = Math.Clamp(settings.DisplayIndex, 0, MonitorChoice.Items.Count - 1);
        }
        finally { changingDisplayChoice = false; }
        RenderCatalog();
        RenderPresets();
        RenderDetails();
    }

    private void SelectPage(UIElement page, Button nav)
    {
        InstalledPage.Visibility = page == InstalledPage ? Visibility.Visible : Visibility.Collapsed;
        PresetsPage.Visibility = page == PresetsPage ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == SettingsPage ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { InstalledNav, PresetsNav, SettingsNav })
        {
            button.Background = button == nav ? (Brush)FindResource("NavSelected") : Brushes.Transparent;
            button.Foreground = button == nav ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted");
        }
    }

    private void InstalledNav_Click(object sender, RoutedEventArgs e) => SelectPage(InstalledPage, InstalledNav);

    private void CatalogList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (CatalogScrollViewer.ScrollableHeight <= 0) return;
        var lines = SystemParameters.WheelScrollLines;
        if (lines == 0) return;
        e.Handled = true;
        if (lines < 0)
        {
            if (e.Delta > 0) CatalogScrollViewer.PageUp();
            else CatalogScrollViewer.PageDown();
        }
        else CatalogScrollViewer.ScrollToVerticalOffset(
            CatalogScrollViewer.VerticalOffset - e.Delta / 120.0 * lines * 16);
    }
    private void PresetsNav_Click(object sender, RoutedEventArgs e) => SelectPage(PresetsPage, PresetsNav);
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => SelectPage(SettingsPage, SettingsNav);
    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(RefreshAsync);

    private async Task RunActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error)
        {
            log.Write("desktop-error", error.ToString());
            ThemedDialog.ShowMessage(this, "BAP Companion", error.Message, settings.Language == "zh-CN" ? "确定" : "OK");
        }
    }

    private async Task RefreshAsync()
    {
        var discovered = await catalog.DiscoverAsync(force: true);
        var presets = await store.ReadAsync();
        RenderSnapshot(discovered, presets);
        await CheckServiceAsync();
    }

    internal void RenderSnapshot(IReadOnlyList<InstalledItem> discovered, PresetState presets)
    {
        items = discovered;
        state = presets;
        selectedItem = items.FirstOrDefault(item => item.Key == selectedItem?.Key);
        RenderCatalog();
        RenderPresets();
        RenderDetails();
    }

    private void RenderCatalog()
    {
        if (HostsList is null) return;
        changingCatalogSelection = true;
        try
        {
            HostsList.ItemsSource = items.Where(item => item.Role == "host").Select(CreateEntry).ToArray();
            ComponentsList.ItemsSource = items.Where(item => item.Role == "component").Select(CreateEntry).ToArray();
            NoHosts.Visibility = HostsList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoComponents.Visibility = ComponentsList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (selectedItem is not null)
            {
                var list = selectedItem.Role == "host" ? HostsList : ComponentsList;
                list.SelectedItem = list.Items.Cast<CatalogEntry>().FirstOrDefault(entry => entry.Item.Key == selectedItem.Key);
            }
        }
        finally { changingCatalogSelection = false; }
    }

    private CatalogEntry CreateEntry(InstalledItem item)
    {
        var preview = LoadPreview(item.PreviewPath);
        return new CatalogEntry(item, item.Title, L(item.SourceKind), $"{L(item.Status)}  ·  {item.Version ?? "—"}", preview, preview is null ? L("noPreview") : "");
    }

    private static ImageSource? LoadPreview(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 420;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException) { return null; }
    }

    private void Catalog_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingCatalogSelection || sender is not ListBox list || list.SelectedItem is not CatalogEntry entry) return;
        changingCatalogSelection = true;
        try
        {
            if (list == HostsList) ComponentsList.SelectedItem = null;
            else HostsList.SelectedItem = null;
            selectedItem = entry.Item;
            RenderDetails();
        }
        finally { changingCatalogSelection = false; }
    }

    private void RenderDetails()
    {
        if (DetailContent is null) return;
        DetailEmpty.Visibility = selectedItem is null ? Visibility.Visible : Visibility.Collapsed;
        DetailContent.Visibility = selectedItem is null ? Visibility.Collapsed : Visibility.Visible;
        if (selectedItem is not { } item) return;
        DetailPreview.Source = LoadPreview(item.PreviewPath);
        DetailTitle.Text = item.Title;
        var creatorGeneration = ++creatorLookupGeneration;
        var creatorText = item.WorkshopId is null ? "" : $"\n{L("creator")}: {L("creatorLoading")}";
        DetailMetadata.Text = $"{L("source")}: {L(item.SourceKind)}\n{L("status")}: {L(item.Status)}\n{L("bapId")}: {item.BapId ?? "—"}\n{L("version")}: {item.Version ?? "—"}" + creatorText +
            (item.WorkshopId is null ? "" : $"\n{L("workshopId")}: {item.WorkshopId}");
        if (item.WorkshopId is { } workshopId) _ = LoadCreatorNameAsync(item.Key, workshopId, creatorGeneration);
        DetailPath.Text = item.ProjectDirectory;
        HostActions.Visibility = item.Role == "host" && item.Status != "invalid" ? Visibility.Visible : Visibility.Collapsed;
        SwitchStatus.Text = "";
    }

    private async Task LoadCreatorNameAsync(string itemKey, string workshopId, int generation)
    {
        string? creator;
        try { creator = await creatorLookup.GetCreatorNameAsync(workshopId); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or System.Xml.XmlException or InvalidOperationException)
        {
            log.Write("workshop-metadata", $"Could not read creator for Workshop item {workshopId}: {error.Message}");
            creator = null;
        }
        if (generation != creatorLookupGeneration || selectedItem?.Key != itemKey || DetailMetadata is null) return;
        var name = string.IsNullOrWhiteSpace(creator) ? L("creatorUnavailable") : creator;
        var metadata = DetailMetadata.Text;
        var loadingLine = $"{L("creator")}: {L("creatorLoading")}";
        DetailMetadata.Text = metadata.Replace(loadingLine, $"{L("creator")}: {name}", StringComparison.Ordinal);
    }

    private void SwitchHostButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(async () =>
    {
        if (selectedItem is null) return;
        SwitchHostButton.IsEnabled = false;
        try
        {
            var result = await WallpaperEngineControl.OpenAsync(selectedItem, EffectiveDisplayIndex);
            SwitchStatus.Text = L(result == WallpaperSwitchResult.Confirmed ? "switchVerified" : "switchSent");
        }
        finally { SwitchHostButton.IsEnabled = true; }
    });

    internal int EffectiveDisplayIndex => Math.Clamp(settings.DisplayIndex, 0, Math.Max(0, Forms.Screen.AllScreens.Length - 1));

    private ComponentPreset? SelectedPreset => (PresetList.SelectedItem as PresetChoice)?.Preset;

    private void RenderPresets(string? selectId = null)
    {
        if (PresetList is null) return;
        selectId ??= SelectedPreset?.Id;
        renderingPresets = true;
        try
        {
            PresetList.ItemsSource = state.Presets.Select(preset => new PresetChoice(preset)).ToArray();
            PresetList.SelectedItem = PresetList.Items.Cast<PresetChoice>().FirstOrDefault(choice => choice.Preset.Id == selectId);
        }
        finally { renderingPresets = false; }
        RenderSelectedComponents();
        RenderPresetHosts();
    }

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (renderingPresets) return;
        RenderSelectedComponents();
        RenderPresetHosts();
    }

    private void ComponentsModeButton_Click(object sender, RoutedEventArgs e) => SetPresetListMode(false);
    private void HostsModeButton_Click(object sender, RoutedEventArgs e) => SetPresetListMode(true);

    private void SetPresetListMode(bool hosts)
    {
        showingPresetHosts = hosts;
        SelectedComponents.Visibility = hosts ? Visibility.Collapsed : Visibility.Visible;
        PresetHostsList.Visibility = hosts ? Visibility.Visible : Visibility.Collapsed;
        AddComponentButton.Visibility = hosts ? Visibility.Collapsed : Visibility.Visible;
        AddHostButton.Visibility = hosts ? Visibility.Visible : Visibility.Collapsed;
        SetGlobalButton.Visibility = hosts ? Visibility.Visible : Visibility.Collapsed;
        ComponentsEmpty.Visibility = !hosts && SelectedComponents.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HostsEmpty.Visibility = hosts && PresetHostsList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ComponentActions.Visibility = hosts ? Visibility.Collapsed : Visibility.Visible;
        RemoveHostButton.Visibility = hosts ? Visibility.Visible : Visibility.Collapsed;
        SelectedComponentsHeading.Text = L(hosts ? "hostsUsingPreset" : "selectedComponents");
        ComponentsModeButton.Style = (Style)FindResource(hosts ? "QuietButton" : "PrimaryButton");
        HostsModeButton.Style = (Style)FindResource(hosts ? "PrimaryButton" : "QuietButton");
        if (!hosts) RenderSelectedComponents();
        RenderPresetHosts();
    }

    private void SelectedComponents_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (renderingComponentChoices) return;
        selectedComponentInstanceId = (SelectedComponents.SelectedItem as ComponentChoice)?.Component.InstanceId;
        UpdateComponentActions();
        RenderPropertyEditors();
    }

    private void RenderSelectedComponents()
    {
        if (SelectedComponents is null) return;
        var selectedInstanceId = (SelectedComponents.SelectedItem as ComponentChoice)?.Component.InstanceId ?? selectedComponentInstanceId;
        renderingComponentChoices = true;
        try
        {
            SelectedComponents.ItemsSource = (SelectedPreset?.Components ?? [])
                .Select(component =>
                {
                    var item = items.FirstOrDefault(candidate => candidate.BapId == component.ComponentId &&
                        (component.SourceKey is null || candidate.Key == component.SourceKey));
                    return new ComponentChoice(component,
                        item?.Title ?? component.ComponentId,
                        item is null ? L("missing") : L(item.SourceKind),
                        $"{(item is null ? L("missing") : L(item.Status))} · {L("instance")}: {component.InstanceId}",
                        LoadPreview(item?.PreviewPath), item?.PreviewPath is null ? L("noPreview") : "");
                }).ToArray();
            SelectedComponents.SelectedItem = SelectedComponents.Items.Cast<ComponentChoice>()
                .FirstOrDefault(choice => choice.Component.InstanceId == selectedInstanceId);
            ComponentsEmpty.Visibility = SelectedComponents.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            selectedComponentInstanceId = (SelectedComponents.SelectedItem as ComponentChoice)?.Component.InstanceId;
        }
        finally { renderingComponentChoices = false; }
        AddComponentButton.IsEnabled = SelectedPreset is not null;
        UpdateComponentActions();
        RenderPropertyEditors();
    }

    private void UpdateComponentActions()
    {
        var selected = SelectedComponent is not null;
        RemoveComponentButton.IsEnabled = selected;
        MoveUpButton.IsEnabled = selected && SelectedComponents.SelectedIndex > 0;
        MoveDownButton.IsEnabled = selected && SelectedPreset is { } preset && SelectedComponents.SelectedIndex < preset.Components.Count - 1;
    }

    private void RenderPresetHosts()
    {
        if (PresetHostsList is null) return;
        var preset = SelectedPreset;
        var selectedHostId = (PresetHostsList.SelectedItem as PresetHostChoice)?.HostId;
        PresetHostChoice[] hosts;
        if (preset is null) hosts = [];
        else if (state.GlobalPresetId == preset.Id)
        {
            hosts = [new PresetHostChoice(null, L("allHosts"), L("globalPreset"), preset.Name, null, "", true)];
        }
        else
        {
            hosts = state.HostPresetIds.Where(binding => binding.Value == preset.Id)
                .Select(binding =>
                {
                    var item = FindHost(binding.Key);
                    return new PresetHostChoice(binding.Key, item?.Title ?? binding.Key,
                        item is null ? L("missing") : L(item.SourceKind),
                        item is null ? binding.Key : $"{L(item.Status)} · {item.Version ?? "—"}",
                        LoadPreview(item?.PreviewPath), item?.PreviewPath is null ? L("noPreview") : "", false);
                }).ToArray();
        }
        PresetHostsList.ItemsSource = hosts;
        PresetHostsList.SelectedItem = hosts.FirstOrDefault(host => host.HostId == selectedHostId);
        HostsEmpty.Visibility = hosts.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddHostButton.IsEnabled = preset is not null && state.GlobalPresetId != preset.Id;
        AddHostButton.Visibility = showingPresetHosts && preset is not null && state.GlobalPresetId != preset.Id ? Visibility.Visible : Visibility.Collapsed;
        SetGlobalButton.IsEnabled = preset is not null;
        SetGlobalButton.Content = L(preset is not null && state.GlobalPresetId == preset.Id ? "globalPreset" : "setGlobal");
        SetGlobalButton.Style = (Style)FindResource(preset is not null && state.GlobalPresetId == preset.Id ? "PrimaryButton" : "QuietButton");
        PresetStatus.Text = $"{L("global")}: {state.Presets.FirstOrDefault(entry => entry.Id == state.GlobalPresetId)?.Name ?? L("allInstalled")}\n{L("overrides")}: {state.HostPresetIds.Count}";
        UpdateHostActions();
    }

    private void PresetHostsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateHostActions();

    private void UpdateHostActions()
    {
        var canRemove = PresetHostsList.SelectedItem is PresetHostChoice { IsGlobal: false, HostId: not null };
        RemoveHostButton.IsEnabled = canRemove;
        RemoveHostButton.Visibility = showingPresetHosts && canRemove ? Visibility.Visible : Visibility.Collapsed;
    }

    private InstalledItem? FindHost(string bapId) => items.FirstOrDefault(item => item.Role == "host" && item.BapId == bapId && item.Status != "invalid");

    private IReadOnlyList<InstalledItem> GetHostCandidates() => items
        .Where(item => item.Role == "host" && item.BapId is not null && item.Status != "invalid")
        .GroupBy(item => item.BapId!, StringComparer.Ordinal)
        .Select(group => group.OrderBy(item => item.Status == "valid" ? 0 : 1)
            .ThenBy(item => item.SourceKind == "workshop" ? 0 : 1).ThenBy(item => item.Key, StringComparer.Ordinal).First())
        .ToArray();

    private PresetComponent? SelectedComponent => (SelectedComponents.SelectedItem as ComponentChoice)?.Component;

    private void RenderPropertyEditors()
    {
        if (InstancePropertyPanel is null) return;
        renderingPropertyEditors = true;
        try
        {
            InstancePropertyPanel.Children.Clear();
            var component = SelectedComponent;
            var item = component is null ? null : items.FirstOrDefault(candidate => candidate.BapId == component.ComponentId &&
                (component.SourceKey is null || candidate.Key == component.SourceKey));
            if (component is null || item is null)
            {
                PropertyHint.Text = settings.Language == "zh-CN" ? "选择一个组件实例以编辑其预设属性。" : "Select a component instance to edit its preset values.";
                return;
            }

            if (item.Properties is not { Count: > 0 } definitions)
            {
                PropertyHint.Text = item.PropertyDiagnostics is { Count: > 0 }
                    ? (settings.Language == "zh-CN" ? "没有可编辑属性；部分声明存在问题。" : "No editable properties; some declarations are invalid.")
                    : (settings.Language == "zh-CN" ? "此组件未声明可编辑的 WE 属性。" : "This component declares no editable WE properties.");
                AddPropertyDiagnostics(item);
                return;
            }

            PropertyHint.Text = settings.Language == "zh-CN"
                ? $"{item.Title} · 此实例在当前预设中的值"
                : $"{item.Title} · values for this instance in the selected preset";
            var effectiveValues = definitions.ToDictionary(definition => definition.Key,
                definition => component.Config is not null && component.Config.TryGetValue(definition.Key, out var configured) &&
                    WallpaperPropertyReader.IsValidValue(definition, configured) ? configured : definition.DefaultValue,
                StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                var value = effectiveValues[definition.Key];
                if (definition.ConditionSupported && !WallpaperPropertyReader.IsConditionMet(definition, effectiveValues)) continue;
                var context = new PropertyEditContext(component.InstanceId!, definition);
                var title = new TextBlock
                {
                    Text = PropertyTitle(definition),
                    Foreground = (Brush)FindResource("Ink"), FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 4)
                };
                InstancePropertyPanel.Children.Add(title);
                switch (definition.Type)
                {
                    case "bool":
                        var check = new CheckBox { IsChecked = value.ValueKind == System.Text.Json.JsonValueKind.True, Tag = context, Margin = new Thickness(0, 0, 0, 5) };
                        check.Checked += PropertyCheck_Changed;
                        check.Unchecked += PropertyCheck_Changed;
                        check.IsEnabled = definition.ConditionSupported;
                        InstancePropertyPanel.Children.Add(check);
                        break;
                    case "combo":
                        var combo = new ComboBox { Tag = context, Margin = new Thickness(0, 0, 0, 5), IsEnabled = definition.ConditionSupported };
                        combo.ItemsSource = (definition.Options ?? []).Select(option => new PropertyOptionChoice(option)).ToArray();
                        combo.DisplayMemberPath = nameof(PropertyOptionChoice.Label);
                        combo.SelectedItem = combo.Items.Cast<PropertyOptionChoice>().FirstOrDefault(option =>
                            System.Text.Json.JsonElement.DeepEquals(option.Option.Value, value));
                        combo.SelectionChanged += PropertyCombo_Changed;
                        InstancePropertyPanel.Children.Add(combo);
                        break;
                    case "textinput":
                        var textBox = new TextBox { Text = value.GetString() ?? "", Tag = context,
                            Style = (Style)FindResource("DialogTextBox"), IsEnabled = definition.ConditionSupported };
                        textBox.LostKeyboardFocus += PropertyText_LostKeyboardFocus;
                        InstancePropertyPanel.Children.Add(textBox);
                        break;
                    case "color":
                        var color = ColorPickerDialog.ParseWeColor(value.GetString());
                        var colorButton = new Button
                        {
                            Tag = context,
                            Name = "PropertyColorPicker",
                            ToolTip = settings.Language == "zh-CN" ? "选择颜色" : "Choose color",
                            Style = (Style)FindResource("QuietButton"),
                            IsEnabled = definition.ConditionSupported,
                            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
                            Margin = new Thickness(0, 0, 0, 3)
                        };
                        colorButton.Content = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Children =
                            {
                                new Border { Width = 28, Height = 20, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(color), BorderBrush = new SolidColorBrush(Color.FromRgb(75, 104, 148)), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 9, 0) },
                                new TextBlock { Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}", VerticalAlignment = VerticalAlignment.Center, FontFamily = new System.Windows.Media.FontFamily("Consolas") }
                            }
                        };
                        colorButton.Click += PropertyColor_Click;
                        InstancePropertyPanel.Children.Add(colorButton);
                        break;
                    case "slider":
                        var sliderValue = value.GetDouble();
                        var valueLabel = new TextBlock { Text = sliderValue.ToString($"F{definition.Fraction ?? 2}", CultureInfo.InvariantCulture),
                            Foreground = (Brush)FindResource("Muted"), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
                        InstancePropertyPanel.Children.Add(valueLabel);
                        var slider = new Slider { Minimum = definition.Minimum!.Value, Maximum = definition.Maximum!.Value,
                            Value = sliderValue, SmallChange = definition.Step ?? Math.Max((definition.Maximum.Value - definition.Minimum.Value) / 100, 0.001),
                            TickFrequency = definition.Step ?? 0, IsSnapToTickEnabled = definition.Step is > 0,
                            Tag = new SliderEditContext(context, valueLabel), IsEnabled = definition.ConditionSupported };
                        slider.ValueChanged += PropertySlider_ValueChanged;
                        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(PropertySlider_DragCompleted));
                        slider.KeyUp += PropertySlider_KeyUp;
                        InstancePropertyPanel.Children.Add(slider);
                        break;
                }
                if (!definition.ConditionSupported)
                    InstancePropertyPanel.Children.Add(new TextBlock { Text = settings.Language == "zh-CN" ? "不支持此属性的显示条件" : "Unsupported display condition",
                        Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, FontSize = 11 });
            }
            AddPropertyDiagnostics(item);
        }
        finally { renderingPropertyEditors = false; }
    }

    private string PropertyTitle(WallpaperPropertyDefinition definition)
    {
        if (definition.Text == "ui_browse_properties_scheme_color")
            return settings.Language == "zh-CN" ? "配色" : "Color scheme";
        if (definition.Text is { Length: > 0 } text && !text.StartsWith("ui_", StringComparison.Ordinal)) return text;
        return System.Text.RegularExpressions.Regex.Replace(definition.Key, "([a-z0-9])([A-Z])", "$1 $2")
            .Replace('_', ' ').Replace('-', ' ');
    }

    private void AddPropertyDiagnostics(InstalledItem item)
    {
        foreach (var diagnostic in item.PropertyDiagnostics ?? [])
            InstancePropertyPanel.Children.Add(new TextBlock
            {
                Text = $"{(settings.Language == "zh-CN" ? "属性提示" : "Property note")}: {diagnostic}",
                Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap,
                FontSize = 11, Margin = new Thickness(0, 6, 0, 0)
            });
    }

    private void PropertyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!renderingPropertyEditors && sender is CheckBox { Tag: PropertyEditContext context } check)
            _ = RunActionAsync(() => SavePropertyValueAsync(context, System.Text.Json.JsonSerializer.SerializeToElement(check.IsChecked == true)));
    }

    private void PropertyCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!renderingPropertyEditors && sender is ComboBox { Tag: PropertyEditContext context, SelectedItem: PropertyOptionChoice choice })
            _ = RunActionAsync(() => SavePropertyValueAsync(context, choice.Option.Value));
    }

    private void PropertyText_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (renderingPropertyEditors || sender is not TextBox { Tag: PropertyEditContext context } textBox) return;
        var json = System.Text.Json.JsonSerializer.SerializeToElement(textBox.Text);
        if (WallpaperPropertyReader.IsValidValue(context.Definition, json))
            _ = RunActionAsync(() => SavePropertyValueAsync(context, json));
        else RenderPropertyEditors();
    }

    private void PropertyColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PropertyEditContext context }) return;
        var current = context.Definition.DefaultValue.GetString();
        if (SelectedComponent?.Config?.TryGetValue(context.Definition.Key, out var configured) == true && configured.ValueKind == System.Text.Json.JsonValueKind.String)
            current = configured.GetString();
        var initial = ColorPickerDialog.ParseWeColor(current);
        var selected = ColorPickerDialog.Show(this, initial, settings.Language == "zh-CN");
        if (selected is { } color)
        {
            var json = System.Text.Json.JsonSerializer.SerializeToElement(ColorPickerDialog.FormatWeColor(color));
            _ = RunActionAsync(() => SavePropertyValueAsync(context, json));
        }
    }

    private void PropertySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is Slider { Tag: SliderEditContext context } slider)
            context.ValueLabel.Text = slider.Value.ToString($"F{context.Edit.Definition.Fraction ?? 2}", CultureInfo.InvariantCulture);
    }

    private void PropertySlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!renderingPropertyEditors && sender is Slider { Tag: SliderEditContext context } slider)
            _ = RunActionAsync(() => SavePropertyValueAsync(context.Edit, System.Text.Json.JsonSerializer.SerializeToElement(slider.Value)));
    }

    private void PropertySlider_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!renderingPropertyEditors && sender is Slider { Tag: SliderEditContext context } slider)
            _ = RunActionAsync(() => SavePropertyValueAsync(context.Edit, System.Text.Json.JsonSerializer.SerializeToElement(slider.Value)));
    }

    private async Task SavePropertyValueAsync(PropertyEditContext context, System.Text.Json.JsonElement value)
    {
        var preset = SelectedPreset;
        if (preset is null || !WallpaperPropertyReader.IsValidValue(context.Definition, value)) return;
        var changed = preset.Components.Select(component =>
        {
            if (component.InstanceId != context.InstanceId) return component;
            var config = component.Config is null
                ? new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
                : new Dictionary<string, System.Text.Json.JsonElement>(component.Config, StringComparer.Ordinal);
            config[context.Definition.Key] = value.Clone();
            return component with { Config = config };
        }).ToArray();
        await ReplacePresetAsync(preset with { Components = changed });
    }

    private async Task SaveAsync(PresetState next, string? selectedId = null)
    {
        await store.WriteAsync(next);
        state = next;
        RenderPresets(selectedId);
    }

    private async Task ReplacePresetAsync(ComponentPreset changed) => await SaveAsync(state with
    {
        Presets = state.Presets.Select(preset => preset.Id == changed.Id ? changed : preset).ToArray()
    }, changed.Id);

    private void NewPresetButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(async () =>
    {
        var name = PromptForName(L("newPreset"));
        if (name is null) return;
        var preset = new ComponentPreset(Guid.NewGuid().ToString("N"), name, []);
        await SaveAsync(state with { Presets = [.. state.Presets, preset] }, preset.Id);
    });

    private void RenameButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(async () =>
    {
        var preset = SelectedPreset;
        if (preset is null) return;
        var name = PromptForName(L("rename"), preset.Name);
        if (name is not null) await ReplacePresetAsync(preset with { Name = name });
    });

    private void DeleteButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(async () =>
    {
        var preset = SelectedPreset;
        if (preset is null || !ThemedDialog.Confirm(this, "BAP Companion", L("deletePreset", preset.Name),
                settings.Language == "zh-CN" ? "删除" : "Delete", settings.Language == "zh-CN" ? "取消" : "Cancel")) return;
        await SaveAsync(state with
        {
            Presets = state.Presets.Where(item => item.Id != preset.Id).ToArray(),
            GlobalPresetId = state.GlobalPresetId == preset.Id ? null : state.GlobalPresetId,
            HostPresetIds = state.HostPresetIds.Where(pair => pair.Value != preset.Id).ToDictionary()
        });
    });

    private void AddComponentButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(async () =>
    {
        var preset = SelectedPreset;
        if (preset is null) return;
        var choices = items.Where(item => item.Role == "component" && item.Status == "valid" && item.BapId is not null)
            .Select(item => new CatalogPickerItem(item, item.Title, L(item.SourceKind),
                $"{L(item.Status)} · {item.Version ?? "—"}", LoadPreview(item.PreviewPath), item.PreviewPath is null ? L("noPreview") : ""))
            .ToArray();
        var selected = CatalogSelectionDialog.Show(this, L("chooseComponent"), choices, settings.Language);
        if (selected?.Value is not InstalledItem item || item.BapId is null) return;
        var order = preset.Components.Count;
        await ReplacePresetAsync(preset with { Components = [.. preset.Components, new(item.BapId, item.Key,
            Guid.NewGuid().ToString("N"), Layout: new ComponentLayout("automatic", Order: order, ZIndex: order))] });
    });

    private void AddHostButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(async () =>
    {
        var preset = SelectedPreset;
        if (preset is null || state.GlobalPresetId == preset.Id) return;
        var choices = GetHostCandidates()
            .Where(item => !state.HostPresetIds.TryGetValue(item.BapId!, out var assignedId) || assignedId != preset.Id)
            .Select(item =>
            {
                var assignedName = state.HostPresetIds.TryGetValue(item.BapId!, out var assignedId)
                    ? state.Presets.FirstOrDefault(candidate => candidate.Id == assignedId)?.Name ?? assignedId
                    : L("notAssigned");
                return new CatalogPickerItem(item, item.Title, L(item.SourceKind),
                    $"{L(item.Status)} · {L("currentlyAssigned", assignedName)}", LoadPreview(item.PreviewPath), item.PreviewPath is null ? L("noPreview") : "");
            }).ToArray();
        var selected = CatalogSelectionDialog.Show(this, L("chooseHost"), choices, settings.Language);
        if (selected?.Value is not InstalledItem item || item.BapId is null) return;
        if (state.HostPresetIds.TryGetValue(item.BapId, out var currentPresetId) && currentPresetId != preset.Id)
        {
            var currentName = state.Presets.FirstOrDefault(candidate => candidate.Id == currentPresetId)?.Name ?? currentPresetId;
            if (!ThemedDialog.Confirm(this, L("presets"), L("replaceHostPreset", currentName, preset.Name),
                    settings.Language == "zh-CN" ? "继续" : "Continue", settings.Language == "zh-CN" ? "取消" : "Cancel")) return;
        }
        var bindings = new Dictionary<string, string>(state.HostPresetIds, StringComparer.Ordinal) { [item.BapId] = preset.Id };
        await SaveAsync(state with { HostPresetIds = bindings }, preset.Id);
    });

    private void RemoveComponentButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(async () =>
    {
        var preset = SelectedPreset;
        var component = (SelectedComponents.SelectedItem as ComponentChoice)?.Component;
        if (preset is null || component is null) return;
        await ReplacePresetAsync(preset with { Components = preset.Components.Where(item => item.InstanceId != component.InstanceId).ToArray() });
    });

    private void MoveUpButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(() => MoveComponentAsync(-1));
    private void MoveDownButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(() => MoveComponentAsync(1));

    private void RemoveHostButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(async () =>
    {
        var hostId = (PresetHostsList.SelectedItem as PresetHostChoice)?.HostId;
        if (hostId is null) return;
        var bindings = new Dictionary<string, string>(state.HostPresetIds, StringComparer.Ordinal);
        bindings.Remove(hostId);
        await SaveAsync(state with { HostPresetIds = bindings }, SelectedPreset?.Id);
    });

    private async Task MoveComponentAsync(int offset)
    {
        var preset = SelectedPreset;
        var index = SelectedComponents.SelectedIndex;
        if (preset is null || index < 0 || index + offset < 0 || index + offset >= preset.Components.Count) return;
        var ordered = preset.Components.ToList();
        (ordered[index], ordered[index + offset]) = (ordered[index + offset], ordered[index]);
        ordered = ordered.Select((component, order) => component with
        {
            Layout = component.Layout is { Mode: "automatic" } layout ? layout with { Order = order, ZIndex = order } : component.Layout
        }).ToList();
        await ReplacePresetAsync(preset with { Components = ordered });
        SelectedComponents.SelectedIndex = index + offset;
    }

    private void SetGlobalButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(async () =>
    {
        if (SelectedPreset is { } preset) await SaveAsync(state with { GlobalPresetId = preset.Id }, preset.Id);
    });

    private string? PromptForName(string title, string value = "")
    {
        return ThemedDialog.Prompt(this, title, value,
            settings.Language == "zh-CN" ? "确定" : "OK",
            settings.Language == "zh-CN" ? "取消" : "Cancel");
    }

    private void LanguageChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (uiReady) settings.SetLanguage(LanguageChoice.SelectedIndex == 1 ? "zh-CN" : "en");
    }

    private void MonitorChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!uiReady || changingDisplayChoice || MonitorChoice.SelectedIndex < 0) return;
        try { settings.SetDisplayIndex(MonitorChoice.SelectedIndex); }
        catch (Exception error)
        {
            ThemedDialog.ShowMessage(this, L("settings"), error.Message, settings.Language == "zh-CN" ? "确定" : "OK");
            changingDisplayChoice = true;
            try { MonitorChoice.SelectedIndex = Math.Clamp(settings.DisplayIndex, 0, MonitorChoice.Items.Count - 1); }
            finally { changingDisplayChoice = false; }
        }
    }

    private void StartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!uiReady) return;
        try { settings.SetAutoStart(StartupCheck.IsChecked == true); StartupError.Text = ""; }
        catch (Exception error)
        {
            ThemedDialog.ShowMessage(this, L("settings"), error.Message, settings.Language == "zh-CN" ? "确定" : "OK");
            StartupCheck.IsChecked = settings.AutoStartEnabled;
        }
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e) => OpenPath(log.Path);
    private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e) => OpenPath(Path.GetDirectoryName(log.Path)!);
    private static void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private void CheckServiceButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(CheckServiceAsync);

    private async Task RefreshTrustedHostsAsync()
    {
        if (authorizationClient is null || TrustedHostsList is null) return;
        var selectedId = (TrustedHostsList.SelectedItem as TrustedHostChoice)?.Host.Id;
        var hosts = await authorizationClient.GetTrustedHostsAsync();
        TrustedHostsList.ItemsSource = hosts.Select(host => new TrustedHostChoice(host,
            $"{host.Name}  ·  {host.Id}  ·  {string.Join(", ", host.Permissions)}")).ToArray();
        TrustedHostsList.SelectedItem = TrustedHostsList.Items.Cast<TrustedHostChoice>().FirstOrDefault(item => item.Host.Id == selectedId);
    }

    private void RevokeTrustButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(async () =>
    {
        if (authorizationClient is null || TrustedHostsList.SelectedItem is not TrustedHostChoice selected) return;
        await authorizationClient.RevokeTrustAsync(selected.Host.Id);
        await RefreshTrustedHostsAsync();
    });

    private async Task CheckServiceAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await client.GetAsync($"http://127.0.0.1:{port}/healthz");
            ServiceStatus.Text = response.IsSuccessStatusCode ? L("serviceOnline") : $"{L("status")}: HTTP {(int)response.StatusCode}";
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { ServiceStatus.Text = L("serviceOffline"); }
        SidebarServiceStatus.Text = ServiceStatus.Text;
    }

    private sealed record CatalogEntry(InstalledItem Item, string Title, string Source, string Summary, ImageSource? Preview, string NoPreview);
    private sealed record PresetChoice(ComponentPreset Preset) { public override string ToString() => Preset.Name; }
    private sealed record ComponentChoice(PresetComponent Component, string Title, string Source, string Summary, ImageSource? Preview, string NoPreview);
    private sealed record PresetHostChoice(string? HostId, string Title, string Source, string Summary, ImageSource? Preview, string NoPreview, bool IsGlobal);
    private sealed record PropertyEditContext(string InstanceId, WallpaperPropertyDefinition Definition);
    private sealed record SliderEditContext(PropertyEditContext Edit, TextBlock ValueLabel);
    private sealed record PropertyOptionChoice(WallpaperPropertyOption Option) { public string Label => Option.Text; }
    private sealed record TrustedHostChoice(TrustedHost Host, string Label) { public override string ToString() => Label; }
}
