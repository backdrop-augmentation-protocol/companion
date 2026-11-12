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
    private PresetState state = new([], null, new Dictionary<string, IReadOnlyList<string>>());
    private IReadOnlyList<InstalledItem> items = [];
    private InstalledItem? selectedItem;
    private bool uiReady;
    private bool changingCatalogSelection;
    private bool changingDisplayChoice;
    private bool showingPresetHosts;
    private readonly HostAuthorizationClient? authorizationClient;
    private readonly DispatcherTimer authorizationTimer;
    private IReadOnlyList<RuntimeHostSnapshot> runtimeSnapshots = [];
    private RuntimeComponentChoice? selectedRuntimeComponent;
    private readonly Dictionary<string, long> runtimeCommandRevisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ComponentLayout> runtimeLayoutCommands = new(StringComparer.Ordinal);
    private bool refreshingRuntime;
    private bool changingRuntimeSelection;
    private bool showingAuthorizationPrompt;
    private int creatorLookupGeneration;
    private bool renderingPropertyEditors;
    private bool renderingComponentChoices;
    private bool renderingPresets;
    private string? selectedComponentInstanceId;
    private string? activeHostKey;

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
            if (request is not null)
            {
                if (!IsVisible) Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                var approved = HostApprovalDialog.Show(this, request, settings.Language, out var permissions, out var remember);
                var applied = await authorizationClient.DecideAsync(request.SessionId, approved, permissions, remember);
                if (!applied) log.Write("authorization", $"Request {request.SessionId} was no longer pending when the decision was submitted.");
                else log.Write("authorization", $"Host request {request.SessionId} {(approved ? "approved" : "denied")}{(remember ? " and remembered on this device" : " for this session")}.");
            }
        }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException or System.Text.Json.JsonException)
        {
            log.Write("authorization", error.Message);
        }
        finally { showingAuthorizationPrompt = false; }
        await RefreshRuntimeAsync();
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
        DesktopNav.Content = L("desktop");
        InstalledTitle.Text = L("installed"); CatalogHint.Text = L("catalogHint"); RefreshButton.Content = L("refresh");
        PropertyRefreshButton.Content = L("refreshProperties");
        HostsHeading.Text = L("hosts"); ComponentsHeading.Text = L("components");
        NoHosts.Text = L("noHosts"); NoComponents.Text = L("noComponents");
        DetailsHeading.Text = L("details"); DetailEmpty.Text = L("selectItem");
        LocationLabel.Text = L("location"); MonitorLabel.Text = L("switchDisplay"); MonitorHint.Text = L("switchDisplayHint");
        SwitchHostButton.Content = L("switchHost");
        PresetsTitle.Text = L("presets"); PresetIntro.Text = L("presetIntro");
        DesktopTitle.Text = L("desktop"); DesktopIntro.Text = L("desktopIntro");
        DesktopHostHeading.Text = L("desktopCurrentHost"); DesktopComponentsHeading.Text = L("desktopRuntimeComponents");
        PropertyRefreshButton.Content = L("refreshProperties");
        DesktopComponentsEmpty.Text = L("desktopWaiting");
        NewPresetButton.Content = L("newPreset"); RenameButton.Content = L("rename"); DeleteButton.Content = L("delete");
        PresetUpButton.Content = L("presetUp"); PresetDownButton.Content = L("presetDown");
        SelectedComponentsHeading.Text = L(showingPresetHosts ? "hostsUsingPreset" : "selectedComponents");
        PropertyHeading.Text = L("componentProperties");
        if (SelectedComponents is not null) RenderPropertyEditors();
        ComponentsModeButton.Content = L("presetComponentsTab"); HostsModeButton.Content = L("presetHostsTab");
        AddComponentButton.Content = L("addComponent"); AddHostButton.Content = L("addHost");
        RemoveComponentButton.Content = L("remove"); RemovePresetButton.Content = L("removePreset");
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
        RenderDesktop();
    }

    private async Task RefreshRuntimeAsync()
    {
        if (authorizationClient is null || refreshingRuntime) return;
        refreshingRuntime = true;
        try
        {
            var next = await authorizationClient.GetRuntimeSnapshotsAsync();
            var merged = new List<RuntimeHostSnapshot>(next.Count);
            foreach (var snapshot in next)
            {
                var oldSnapshot = runtimeSnapshots.FirstOrDefault(candidate => candidate.SessionId == snapshot.SessionId);
                var instances = snapshot.Instances.Select(instance =>
                {
                    var key = RuntimeCommandKey(snapshot.SessionId, instance.InstanceId);
                    var previous = oldSnapshot?.Instances.FirstOrDefault(candidate => candidate.InstanceId == instance.InstanceId);
                    if (runtimeCommandRevisions.TryGetValue(key, out var revision))
                    {
                        if (instance.ConfigRevision >= revision) runtimeCommandRevisions.Remove(key);
                        else if (previous?.ConfigRevision == revision)
                            instance = instance with { Config = previous.Config, ConfigRevision = revision };
                    }
                    if (runtimeLayoutCommands.TryGetValue(key, out var layout))
                    {
                        if (Equals(instance.Layout, layout)) runtimeLayoutCommands.Remove(key);
                        else instance = instance with { Layout = layout };
                    }
                    return instance;
                }).ToArray();
                merged.Add(snapshot with { Instances = instances });
            }
            var changed = !RuntimeSnapshotsEqual(runtimeSnapshots, merged);
            runtimeSnapshots = merged;
            if (changed && DesktopPage.Visibility == Visibility.Visible) RenderDesktop();
        }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException or System.Text.Json.JsonException)
        {
            log.Write("runtime-desktop", error.Message);
        }
        finally { refreshingRuntime = false; }
    }

    private static string RuntimeCommandKey(string sessionId, string instanceId) => sessionId + "\n" + instanceId;

    private static bool RuntimeSnapshotsEqual(IReadOnlyList<RuntimeHostSnapshot> left, IReadOnlyList<RuntimeHostSnapshot> right) =>
        left.Count == right.Count && left.All(snapshot => right.FirstOrDefault(candidate => candidate.SessionId == snapshot.SessionId) is { } next &&
            snapshot.HostId == next.HostId && snapshot.HostName == next.HostName &&
            System.Text.Json.JsonSerializer.Serialize(snapshot.Instances) == System.Text.Json.JsonSerializer.Serialize(next.Instances));

    private void RenderDesktop()
    {
        if (DesktopHostsList is null) return;
        changingRuntimeSelection = true;
        try
        {
            var currentHost = activeHostKey is null ? null : FindHost(activeHostKey);
            DesktopHostsList.ItemsSource = currentHost is null ? Array.Empty<CatalogEntry>() : new[] { CreateEntry(currentHost) };
            DesktopHostsList.SelectedIndex = currentHost is null ? -1 : 0;
            var snapshot = currentHost?.BapId is { } hostId
                ? runtimeSnapshots.Where(candidate => candidate.HostId == hostId)
                    .OrderByDescending(candidate => candidate.UpdatedAt).FirstOrDefault()
                : null;
            var previousInstanceId = selectedRuntimeComponent?.Instance.InstanceId;
            var instanceNumbers = new Dictionary<string, int>(StringComparer.Ordinal);
            var choices = snapshot?.Instances.Select(instance =>
            {
                var instanceNumber = instanceNumbers.GetValueOrDefault(instance.ComponentId) + 1;
                instanceNumbers[instance.ComponentId] = instanceNumber;
                var instanceLabel = L("instanceNumber", instanceNumber);
                var item = items.FirstOrDefault(candidate => candidate.Role == "component" && candidate.BapId == instance.ComponentId &&
                    (instance.SourceKey is null || candidate.Key == instance.SourceKey));
                return new RuntimeComponentChoice(snapshot, instance, item,
                    item?.Title ?? instance.ComponentId, instance.InstanceId, instanceLabel,
                    RuntimeStateText(instance.State) + " · " + instanceLabel, LoadPreview(item?.PreviewPath),
                    item?.PreviewPath is null ? L("noPreview") : "");
            }).ToArray() ?? [];
            DesktopComponentsList.ItemsSource = choices;
            selectedRuntimeComponent = choices.FirstOrDefault(choice => choice.Instance.InstanceId == previousInstanceId);
            if (selectedRuntimeComponent is not null) DesktopComponentsList.SelectedItem = selectedRuntimeComponent;
            else DesktopComponentsList.SelectedIndex = -1;
            DesktopComponentsEmpty.Visibility = choices.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            DesktopComponentsEmpty.Text = currentHost is null ? L("desktopNoHost") : snapshot is null ? L("desktopWaiting") : L("desktopNoComponents");
        }
        finally { changingRuntimeSelection = false; }
        if (selectedRuntimeComponent is null)
        {
            if (DesktopHostsList.SelectedItem is CatalogEntry entry) RenderPresetHostDetails(entry.Item);
            else
            {
                PresetHostDetailPanel.Visibility = Visibility.Collapsed;
                TransformPanel.Visibility = Visibility.Collapsed;
                InstancePropertyPanel.Children.Clear();
                PropertyHeading.Text = L("hostProperties");
                PropertyHint.Text = L("desktopNoHost");
            }
        }
        else RenderPropertyEditors();
    }

    private void DesktopHostsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingRuntimeSelection) return;
        selectedRuntimeComponent = null;
        DesktopComponentsList.SelectedItem = null;
        RenderDesktop();
    }

    private void DesktopHostsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DesktopHostsList.SelectedItem is not CatalogEntry) return;
        ShowDesktopHostProperties();
    }

    private void ShowDesktopHostProperties()
    {
        selectedRuntimeComponent = null;
        changingRuntimeSelection = true;
        try { DesktopComponentsList.SelectedItem = null; }
        finally { changingRuntimeSelection = false; }
        RenderDesktop();
    }

    private void DesktopComponentsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingRuntimeSelection) return;
        selectedRuntimeComponent = DesktopComponentsList.SelectedItem as RuntimeComponentChoice;
        PropertyScrollViewer.ScrollToTop();
        RenderPropertyEditors();
    }

    private void SelectPage(UIElement page, Button nav)
    {
        InstalledPage.Visibility = page == InstalledPage ? Visibility.Visible : Visibility.Collapsed;
        PresetsPage.Visibility = page == PresetsPage ? Visibility.Visible : Visibility.Collapsed;
        DesktopPage.Visibility = page == DesktopPage ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == SettingsPage ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { InstalledNav, PresetsNav, DesktopNav, SettingsNav })
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
    private void DesktopNav_Click(object sender, RoutedEventArgs e)
    {
        SelectPage(DesktopPage, DesktopNav);
        RenderDesktop();
    }
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => SelectPage(SettingsPage, SettingsNav);
    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(RefreshAsync);
    private void PropertyRefreshButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(RefreshAsync);

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
        if (authorizationClient is not null) await authorizationClient.RefreshCatalogAsync();
        var discovered = await catalog.DiscoverAsync(force: true);
        activeHostKey = await FindActiveHostKeyAsync(discovered);
        var presets = await store.ReadAsync();
        var migrated = PresetSelection.MigrateInstalledHostBindings(presets, discovered);
        if (!PresetSelection.HostBindingsEqual(presets, migrated))
        {
            await store.WriteAsync(migrated);
            presets = migrated;
        }
        RenderSnapshot(discovered, presets);
        await CheckServiceAsync();
        await RefreshRuntimeAsync();
    }

    internal void RenderSnapshot(IReadOnlyList<InstalledItem> discovered, PresetState presets)
    {
        items = discovered;
        state = presets;
        selectedItem = items.FirstOrDefault(item => item.Key == selectedItem?.Key);
        RenderCatalog();
        RenderPresets();
        RenderDetails();
        RenderDesktop();
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

    private string RuntimeStateText(string state) => settings.Language == "zh-CN"
        ? state switch
        {
            "running" => "\u8fd0\u884c\u4e2d",
            "paused" => "\u5df2\u6682\u505c",
            "failed" => "\u5df2\u6545\u969c",
            "stopped" => "\u5df2\u505c\u6b62",
            "created" or "connected" or "initialized" => "\u52a0\u8f7d\u4e2d",
            _ => state
        }
        : state switch
        {
            "running" => "Running",
            "paused" => "Paused",
            "failed" => "Failed",
            "stopped" => "Stopped",
            "created" or "connected" or "initialized" => "Loading",
            _ => state
        };

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
            var result = await (authorizationClient ?? throw new InvalidOperationException("The Companion service client is unavailable."))
                .SwitchWallpaperAsync(selectedItem.Key, EffectiveDisplayIndex);
            SwitchStatus.Text = L(result.Status == "confirmed" ? "switchVerified" : "switchSent");
        }
        finally { SwitchHostButton.IsEnabled = true; }
    });

    private void PresetSwitchHostButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(async () =>
    {
        var key = (PresetHostsList.SelectedItem as PresetHostChoice)?.HostKey;
        var host = key is null ? null : FindHost(key);
        if (host is null) return;
        PresetSwitchHostButton.IsEnabled = false;
        try
        {
            var result = await (authorizationClient ?? throw new InvalidOperationException("The Companion service client is unavailable."))
                .SwitchWallpaperAsync(host.Key, EffectiveDisplayIndex);
            PresetSwitchHostStatus.Text = L(result.Status == "confirmed" ? "switchVerified" : "switchSent");
            activeHostKey = await FindActiveHostKeyAsync(items);
            RenderPresetHosts(host.Key);
        }
        finally { PresetSwitchHostButton.IsEnabled = true; }
    });

    internal int EffectiveDisplayIndex => Math.Clamp(settings.DisplayIndex, 0, Math.Max(0, Forms.Screen.AllScreens.Length - 1));

    private ComponentPreset? SelectedPreset
    {
        get
        {
            var selectedId = (PresetList.SelectedItem as PresetChoice)?.Preset.Id;
            return selectedId is null ? null : state.Presets.FirstOrDefault(preset => preset.Id == selectedId);
        }
    }

    private void RenderPresets(string? selectId = null)
    {
        if (PresetList is null) return;
        selectId ??= SelectedPreset?.Id;
        renderingPresets = true;
        try
        {
            PresetList.SelectedItem = null;
            PresetList.ItemsSource = state.Presets.Select(preset => new PresetChoice(preset, L("disabledPresetMark"))).ToArray();
            PresetList.SelectedItem = PresetList.Items.Cast<PresetChoice>().FirstOrDefault(choice => choice.Preset.Id == selectId);
        }
        finally { renderingPresets = false; }
        RenderSelectedComponents();
        RenderPresetHosts();
        UpdatePresetOrderActions();
    }

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (renderingPresets) return;
        RenderSelectedComponents();
        RenderPresetHosts();
        UpdatePresetOrderActions();
    }

    private void UpdatePresetOrderActions()
    {
        var index = SelectedPreset is { } preset ? state.Presets.ToList().FindIndex(item => item.Id == preset.Id) : -1;
        PresetUpButton.IsEnabled = index > 0;
        PresetDownButton.IsEnabled = index >= 0 && index < state.Presets.Count - 1;
        TogglePresetButton.IsEnabled = index >= 0;
        if (index >= 0) TogglePresetButton.Content = L(state.Presets[index].Enabled ? "disablePreset" : "enablePreset");
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
        RemovePresetButton.Visibility = hosts ? Visibility.Visible : Visibility.Collapsed;
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
        PropertyScrollViewer.ScrollToTop();
        UpdateComponentActions();
        RenderPropertyEditors();
    }

    private void RenderSelectedComponents()
    {
        if (SelectedComponents is null) return;
        var previousInstanceId = selectedComponentInstanceId;
        var selectedInstanceId = (SelectedComponents.SelectedItem as ComponentChoice)?.Component.InstanceId ?? selectedComponentInstanceId;
        renderingComponentChoices = true;
        try
        {
            SelectedComponents.SelectedItem = null;
            var instanceNumbers = new Dictionary<string, int>(StringComparer.Ordinal);
            SelectedComponents.ItemsSource = (SelectedPreset?.Components ?? [])
                .Select(component =>
                {
                    var instanceNumber = instanceNumbers.GetValueOrDefault(component.ComponentId) + 1;
                    instanceNumbers[component.ComponentId] = instanceNumber;
                    var instanceLabel = L("instanceNumber", instanceNumber);
                    var item = items.FirstOrDefault(candidate => candidate.BapId == component.ComponentId &&
                        (component.SourceKey is null || candidate.Key == component.SourceKey));
                    return new ComponentChoice(component,
                        item?.Title ?? component.ComponentId,
                        item is null ? L("missing") : L(item.SourceKind),
                        $"{(item is null ? L("missing") : L(item.Status))} · {instanceLabel}", instanceLabel,
                        LoadPreview(item?.PreviewPath), item?.PreviewPath is null ? L("noPreview") : "");
                }).ToArray();
            SelectedComponents.SelectedItem = SelectedComponents.Items.Cast<ComponentChoice>()
                .FirstOrDefault(choice => choice.Component.InstanceId == selectedInstanceId);
            ComponentsEmpty.Visibility = !showingPresetHosts && SelectedComponents.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            selectedComponentInstanceId = (SelectedComponents.SelectedItem as ComponentChoice)?.Component.InstanceId;
        }
        finally { renderingComponentChoices = false; }
        if (previousInstanceId != selectedComponentInstanceId) PropertyScrollViewer.ScrollToTop();
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

    private void RenderPresetHosts(string? preferredHostKey = null)
    {
        if (PresetHostsList is null) return;
        var preset = SelectedPreset;
        var selectedHostKey = (PresetHostsList.SelectedItem as PresetHostChoice)?.HostKey;
        PresetHostChoice[] hosts;
        if (preset is null) hosts = [];
        else if (state.GlobalPresetId == preset.Id)
        {
            var active = FindHost(activeHostKey ?? "");
            var globalHosts = new List<PresetHostChoice>();
            if (active is not null) globalHosts.Add(CreatePresetHostChoice(active, true));
            globalHosts.Add(new PresetHostChoice(null, L("allHosts"), L("globalPreset"), preset.Name, null, "", true));
            hosts = globalHosts.ToArray();
        }
        else
        {
            hosts = state.HostPresetIds.Where(binding => binding.Value.Contains(preset.Id, StringComparer.Ordinal))
                .Select(binding =>
                {
                    var item = FindHost(binding.Key);
                    return new PresetHostChoice(binding.Key, item?.Title ?? binding.Key,
                        item is null ? L("missing") : L(item.SourceKind),
                        item is null ? binding.Key : $"{L(item.Status)} · {item.Version ?? "—"}",
                        LoadPreview(item?.PreviewPath), item?.PreviewPath is null ? L("noPreview") : "", false);
                }).Select(choice => choice with { IsCurrent = choice.HostKey == activeHostKey })
                .OrderByDescending(choice => choice.IsCurrent).ThenBy(choice => choice.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        PresetHostsList.ItemsSource = hosts;
        PresetHostsList.SelectedItem = hosts.FirstOrDefault(host => host.HostKey == preferredHostKey)
            ?? hosts.FirstOrDefault(host => host.HostKey == selectedHostKey)
            ?? hosts.FirstOrDefault(host => host.IsCurrent)
            ?? hosts.FirstOrDefault(host => host.IsGlobal)
            ?? hosts.FirstOrDefault();
        HostsEmpty.Visibility = showingPresetHosts && hosts.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddHostButton.IsEnabled = preset is not null && state.GlobalPresetId != preset.Id && GetAvailableHostCandidates(preset).Count > 0;
        AddHostButton.Visibility = showingPresetHosts && preset is not null ? Visibility.Visible : Visibility.Collapsed;
        SetGlobalButton.IsEnabled = preset is not null && state.GlobalPresetId != preset.Id;
        SetGlobalButton.Content = L(preset is not null && state.GlobalPresetId == preset.Id ? "globalPreset" : "setGlobal");
        SetGlobalButton.Style = (Style)FindResource(preset is not null && state.GlobalPresetId == preset.Id ? "PrimaryButton" : "QuietButton");
        PresetStatus.Text = $"{L("global")}: {state.Presets.FirstOrDefault(entry => entry.Id == state.GlobalPresetId)?.Name ?? L("allInstalled")}\n{L("overrides")}: {state.HostPresetIds.Count}";
        UpdateHostActions();
        RenderPresetHostDetails();
    }

    private PresetHostChoice CreatePresetHostChoice(InstalledItem item, bool current) => new(item.Key, item.Title,
        current ? $"{L(item.SourceKind)} · {L("currentWallpaper")}" : L(item.SourceKind),
        $"{L(item.Status)} · {item.Version ?? "—"}", LoadPreview(item.PreviewPath), item.PreviewPath is null ? L("noPreview") : "", false, current);

    private void PresetHostsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateHostActions();
    }

    private void RenderPresetHostDetails(InstalledItem? desktopItem = null)
    {
        var desktopMode = DesktopPage.Visibility == Visibility.Visible;
        if (PresetHostDetailPanel is null || !desktopMode) return;
        var item = desktopItem ?? (DesktopHostsList.SelectedItem as CatalogEntry)?.Item;
        var isCurrent = item is not null && (desktopMode || item.Key == activeHostKey);
        PresetHostDetailPanel.Visibility = item is not null && !isCurrent ? Visibility.Visible : Visibility.Collapsed;
        PropertyHint.Visibility = item is not null && !isCurrent ? Visibility.Collapsed : Visibility.Visible;
        TransformPanel.Visibility = Visibility.Collapsed;
        InstancePropertyPanel.Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed;
        if (item is null)
        {
            PropertyHeading.Text = settings.Language == "zh-CN" ? "宿主属性" : "Host properties";
            PropertyHint.Text = settings.Language == "zh-CN" ? "当前壁纸未出现在此预设的宿主列表中。" : "The active wallpaper is not listed in this preset.";
            InstancePropertyPanel.Children.Clear();
            return;
        }
        if (!isCurrent)
        {
            PropertyHeading.Text = settings.Language == "zh-CN" ? "宿主详情" : "Host details";
            PresetHostPreview.Source = LoadPreview(item.PreviewPath);
            PresetHostTitle.Text = item.Title;
            PresetHostMetadata.Text = $"{L("source")}: {L(item.SourceKind)}\n{L("status")}: {L(item.Status)}\n{L("bapId")}: {item.BapId ?? "—"}\n{L("version")}: {item.Version ?? "—"}";
            PresetHostPath.Text = item.ProjectDirectory;
            PresetSwitchHostButton.Content = L("switchHost");
            PresetSwitchHostButton.IsEnabled = true;
            PresetSwitchHostStatus.Text = "";
            InstancePropertyPanel.Children.Clear();
            return;
        }

        PropertyHeading.Text = settings.Language == "zh-CN" ? "宿主属性" : "Host properties";
        renderingPropertyEditors = true;
        try
        {
            InstancePropertyPanel.Children.Clear();
            var definitions = item.Properties ?? [];
            if (definitions.Count == 0)
            {
                PropertyHint.Text = item.PropertyDiagnostics is { Count: > 0 }
                    ? (settings.Language == "zh-CN" ? "没有可编辑属性；部分声明无效。" : "No editable properties; some declarations are invalid.")
                    : (settings.Language == "zh-CN" ? "此宿主没有可编辑的 WE 属性。" : "This host declares no editable Wallpaper Engine properties.");
                AddPropertyDiagnostics(item);
                return;
            }
            PropertyHint.Text = settings.Language == "zh-CN"
                ? $"{item.Title} · 设置保存在此宿主项目，并同步到当前壁纸。"
                : $"{item.Title} · settings are stored per host project and applied to the active wallpaper.";
            var config = state.HostConfigs?.GetValueOrDefault(item.Key) ?? [];
            var values = definitions.ToDictionary(definition => definition.Key,
                definition => config.TryGetValue(definition.Key, out var configured) && WallpaperPropertyReader.IsValidValue(definition, configured)
                    ? configured : definition.DefaultValue, StringComparer.Ordinal);
            int? previousGroup = null;
            foreach (var definition in definitions)
            {
                if (definition.ConditionSupported && !WallpaperPropertyReader.IsConditionMet(definition, values)) continue;
                var group = Math.Max(0, definition.Order) / 10;
                var lead = previousGroup != group;
                if (lead)
                {
                    InstancePropertyPanel.Children.Add(new TextBlock { Text = PropertyTitle(definition), Foreground = (Brush)FindResource("Muted"), FontWeight = FontWeights.SemiBold, FontSize = 11, Margin = new Thickness(0, previousGroup is null ? 4 : 14, 0, 6) });
                    previousGroup = group;
                }
                if (!lead && definition.Type != "bool") InstancePropertyPanel.Children.Add(new TextBlock { Text = PropertyTitle(definition), Foreground = (Brush)FindResource("Ink"), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 4) });
                var context = new PropertyEditContext("host", item.Key, null, definition);
                var value = values[definition.Key];
                switch (definition.Type)
                {
                    case "bool":
                        var check = new CheckBox { Content = lead ? null : PropertyTitle(definition), IsChecked = value.ValueKind == System.Text.Json.JsonValueKind.True, Tag = context, Margin = new Thickness(0, 0, 0, 5), IsEnabled = definition.ConditionSupported };
                        check.Checked += PropertyCheck_Changed; check.Unchecked += PropertyCheck_Changed; InstancePropertyPanel.Children.Add(check); break;
                    case "combo":
                        var combo = new ComboBox { Tag = context, Margin = new Thickness(0, 0, 0, 5), IsEnabled = definition.ConditionSupported };
                        combo.ItemsSource = (definition.Options ?? []).Select(option => new PropertyOptionChoice(option, UiStrings.PropertyText(WallpaperPropertyReader.ResolveLocalizedLabel(option.LocalizedLabels, settings.Language) ?? option.Text, settings.Language))).ToArray();
                        combo.DisplayMemberPath = nameof(PropertyOptionChoice.Label);
                        combo.SelectedItem = combo.Items.Cast<PropertyOptionChoice>().FirstOrDefault(option => System.Text.Json.JsonElement.DeepEquals(option.Option.Value, value));
                        combo.SelectionChanged += PropertyCombo_Changed; InstancePropertyPanel.Children.Add(combo); break;
                    case "textinput":
                        var text = new TextBox { Text = value.GetString() ?? "", Tag = context, Style = (Style)FindResource("DialogTextBox"), IsEnabled = definition.ConditionSupported };
                        text.LostKeyboardFocus += PropertyText_LostKeyboardFocus; InstancePropertyPanel.Children.Add(text); break;
                    case "color":
                        var color = ColorPickerDialog.ParseWeColor(value.GetString());
                        var colorButton = new Button { Tag = context, ToolTip = settings.Language == "zh-CN" ? "选择颜色" : "Choose color", Style = (Style)FindResource("QuietButton"), IsEnabled = definition.ConditionSupported, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 3), Content = $"#{color.R:X2}{color.G:X2}{color.B:X2}" };
                        colorButton.Click += PropertyColor_Click; InstancePropertyPanel.Children.Add(colorButton); break;
                    case "slider":
                        var number = value.GetDouble();
                        var label = new TextBlock { Text = number.ToString($"F{definition.Fraction ?? 2}", CultureInfo.InvariantCulture), Foreground = (Brush)FindResource("Muted"), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
                        InstancePropertyPanel.Children.Add(label);
                        var slider = new Slider { Minimum = definition.Minimum!.Value, Maximum = definition.Maximum!.Value, Value = number, SmallChange = definition.Step ?? 0.01, Tag = new SliderEditContext(context, label), IsEnabled = definition.ConditionSupported };
                        slider.ValueChanged += PropertySlider_ValueChanged; slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(PropertySlider_DragCompleted)); slider.KeyUp += PropertySlider_KeyUp; InstancePropertyPanel.Children.Add(slider); break;
                }
            }
            AddPropertyDiagnostics(item);
        }
        finally { renderingPropertyEditors = false; }
    }

    private void UpdateHostActions()
    {
        var selected = PresetHostsList.SelectedItem as PresetHostChoice;
        var canRemove = selected is { IsGlobal: true } ||
            selected?.HostKey is { } hostKey && SelectedPreset is { } preset &&
            state.HostPresetIds.TryGetValue(hostKey, out var assigned) && assigned.Contains(preset.Id, StringComparer.Ordinal);
        RemovePresetButton.IsEnabled = canRemove;
        RemovePresetButton.Visibility = showingPresetHosts ? Visibility.Visible : Visibility.Collapsed;
    }

    private InstalledItem? FindHost(string hostKey) => items.FirstOrDefault(item => item.Role == "host" && item.Key == hostKey);

    private async Task<string?> FindActiveHostKeyAsync(IReadOnlyList<InstalledItem> candidates)
    {
        string current;
        try { current = await WallpaperEngineControl.GetCurrentAsync(EffectiveDisplayIndex); }
        catch (Exception error) when (error is FileNotFoundException or InvalidOperationException or TimeoutException) { return null; }
        if (string.IsNullOrWhiteSpace(current)) return null;
        string currentPath;
        try { currentPath = Path.GetFullPath(current); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return null; }
        foreach (var host in candidates.Where(item => item.Role == "host"))
        {
            var projectPath = Path.Combine(host.ProjectDirectory, "project.json");
            if (string.Equals(Path.GetFullPath(projectPath), currentPath, StringComparison.OrdinalIgnoreCase)) return host.Key;
            try
            {
                using var project = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(projectPath));
                if (project.RootElement.TryGetProperty("file", out var entry) && entry.ValueKind == System.Text.Json.JsonValueKind.String &&
                    string.Equals(Path.GetFullPath(Path.Combine(host.ProjectDirectory, entry.GetString()!)), currentPath, StringComparison.OrdinalIgnoreCase)) return host.Key;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NotSupportedException) { }
        }
        return null;
    }

    private IReadOnlyList<InstalledItem> GetHostCandidates() => items
        .Where(item => item.Role == "host")
        .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(item => item.SourceKind, StringComparer.Ordinal)
        .ThenBy(item => item.Key, StringComparer.Ordinal)
        .ToArray();

    private IReadOnlyList<InstalledItem> GetAvailableHostCandidates(ComponentPreset preset) => GetHostCandidates()
        .Where(item => !state.HostPresetIds.TryGetValue(item.Key, out var assignedIds) || !assignedIds.Contains(preset.Id, StringComparer.Ordinal))
        .ToArray();

    private PresetComponent? SelectedComponent => (SelectedComponents.SelectedItem as ComponentChoice)?.Component;

    private void RenderPropertyEditors()
    {
        if (InstancePropertyPanel is null) return;
        if (DesktopPage.Visibility != Visibility.Visible)
        {
            PresetHostDetailPanel.Visibility = Visibility.Collapsed;
            TransformPanel.Children.Clear();
            InstancePropertyPanel.Children.Clear();
            return;
        }
        if (DesktopPage.Visibility == Visibility.Visible && DesktopHostsList.SelectedItem is CatalogEntry desktopHost && selectedRuntimeComponent is null)
        {
            RenderPresetHostDetails(desktopHost.Item);
            return;
        }
        var runtimeMode = DesktopPage.Visibility == Visibility.Visible;
        PresetHostDetailPanel.Visibility = Visibility.Collapsed;
        PropertyHint.Visibility = Visibility.Visible;
        TransformPanel.Visibility = Visibility.Visible;
        InstancePropertyPanel.Visibility = Visibility.Visible;
        PropertyHeading.Text = L("componentProperties");
        renderingPropertyEditors = true;
        try
        {
            TransformPanel.Children.Clear();
            InstancePropertyPanel.Children.Clear();
            var component = SelectedComponent;
            var preset = SelectedPreset;
            var runtimeComponent = runtimeMode ? selectedRuntimeComponent : null;
            var componentId = runtimeComponent?.Instance.ComponentId ?? component?.ComponentId;
            var sourceKey = runtimeComponent?.Instance.SourceKey ?? component?.SourceKey;
            var instanceId = runtimeComponent?.Instance.InstanceId ?? component?.InstanceId;
            var config = runtimeComponent?.Instance.Config ?? component?.Config;
            var item = runtimeComponent?.Item ?? (componentId is null ? null : items.FirstOrDefault(candidate => candidate.BapId == componentId &&
                (sourceKey is null || candidate.Key == sourceKey)));
            if (instanceId is null || item is null)
            {
                PropertyHint.Text = runtimeMode ? L("desktopWaiting") : "Select a component instance to edit its preset values.";
                return;
            }

            if (runtimeComponent is not null)
            {
                var runtimePresetComponent = new PresetComponent(componentId!, sourceKey, instanceId, Layout: runtimeComponent.Instance.Layout);
                RenderLayoutEditors(runtimePresetComponent,
                    new ComponentPreset("runtime", "runtime", [runtimePresetComponent]), runtimeComponent.Snapshot.SessionId);
            }
            else if (!runtimeMode && component is not null && preset is not null) RenderLayoutEditors(component, preset);

            if (item.Properties is not { Count: > 0 } definitions)
            {
                PropertyHint.Text = item.PropertyDiagnostics is { Count: > 0 }
                    ? (settings.Language == "zh-CN" ? "没有可编辑属性；部分声明存在问题。" : "No editable properties; some declarations are invalid.")
                    : (settings.Language == "zh-CN" ? "此组件未声明可编辑的 WE 属性。" : "This component declares no editable WE properties.");
                AddPropertyDiagnostics(item);
                return;
            }

            PropertyHint.Text = runtimeMode
                ? $"{item.Title} · {runtimeComponent!.InstanceLabel}\n{L("runtimePropertiesHint")}"
                : $"{item.Title} · {(SelectedComponents.SelectedItem as ComponentChoice)?.InstanceLabel} · values for this instance in the selected preset";
            var effectiveValues = definitions.ToDictionary(definition => definition.Key,
                definition => config is not null && config.TryGetValue(definition.Key, out var configured) &&
                    WallpaperPropertyReader.IsValidValue(definition, configured) ? configured : definition.DefaultValue,
                StringComparer.Ordinal);
            int? previousPropertyGroup = null;
            foreach (var definition in definitions)
            {
                var value = effectiveValues[definition.Key];
                if (definition.ConditionSupported && !WallpaperPropertyReader.IsConditionMet(definition, effectiveValues)) continue;
                // BA projects use an order decade per capability group, with its leading property naming the group.
                var propertyGroup = Math.Max(0, definition.Order) / 10;
                var isGroupLead = previousPropertyGroup != propertyGroup;
                if (isGroupLead)
                {
                    InstancePropertyPanel.Children.Add(new TextBlock
                    {
                        Text = PropertyTitle(definition),
                        Foreground = (Brush)FindResource("Muted"), FontWeight = FontWeights.SemiBold,
                        FontSize = 11, TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, previousPropertyGroup is null ? 4 : 14, 0, 6),
                        Tag = "PropertyGroupHeader"
                    });
                    previousPropertyGroup = propertyGroup;
                }
                var context = new PropertyEditContext(runtimeMode ? "runtime" : "component", item.Key, instanceId, definition,
                    runtimeComponent?.Snapshot.SessionId);
                if (!isGroupLead && definition.Type != "bool")
                {
                    var title = new TextBlock
                    {
                        Text = PropertyTitle(definition),
                        Foreground = (Brush)FindResource("Ink"), FontWeight = FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 4)
                    };
                    InstancePropertyPanel.Children.Add(title);
                }
                switch (definition.Type)
                {
                    case "bool":
                        var check = new CheckBox
                        {
                            Content = isGroupLead ? null : PropertyTitle(definition),
                            IsChecked = value.ValueKind == System.Text.Json.JsonValueKind.True,
                            Tag = context, Margin = new Thickness(0, 0, 0, 5)
                        };
                        check.Checked += PropertyCheck_Changed;
                        check.Unchecked += PropertyCheck_Changed;
                        check.IsEnabled = definition.ConditionSupported;
                        InstancePropertyPanel.Children.Add(check);
                        break;
                    case "combo":
                        var combo = new ComboBox { Tag = context, Margin = new Thickness(0, 0, 0, 5), IsEnabled = definition.ConditionSupported };
                        combo.ItemsSource = (definition.Options ?? []).Select(option => new PropertyOptionChoice(
                            option, UiStrings.PropertyText(
                                WallpaperPropertyReader.ResolveLocalizedLabel(option.LocalizedLabels, settings.Language) ?? option.Text,
                                settings.Language))).ToArray();
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

    private void RenderLayoutEditors(PresetComponent component, ComponentPreset preset, string? sessionId = null)
    {
        var layout = component.Layout ?? new ComponentLayout("automatic", Order: 0, ZIndex: 0);
        TransformPanel.Children.Add(new TextBlock
        {
            Text = L("transform"), Foreground = (Brush)FindResource("Muted"),
            FontWeight = FontWeights.SemiBold, FontSize = 11, Margin = new Thickness(0, 4, 0, 6),
            Tag = "TransformGroupHeader"
        });

        var mode = new ComboBox { Tag = new LayoutEditContext(component.InstanceId!, "mode", sessionId), Margin = new Thickness(0, 0, 0, 7) };
        mode.ItemsSource = new[]
        {
            new LayoutChoice("automatic", L("automatic")),
            new LayoutChoice("manual", L("manual"))
        };
        mode.DisplayMemberPath = nameof(LayoutChoice.Label);
        mode.SelectedItem = mode.Items.Cast<LayoutChoice>().First(choice => choice.Value == layout.Mode);
        mode.IsEnabled = preset.LayoutMode != "manual";
        mode.SelectionChanged += LayoutMode_Changed;
        AddLayoutField(L("placement"), mode);

        if (layout.Mode == "manual")
        {
            var anchor = new ComboBox { Tag = new LayoutEditContext(component.InstanceId!, "anchor", sessionId), Margin = new Thickness(0, 0, 0, 7) };
            anchor.ItemsSource = new[]
            {
                new LayoutChoice("top-left", L("anchorTopLeft")), new LayoutChoice("top", L("anchorTop")), new LayoutChoice("top-right", L("anchorTopRight")),
                new LayoutChoice("left", L("anchorLeft")), new LayoutChoice("center", L("anchorCenter")), new LayoutChoice("right", L("anchorRight")),
                new LayoutChoice("bottom-left", L("anchorBottomLeft")), new LayoutChoice("bottom", L("anchorBottom")), new LayoutChoice("bottom-right", L("anchorBottomRight"))
            };
            anchor.DisplayMemberPath = nameof(LayoutChoice.Label);
            anchor.SelectedItem = anchor.Items.Cast<LayoutChoice>().FirstOrDefault(choice => choice.Value == layout.Anchor);
            anchor.SelectionChanged += LayoutAnchor_Changed;
            AddLayoutField(L("anchor"), anchor);
            AddFocusedLayoutSlider(component.InstanceId!, "x", layout.X ?? 0.5, -2, 2, 0.001, 0.5, sessionId: sessionId);
            AddFocusedLayoutSlider(component.InstanceId!, "y", layout.Y ?? 0.5, -2, 2, 0.001, 0.5, sessionId: sessionId);
            AddFocusedLayoutSlider(component.InstanceId!, "width", layout.Width ?? 0.25, 0.01, 2, 0.001, 0.5, sessionId: sessionId);
            AddFocusedLayoutSlider(component.InstanceId!, "height", layout.Height ?? 0.35, 0.01, 2, 0.001, 0.5, sessionId: sessionId);
        }
        else AddFocusedLayoutSlider(component.InstanceId!, "order", layout.Order ?? 0, 0, 10000, 1, 20, integer: true, sessionId: sessionId);
        AddFocusedLayoutSlider(component.InstanceId!, "scale", layout.Scale, 0.1, 5, 0.005, 0.5, sessionId: sessionId);
        AddFocusedLayoutSlider(component.InstanceId!, "rotation", layout.Rotation, -360, 360, 0.1, 45, sessionId: sessionId);
        AddFocusedLayoutSlider(component.InstanceId!, "opacity", layout.Opacity, 0, 1, 0.005, 0.25, sessionId: sessionId);
        AddFocusedLayoutSlider(component.InstanceId!, "zIndex", layout.ZIndex, -100000, 100000, 1, 20, integer: true, sessionId: sessionId);
        if (layout.Mode == "manual" && preset.LayoutMode == "grid")
        {
            var locked = new CheckBox
            {
                Content = L("locked"), IsChecked = layout.Locked, IsEnabled = false,
                Tag = new LayoutEditContext(component.InstanceId!, "locked", sessionId), Margin = new Thickness(0, 2, 0, 9)
            };
            TransformPanel.Children.Add(locked);
        }
        TransformPanel.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Border"), Margin = new Thickness(0, 4, 0, 10) });
    }

    private void AddLayoutField(string label, System.Windows.Controls.Control control)
    {
        TransformPanel.Children.Add(new TextBlock
        {
            Text = label, Foreground = (Brush)FindResource("Ink"), FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 4)
        });
        TransformPanel.Children.Add(control);
    }

    private void AddLayoutNumber(string instanceId, string key, double? value, double minimum, double maximum, bool integer = false)
    {
        var field = new TextBox
        {
            Text = value?.ToString(integer ? "0" : "0.###", CultureInfo.InvariantCulture) ?? "",
            Tag = new LayoutEditContext(instanceId, key), Style = (Style)FindResource("DialogTextBox"),
            ToolTip = $"{minimum.ToString(CultureInfo.InvariantCulture)} – {maximum.ToString(CultureInfo.InvariantCulture)}",
            Margin = new Thickness(0, 0, 0, 7)
        };
        field.LostKeyboardFocus += LayoutNumber_LostKeyboardFocus;
        AddLayoutField(L(key), field);
    }

    private void AddFocusedLayoutSlider(string instanceId, string key, double value, double minimum, double maximum,
        double fineStep, double window, bool integer = false, string? sessionId = null)
    {
        var effectiveMinimum = Math.Max(minimum, value - window);
        var effectiveMaximum = Math.Min(maximum, value + window);
        if (effectiveMaximum - effectiveMinimum < fineStep)
        {
            effectiveMinimum = minimum;
            effectiveMaximum = maximum;
        }
        AddLayoutSlider(instanceId, key, value, effectiveMinimum, effectiveMaximum, fineStep, integer, sessionId);
    }

    private void AddLayoutSlider(string instanceId, string key, double value, double minimum, double maximum, double step, bool integer = false, string? sessionId = null)
    {
        var valueText = new TextBlock
        {
            Text = FormatLayoutValue(value, integer), Foreground = (Brush)FindResource("Muted"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11
        };
        var heading = new Grid { Margin = new Thickness(0, 3, 10, 0) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = L(key), Foreground = (Brush)FindResource("Ink"), FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        });
        var slider = new Slider
        {
            Minimum = minimum, Maximum = maximum, Value = Math.Clamp(value, minimum, maximum),
            SmallChange = step, LargeChange = step * 5,
            TickFrequency = step, IsSnapToTickEnabled = false, IsMoveToPointEnabled = false,
            Height = 28, Margin = new Thickness(0, 0, 0, 5),
            Style = (Style)FindResource("TransformSlider"),
            Tag = new LayoutSliderEditContext(instanceId, key, valueText, integer, step, sessionId)
        };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (glyph, direction, tooltip) in new[] { ("−", -1d, "adjustLess"), ("+", 1d, "adjustMore") })
        {
            var button = new Button
            {
                Content = glyph, Width = 22, Height = 23, Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 2, 0), Style = (Style)FindResource("QuietButton"),
                ToolTip = L(tooltip), Tag = new LayoutNudgeContext(slider, direction)
            };
            button.Click += LayoutNudge_Click;
            controls.Children.Add(button);
        }
        controls.Children.Add(valueText);
        Grid.SetColumn(controls, 1);
        heading.Children.Add(controls);
        TransformPanel.Children.Add(heading);
        slider.ValueChanged += LayoutSlider_ValueChanged;
        slider.LostKeyboardFocus += LayoutSlider_LostKeyboardFocus;
        slider.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(LayoutSlider_MouseLeftButtonUp), true);
        TransformPanel.Children.Add(slider);
    }

    private static string FormatLayoutValue(double value, bool integer) =>
        value.ToString(integer ? "0" : "0.###", CultureInfo.InvariantCulture);

    private void LayoutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is Slider { Tag: LayoutSliderEditContext context })
            context.ValueText.Text = FormatLayoutValue(e.NewValue, context.Integer);
    }

    private void LayoutNudge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LayoutNudgeContext context } || context.Slider.Tag is not LayoutSliderEditContext edit) return;
        var next = Math.Clamp(context.Slider.Value + context.Direction * edit.FineStep, context.Slider.Minimum, context.Slider.Maximum);
        if (Math.Abs(next - context.Slider.Value) < double.Epsilon) return;
        context.Slider.Value = next;
        SaveLayoutSlider(edit, next);
    }

    private void LayoutSlider_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Slider { Tag: LayoutSliderEditContext context } slider)
            SaveLayoutSlider(context, slider.Value);
    }

    private void LayoutSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider { Tag: LayoutSliderEditContext context } slider)
            SaveLayoutSlider(context, slider.Value);
    }

    private void SaveLayoutSlider(LayoutSliderEditContext context, double value)
    {
        if (renderingPropertyEditors || !TryGetLayout(context.InstanceId, context.SessionId, out var layout)) return;
        value = context.Integer ? Math.Round(value) : Math.Round(value, 3);
        var currentValue = context.Field switch
        {
            "x" => layout.X,
            "y" => layout.Y,
            "width" => layout.Width,
            "height" => layout.Height,
            "scale" => layout.Scale,
            "rotation" => layout.Rotation,
            "opacity" => layout.Opacity,
            "order" => layout.Order,
            "zIndex" => layout.ZIndex,
            _ => null
        };
        if (currentValue is { } current && Math.Abs(current - value) < 0.0005) return;
        var next = context.Field switch
        {
            "x" => layout with { X = value },
            "y" => layout with { Y = value },
            "width" => layout with { Width = value },
            "height" => layout with { Height = value },
            "scale" => layout with { Scale = value },
            "rotation" => layout with { Rotation = value },
            "opacity" => layout with { Opacity = value },
            "order" => layout with { Order = (int)Math.Round(value) },
            "zIndex" => layout with { ZIndex = (int)Math.Round(value) },
            _ => null
        };
        if (next is not null) _ = RunActionAsync(() => SaveLayoutAsync(context.InstanceId, context.SessionId, next));
    }

    private void LayoutMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (renderingPropertyEditors || sender is not ComboBox { Tag: LayoutEditContext context, SelectedItem: LayoutChoice choice } ||
            !TryGetLayout(context.InstanceId, context.SessionId, out var current)) return;
        var preset = SelectedPreset;
        var order = context.SessionId is not null
            ? runtimeSnapshots.FirstOrDefault(snapshot => snapshot.SessionId == context.SessionId)?.Instances
                .ToList().FindIndex(item => item.InstanceId == context.InstanceId) ?? 0
            : Math.Max(0, SelectedComponents.SelectedIndex);
        var next = choice.Value == "manual"
            ? current with { Mode = "manual", Anchor = "center", X = 0.5, Y = 0.5, Width = 0.25, Height = 0.38, Locked = preset?.LayoutMode == "grid" }
            : current with { Mode = "automatic", Anchor = null, X = null, Y = null, Width = null, Height = null, Locked = false, Order = current.Order ?? order };
        _ = RunActionAsync(() => SaveLayoutAsync(context.InstanceId, context.SessionId, next));
    }

    private void LayoutAnchor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!renderingPropertyEditors && sender is ComboBox { Tag: LayoutEditContext context, SelectedItem: LayoutChoice choice } &&
            TryGetLayout(context.InstanceId, context.SessionId, out var layout) && layout.Mode == "manual")
            _ = RunActionAsync(() => SaveLayoutAsync(context.InstanceId, context.SessionId, layout with { Anchor = choice.Value }));
    }

    private void LayoutNumber_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (renderingPropertyEditors || sender is not TextBox { Tag: LayoutEditContext context } field ||
            !TryGetLayout(context.InstanceId, context.SessionId, out var layout) ||
            !double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            RenderPropertyEditors();
            return;
        }
        var next = context.Field switch
        {
            "x" => layout with { X = value },
            "y" => layout with { Y = value },
            "width" => layout with { Width = value },
            "height" => layout with { Height = value },
            "scale" => layout with { Scale = value },
            "rotation" => layout with { Rotation = value },
            "opacity" => layout with { Opacity = value },
            "zIndex" when value == Math.Truncate(value) && value is >= int.MinValue and <= int.MaxValue => layout with { ZIndex = (int)value },
            "order" when value == Math.Truncate(value) && value is >= int.MinValue and <= int.MaxValue => layout with { Order = (int)value },
            _ => null
        };
        if (next is null)
        {
            RenderPropertyEditors();
            return;
        }
        _ = RunActionAsync(() => SaveLayoutAsync(context.InstanceId, context.SessionId, next));
    }

    private bool TryGetLayout(string instanceId, string? sessionId, out ComponentLayout layout)
    {
        if (sessionId is not null)
        {
            var instance = runtimeSnapshots.FirstOrDefault(snapshot => snapshot.SessionId == sessionId)?
                .Instances.FirstOrDefault(item => item.InstanceId == instanceId);
            if (instance is null) { layout = null!; return false; }
            var index = runtimeSnapshots.First(snapshot => snapshot.SessionId == sessionId).Instances
                .ToList().FindIndex(item => item.InstanceId == instanceId);
            layout = instance.Layout ?? new ComponentLayout("automatic", Order: Math.Max(0, index), ZIndex: Math.Max(0, index));
            return true;
        }
        if (SelectedPreset is { } preset && preset.Components.FirstOrDefault(item => item.InstanceId == instanceId) is { } component)
        {
            layout = GetLayout(component, preset);
            return true;
        }
        layout = null!;
        return false;
    }

    private async Task SaveLayoutAsync(string instanceId, string? sessionId, ComponentLayout layout)
    {
        if (sessionId is null)
        {
            await SaveComponentLayoutAsync(instanceId, layout);
            return;
        }
        if (authorizationClient is null) return;
        await authorizationClient.SetRuntimeLayoutAsync(sessionId, instanceId, layout);
        runtimeLayoutCommands[RuntimeCommandKey(sessionId, instanceId)] = layout;
        runtimeSnapshots = runtimeSnapshots.Select(snapshot => snapshot.SessionId != sessionId ? snapshot :
            snapshot with { Instances = snapshot.Instances.Select(instance => instance.InstanceId != instanceId ? instance :
                instance with { Layout = layout }).ToArray() }).ToArray();
        RenderDesktop();
    }

    private async Task SaveComponentLayoutAsync(string instanceId, ComponentLayout layout)
    {
        var preset = SelectedPreset;
        if (preset is null) return;
        var components = preset.Components.Select(component => component.InstanceId == instanceId ? component with { Layout = layout } : component).ToArray();
        var nextPreset = preset with { Components = components };
        var nextState = state with { Presets = state.Presets.Select(item => item.Id == preset.Id ? nextPreset : item).ToArray() };
        var errors = PresetSelection.Validate(nextState);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("; ", errors));
        await SaveAsync(nextState, preset.Id);
    }

    private static ComponentLayout GetLayout(PresetComponent component, ComponentPreset preset)
    {
        var index = preset.Components.ToList().FindIndex(item => item.InstanceId == component.InstanceId);
        return component.Layout ?? new ComponentLayout("automatic", Order: Math.Max(0, index), ZIndex: Math.Max(0, index));
    }

    private string PropertyTitle(WallpaperPropertyDefinition definition)
    {
        var localized = WallpaperPropertyReader.ResolveLocalizedLabel(definition.LocalizedLabels, settings.Language);
        if (!string.IsNullOrWhiteSpace(localized)) return UiStrings.PropertyText(localized, settings.Language);
        if (definition.Text == "ui_browse_properties_scheme_color")
            return settings.Language == "zh-CN" ? "配色" : "Color scheme";
        var text = definition.Text is { Length: > 0 } label && !label.StartsWith("ui_", StringComparison.Ordinal)
            ? label
            : System.Text.RegularExpressions.Regex.Replace(definition.Key, "([a-z0-9])([A-Z])", "$1 $2").Replace('_', ' ').Replace('-', ' ');
        return UiStrings.PropertyText(text, settings.Language);
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
        var config = context.Scope == "host"
            ? state.HostConfigs?.GetValueOrDefault(context.OwnerKey)
            : context.Scope == "runtime"
                ? selectedRuntimeComponent?.Instance.Config
                : SelectedComponent?.Config;
        if (config?.TryGetValue(context.Definition.Key, out var configured) == true && configured.ValueKind == System.Text.Json.JsonValueKind.String)
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
        if (context.Scope == "runtime")
        {
            if (authorizationClient is null || context.SessionId is null || context.InstanceId is null ||
                !WallpaperPropertyReader.IsValidValue(context.Definition, value)) return;
            var snapshot = runtimeSnapshots.FirstOrDefault(candidate => candidate.SessionId == context.SessionId);
            var instance = snapshot?.Instances.FirstOrDefault(candidate => candidate.InstanceId == context.InstanceId);
            if (snapshot is null || instance is null) return;
            var config = new Dictionary<string, System.Text.Json.JsonElement>(instance.Config, StringComparer.Ordinal)
            {
                [context.Definition.Key] = value.Clone()
            };
            var revisionKey = RuntimeCommandKey(context.SessionId, context.InstanceId);
            var revision = Math.Max(instance.ConfigRevision, runtimeCommandRevisions.GetValueOrDefault(revisionKey)) + 1;
            await authorizationClient.SetRuntimeConfigAsync(context.SessionId, context.InstanceId, config, revision);
            runtimeCommandRevisions[revisionKey] = revision;
            runtimeSnapshots = runtimeSnapshots.Select(candidate => candidate.SessionId != snapshot.SessionId ? candidate :
                candidate with { Instances = candidate.Instances.Select(current => current.InstanceId != instance.InstanceId ? current :
                    current with { Config = config, ConfigRevision = revision }).ToArray() }).ToArray();
            RenderDesktop();
            return;
        }
        if (context.Scope == "host")
        {
            if (authorizationClient is null || activeHostKey != context.OwnerKey ||
                !WallpaperPropertyReader.IsValidValue(context.Definition, value)) return;
            var receipt = await authorizationClient.SetHostPropertyAsync(context.OwnerKey, EffectiveDisplayIndex,
                context.Definition.Key, value);
            state = receipt.Presets;
            return;
        }
        var preset = SelectedPreset;
        if (preset is null || context.InstanceId is null || !WallpaperPropertyReader.IsValidValue(context.Definition, value)) return;
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
        var limitErrors = PresetSelection.ValidateInstanceLimitIncreases(state, next, items);
        if (limitErrors.Count > 0) throw new InvalidDataException(string.Join("; ", limitErrors));
        await store.WriteAsync(next);
        state = await store.ReadAsync();
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

    private void PresetUpButton_Click(object sender, RoutedEventArgs e) => _ = MovePresetAsync(-1);
    private void PresetDownButton_Click(object sender, RoutedEventArgs e) => _ = MovePresetAsync(1);
    private void TogglePresetButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(async () =>
    {
        if (SelectedPreset is { } preset) await SaveAsync(PresetSelection.SetPresetEnabled(state, preset.Id, !preset.Enabled), preset.Id);
    });

    private Task MovePresetAsync(int offset) => RunActionAsync(async () =>
    {
        var preset = SelectedPreset;
        var index = preset is null ? -1 : state.Presets.ToList().FindIndex(item => item.Id == preset.Id);
        if (index < 0 || index + offset < 0 || index + offset >= state.Presets.Count) return;
        await SaveAsync(PresetSelection.MovePreset(state, preset!.Id, index + offset), preset.Id);
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
        await SaveAsync(PresetSelection.DeletePreset(state, preset.Id));
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
        var changed = preset with { Components = [.. preset.Components, new(item.BapId, item.Key,
            Guid.NewGuid().ToString("N"), Layout: new ComponentLayout("automatic", Order: order, ZIndex: order))] };
        var limitErrors = PresetSelection.ValidateInstanceLimits(changed, items);
        if (limitErrors.Count > 0)
        {
            var limit = items.Where(candidate => candidate.Role == "component" && candidate.BapId == item.BapId && candidate.MaxInstances is not null)
                .Select(candidate => candidate.MaxInstances!.Value).DefaultIfEmpty(int.MaxValue).Min();
            var message = settings.Language == "zh-CN"
                ? $"此组件在每个预设中最多只能有 {limit} 个实例。"
                : $"This component allows at most {limit} instance(s) in each preset.";
            ThemedDialog.ShowMessage(this, L("chooseComponent"), message, settings.Language == "zh-CN" ? "确定" : "OK");
            return;
        }
        await ReplacePresetAsync(changed);
    });

    private void AddHostButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(async () =>
    {
        var preset = SelectedPreset;
        if (preset is null || state.GlobalPresetId == preset.Id) return;
        var choices = GetAvailableHostCandidates(preset)
            .Select(item =>
            {
                var assignedName = state.HostPresetIds.TryGetValue(item.Key, out var assignedIds)
                    ? string.Join(", ", assignedIds.Select(id => state.Presets.FirstOrDefault(candidate => candidate.Id == id)?.Name ?? id))
                    : L("notAssigned");
                return new CatalogPickerItem(item, item.Title, L(item.SourceKind),
                    $"{L(item.Status)} · {L("currentlyAssigned", assignedName)}" + (IsUnverifiedHost(item) ? $" · {L("notBapVerifiedShort")}" : ""),
                    LoadPreview(item.PreviewPath), item.PreviewPath is null ? L("noPreview") : "");
            }).ToArray();
        var selected = CatalogSelectionDialog.Show(this, L("chooseHost"), choices, settings.Language);
        if (selected?.Value is not InstalledItem item) return;
        if (IsUnverifiedHost(item) && !ThemedDialog.Confirm(this, L("chooseHost"), L("unverifiedHostWarning", item.Title),
                L("continue"), L("cancel"))) return;
        await SaveAsync(PresetSelection.AddHostToPreset(state, preset.Id, item.Key), preset.Id);
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

    private void RemovePresetButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(async () =>
    {
        if (PresetHostsList.SelectedItem is not PresetHostChoice choice) return;
        if (choice.IsGlobal)
        {
            await SaveAsync(state with { GlobalPresetId = null });
            return;
        }
        var hostKey = choice.HostKey;
        if (hostKey is null) return;
        if (SelectedPreset is { } preset) await SaveAsync(PresetSelection.RemoveHostFromPreset(state, preset.Id, hostKey), preset.Id);
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
        if (SelectedPreset is { } preset)
        {
            await SaveAsync(PresetSelection.SetGlobalPreset(state, preset.Id), preset.Id);
        }
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
    private sealed record PresetChoice(ComponentPreset Preset, string DisabledLabel)
    { public override string ToString() => Preset.Enabled ? Preset.Name : $"{Preset.Name} ({DisabledLabel})"; }
    private sealed record ComponentChoice(PresetComponent Component, string Title, string Source, string Summary, string InstanceLabel, ImageSource? Preview, string NoPreview);
    private static bool IsUnverifiedHost(InstalledItem item) => item.BapId is null || item.Status != "valid";
    private sealed record PresetHostChoice(string? HostKey, string Title, string Source, string Summary, ImageSource? Preview, string NoPreview, bool IsGlobal, bool IsCurrent = false);
    private sealed record RuntimeComponentChoice(RuntimeHostSnapshot Snapshot, RuntimeComponentInstance Instance, InstalledItem? Item,
        string Title, string Source, string InstanceLabel, string Summary, ImageSource? Preview, string NoPreview);
    private sealed record PropertyEditContext(string Scope, string OwnerKey, string? InstanceId,
        WallpaperPropertyDefinition Definition, string? SessionId = null);
    private sealed record LayoutEditContext(string InstanceId, string Field, string? SessionId = null);
    private sealed record LayoutSliderEditContext(string InstanceId, string Field, TextBlock ValueText, bool Integer, double FineStep, string? SessionId = null);
    private sealed record LayoutNudgeContext(Slider Slider, double Direction);
    private sealed record LayoutChoice(string Value, string Label);
    private sealed record SliderEditContext(PropertyEditContext Edit, TextBlock ValueLabel);
    private sealed record PropertyOptionChoice(WallpaperPropertyOption Option, string Label);
    private sealed record TrustedHostChoice(TrustedHost Host, string Label) { public override string ToString() => Label; }
}
