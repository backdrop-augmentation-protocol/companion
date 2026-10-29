using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bap.Companion.Core;
using Bap.Companion.Desktop;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace Bap.Companion.Desktop.Wpf;

public partial class MainWindow : Window
{
    private readonly InstalledCatalog catalog = InstalledCatalog.FromEnvironment();
    private readonly JsonPresetStore store = new(JsonPresetStore.DefaultPath);
    private readonly CompanionLog log;
    private readonly int port;
    private readonly DesktopSettings settings;
    private PresetState state = new([], null, new Dictionary<string, string>());
    private IReadOnlyList<InstalledItem> items = [];
    private InstalledItem? selectedItem;
    private bool uiReady;
    private bool changingCatalogSelection;
    private bool changingDisplayChoice;
    private readonly HostAuthorizationClient? authorizationClient;
    private readonly DispatcherTimer authorizationTimer;
    private bool showingAuthorizationPrompt;

    internal MainWindow(CompanionLog log, int port, DesktopSettings settings, HostAuthorizationClient? authorizationClient = null)
    {
        this.log = log;
        this.port = port;
        this.settings = settings;
        this.authorizationClient = authorizationClient;
        InitializeComponent();
        LanguageChoice.SelectedIndex = settings.Language == "zh-CN" ? 1 : 0;
        StartupCheck.IsChecked = settings.AutoStartEnabled;
        ServiceAddress.Text = $"Service: http://127.0.0.1:{port}";
        if (settings.StartupError is not null) StartupError.Text = L("startupError", settings.StartupError);
        settings.LanguageChanged += OnLanguageChanged;
        ApplyLanguage();
        SelectPage(InstalledPage, InstalledNav);
        uiReady = true;
        StateChanged += (_, _) => UpdateMaximizeButton();
        Loaded += async (_, _) => await RunActionAsync(RefreshAsync);
        authorizationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        authorizationTimer.Tick += AuthorizationTimer_Tick;
        if (authorizationClient is not null) authorizationTimer.Start();
        Closed += (_, _) => authorizationTimer.Stop();
    }

    private async void AuthorizationTimer_Tick(object? sender, EventArgs e)
    {
        if (authorizationClient is null || showingAuthorizationPrompt) return;
        try
        {
            var request = (await authorizationClient.GetPendingAsync()).FirstOrDefault();
            if (request is null) return;
            showingAuthorizationPrompt = true;
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
        SelectedComponentsHeading.Text = L("selectedComponents");
        AddComponentButton.Content = L("addComponent"); RemoveComponentButton.Content = L("remove");
        MoveUpButton.Content = L("moveUp"); MoveDownButton.Content = L("moveDown");
        ScopeHeading.Text = L("scope"); SetGlobalButton.Content = L("setGlobal");
        HostOverrideLabel.Text = L("hostOverride"); SetHostButton.Content = L("setHost"); ClearHostButton.Content = L("clearHost");
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
        RenderChoices();
        RenderPresets();
        RenderDetails();
        _ = RunActionAsync(RefreshTrustedHostsAsync);
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
            System.Windows.MessageBox.Show(this, error.Message, "BAP Companion", MessageBoxButton.OK, MessageBoxImage.Error);
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
        RenderChoices();
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
        DetailMetadata.Text = $"{L("source")}: {L(item.SourceKind)}\n{L("status")}: {L(item.Status)}\n{L("bapId")}: {item.BapId ?? "—"}\n{L("version")}: {item.Version ?? "—"}" +
            (item.WorkshopId is null ? "" : $"\n{L("workshopId")}: {item.WorkshopId}");
        DetailPath.Text = item.ProjectDirectory;
        HostActions.Visibility = item.Role == "host" && item.Status != "invalid" ? Visibility.Visible : Visibility.Collapsed;
        SwitchStatus.Text = "";
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

    private void RenderChoices()
    {
        if (AvailableComponents is null) return;
        var componentKey = (AvailableComponents.SelectedItem as CatalogChoice)?.Item.Key;
        var hostKey = (HostChoice.SelectedItem as CatalogChoice)?.Item.Key;
        AvailableComponents.ItemsSource = items.Where(item => item.Role == "component" && item.Status == "valid")
            .Select(item => new CatalogChoice(item, $"{item.Title} [{L(item.SourceKind)}]")).ToArray();
        HostChoice.ItemsSource = items.Where(item => item.Role == "host" && item.BapId is not null && item.Status != "invalid")
            .Select(item => new CatalogChoice(item, $"{item.Title} [{L(item.SourceKind)}]")).ToArray();
        AvailableComponents.SelectedItem = AvailableComponents.Items.Cast<CatalogChoice>().FirstOrDefault(choice => choice.Item.Key == componentKey)
            ?? AvailableComponents.Items.Cast<CatalogChoice>().FirstOrDefault();
        HostChoice.SelectedItem = HostChoice.Items.Cast<CatalogChoice>().FirstOrDefault(choice => choice.Item.Key == hostKey)
            ?? HostChoice.Items.Cast<CatalogChoice>().FirstOrDefault();
    }

    private ComponentPreset? SelectedPreset => (PresetList.SelectedItem as PresetChoice)?.Preset;

    private void RenderPresets(string? selectId = null)
    {
        if (PresetList is null) return;
        selectId ??= SelectedPreset?.Id;
        PresetList.ItemsSource = state.Presets.Select(preset => new PresetChoice(preset)).ToArray();
        PresetList.SelectedItem = PresetList.Items.Cast<PresetChoice>().FirstOrDefault(choice => choice.Preset.Id == selectId);
        RenderSelectedComponents();
        var global = state.Presets.FirstOrDefault(preset => preset.Id == state.GlobalPresetId)?.Name ?? L("allInstalled");
        PresetStatus.Text = $"{L("global")}: {global}\n{L("overrides")}: {state.HostPresetIds.Count}";
    }

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderSelectedComponents();

    private void RenderSelectedComponents()
    {
        if (SelectedComponents is null) return;
        SelectedComponents.ItemsSource = (SelectedPreset?.Components ?? [])
            .Select(component =>
            {
                var item = items.FirstOrDefault(candidate => candidate.BapId == component.ComponentId &&
                    (component.SourceKey is null || candidate.Key == component.SourceKey));
                return new ComponentChoice(component, $"{item?.Title ?? component.ComponentId} [{L(item?.SourceKind ?? "missing")}]");
            }).ToArray();
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
        if (preset is null || System.Windows.MessageBox.Show(this, L("deletePreset", preset.Name), "BAP Companion", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
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
        var item = (AvailableComponents.SelectedItem as CatalogChoice)?.Item;
        if (preset is null || item?.BapId is null) return;
        if (preset.Components.Any(component => component.ComponentId == item.BapId))
        {
            System.Windows.MessageBox.Show(this, L("duplicateComponent"));
            return;
        }
        await ReplacePresetAsync(preset with { Components = [.. preset.Components, new(item.BapId, item.Key)] });
    });

    private void RemoveComponentButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(async () =>
    {
        var preset = SelectedPreset;
        var component = (SelectedComponents.SelectedItem as ComponentChoice)?.Component;
        if (preset is null || component is null) return;
        await ReplacePresetAsync(preset with { Components = preset.Components.Where(item => item.ComponentId != component.ComponentId).ToArray() });
    });

    private void MoveUpButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(() => MoveComponentAsync(-1));
    private void MoveDownButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(() => MoveComponentAsync(1));

    private async Task MoveComponentAsync(int offset)
    {
        var preset = SelectedPreset;
        var index = SelectedComponents.SelectedIndex;
        if (preset is null || index < 0 || index + offset < 0 || index + offset >= preset.Components.Count) return;
        var ordered = preset.Components.ToList();
        (ordered[index], ordered[index + offset]) = (ordered[index + offset], ordered[index]);
        await ReplacePresetAsync(preset with { Components = ordered });
        SelectedComponents.SelectedIndex = index + offset;
    }

    private void SetGlobalButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(async () =>
    {
        if (SelectedPreset is { } preset) await SaveAsync(state with { GlobalPresetId = preset.Id }, preset.Id);
    });

    private void SetHostButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(async () =>
    {
        if (SelectedPreset is not { } preset) return;
        if ((HostChoice.SelectedItem as CatalogChoice)?.Item.BapId is not { } hostId)
        {
            System.Windows.MessageBox.Show(this, L("selectHostFirst"));
            return;
        }
        var bindings = new Dictionary<string, string>(state.HostPresetIds) { [hostId] = preset.Id };
        await SaveAsync(state with { HostPresetIds = bindings }, preset.Id);
    });

    private void ClearHostButton_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(async () =>
    {
        if ((HostChoice.SelectedItem as CatalogChoice)?.Item.BapId is not { } hostId) return;
        var bindings = new Dictionary<string, string>(state.HostPresetIds);
        bindings.Remove(hostId);
        await SaveAsync(state with { HostPresetIds = bindings });
    });

    private string? PromptForName(string title, string value = "")
    {
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            Width = 390,
            Height = 168,
            MinWidth = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.White,
            FontFamily = FontFamily,
            FontSize = FontSize
        };
        var input = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 16) };
        var ok = new Button { Content = "OK", Width = 84, Height = 34, IsDefault = true };
        ok.Click += (_, _) => dialog.DialogResult = true;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(input);
        panel.Children.Add(ok);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(input.Text) ? input.Text.Trim() : null;
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
            System.Windows.MessageBox.Show(this, error.Message, L("settings"), MessageBoxButton.OK, MessageBoxImage.Error);
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
            System.Windows.MessageBox.Show(this, error.Message, L("settings"), MessageBoxButton.OK, MessageBoxImage.Error);
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
    private sealed record CatalogChoice(InstalledItem Item, string Label);
    private sealed record PresetChoice(ComponentPreset Preset) { public override string ToString() => Preset.Name; }
    private sealed record ComponentChoice(PresetComponent Component, string Label);
    private sealed record TrustedHostChoice(TrustedHost Host, string Label) { public override string ToString() => Label; }
}
