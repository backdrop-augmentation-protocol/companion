using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bap.Companion.Desktop;
using Bap.Companion.Desktop.Wpf;
using Bap.Companion.Core;
using Xunit;

namespace Companion.Desktop.Wpf.Tests;

public sealed class WindowTests
{
    [Fact]
    public void WeColorValuesRoundTripThroughEightBitPickerColor()
    {
        var original = System.Windows.Media.Color.FromRgb(42, 71, 117);
        var serialized = ColorPickerDialog.FormatWeColor(original);
        Assert.Equal(original, ColorPickerDialog.ParseWeColor(serialized));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(127, 128)]
    [InlineData(254, 255)]
    [InlineData(255, 255)]
    public void ScreenPickerCorrectsNearlyTransparentHitTestOverlay(byte sampled, byte expected) =>
        Assert.Equal(expected, ColorPickerDialog.ScreenSampler.CorrectChannel(sampled));

    [Fact]
    public void FluentWindowConstructsAndSwitchesLanguageWithoutStartingServer()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bap-wpf-{Guid.NewGuid():N}");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
#pragma warning disable WPF0001 // Match the production Fluent dark-mode configuration.
                var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.Dark };
#pragma warning restore WPF0001
                using var iconStream = System.Windows.Application.GetResourceStream(
                    new Uri("pack://application:,,,/Bap.Companion.Desktop;component/Assets/bap-companion.ico"))?.Stream;
                Assert.NotNull(iconStream);
                using var trayIcon = new System.Drawing.Icon(iconStream);
                Assert.True(trayIcon.Width > 0);
                var settings = new DesktopSettings(Path.Combine(directory, "desktop.json"), synchronizeStartup: false);
                using var log = new CompanionLog(Path.Combine(directory, "companion.log"));
                var presetStore = new JsonPresetStore(Path.Combine(directory, "presets.json"));
                using var creatorHttpClient = new HttpClient(new StaticHttpMessageHandler(request =>
                    request.RequestUri!.Host == "api.steampowered.com"
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"response\":{\"publishedfiledetails\":[{\"creator\":\"76561198000000000\"}]}}", Encoding.UTF8, "application/json") }
                        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<profile><steamID>Creator Name</steamID></profile>", Encoding.UTF8, "application/xml") }));
                var window = new MainWindow(log, 4312, settings, creatorLookup: new SteamWorkshopCreatorLookup(creatorHttpClient), presetStore: presetStore);
                window.Show();
                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
                Assert.NotNull(chrome);
                Assert.Equal(42, chrome.CaptionHeight);
                Assert.False(chrome.UseAeroCaptionButtons);
                Assert.NotNull(window.Icon);
                Assert.True(System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(window.CloseButton));
                Assert.Equal(22, window.CloseButton.FontSize);
                foreach (var control in new System.Windows.Controls.Control[]
                {
                    window.CloseButton, window.MinimizeButton, window.MaximizeButton,
                    window.InstalledNav, window.PresetsNav, window.SettingsNav,
                    window.HostsList, window.ComponentsList, window.PresetList,
                    window.SelectedComponents, window.LanguageChoice, window.MonitorChoice,
                    window.StartupCheck, window.TrustedHostsList
                })
                    Assert.True(ReferenceEquals(window.FindResource("NoFocusVisual"), control.FocusVisualStyle), $"Unexpected focus adorner on {control.Name}.");
                Assert.False(window.CatalogScrollViewer.Focusable);
                Assert.False(window.SettingsPage.Focusable);
                var dialog = ThemedDialog.CreateWindow(window, "Preview", new System.Windows.Controls.TextBlock { Text = "Dialog body" });
                var dialogChrome = System.Windows.Shell.WindowChrome.GetWindowChrome(dialog);
                Assert.Equal(WindowStyle.None, dialog.WindowStyle);
                Assert.NotNull(dialogChrome);
                Assert.Equal(44, dialogChrome.CaptionHeight);
                Assert.Same(window.FindResource("PrimaryButton"), ThemedDialog.CreateButton(window, "OK", primary: true).Style);
                dialog.Close();
                window.MaximizeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(WindowState.Maximized, window.WindowState);
                window.MaximizeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.Equal("Installed", window.InstalledNav.Content);
                Assert.Equal("Wallpaper Manager", window.AppSubtitle.Text);
                window.Measure(new System.Windows.Size(1220, 790));
                window.Arrange(new Rect(0, 0, 1220, 790));
                window.UpdateLayout();
                Assert.True(window.LanguageChoice.ApplyTemplate());
                window.LanguageChoice.SelectedIndex = 1;
                Assert.Equal("zh-CN", settings.Language);
                Assert.Equal("已安装", window.InstalledNav.Content);
                Assert.Equal("壁纸管理器", window.AppSubtitle.Text);
                window.LanguageChoice.SelectedIndex = 0;
                Assert.Equal("en", settings.Language);
                Assert.Equal("Settings", window.SettingsNav.Content);
                window.SettingsNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.UpdateLayout();
                var frame = new System.Windows.Threading.DispatcherFrame();
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                Assert.Equal(Visibility.Visible, window.SettingsPage.Visibility);
                foreach (var choice in new[] { window.LanguageChoice, window.MonitorChoice })
                {
                    Assert.True(choice.IsVisible);
                    Assert.True(choice.IsEnabled);
                    Assert.True(choice.IsLoaded);
                    choice.ApplyTemplate();
                    Assert.Null(choice.Template.FindName("DropDownButton", choice));
                    var toggle = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(choice.Template.FindName("ToggleButton", choice));
                    var popup = Assert.IsType<System.Windows.Controls.Primitives.Popup>(choice.Template.FindName("PART_Popup", choice));
                    Assert.False(choice.IsDropDownOpen);
                    var peer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(toggle);
                    var togglePattern = Assert.IsAssignableFrom<System.Windows.Automation.Provider.IToggleProvider>(peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle));
                    togglePattern.Toggle();
                    Assert.True(choice.IsDropDownOpen);
                    Assert.True(popup.IsOpen);
                    togglePattern.Toggle();
                    Assert.False(choice.IsDropDownOpen);
                    Assert.False(popup.IsOpen);
                }
                Assert.Equal(0, window.EffectiveDisplayIndex);
                settings.SetDisplayIndex(99);
                Assert.Equal(Math.Max(0, System.Windows.Forms.Screen.AllScreens.Length - 1), window.EffectiveDisplayIndex);
                settings.SetDisplayIndex(0);
                using var propertyJson = System.Text.Json.JsonDocument.Parse("""
                    { "general": { "properties": {
                      "mode": { "type": "combo", "text": "Display", "value": "one", "order": 0, "options": [{ "value": "one", "label": "One" }, { "value": "two", "label": "Two" }] },
                      "enabled": { "type": "bool", "text": "Enabled", "value": true, "order": 1 },
                      "label": { "type": "textinput", "text": "Label", "value": "Default", "order": 2, "condition": "mode.value == 'two'" },
                      "appearance": { "type": "combo", "text": "Appearance", "value": "custom", "order": 10, "options": [{ "value": "standard", "text": "Standard" }, { "value": "custom", "text": "Custom" }] },
                      "schemecolor": { "type": "color", "text": "ui_browse_properties_scheme_color", "value": "0.1 0.2 0.3", "order": 11, "condition": "appearance.value == 'custom'" },
                      "opacity": { "type": "slider", "text": "Opacity", "value": 0.5, "min": 0, "max": 1, "order": 12, "condition": "appearance.value == 'custom'" }
                    } } }
                    """);
                var componentProperties = WallpaperPropertyReader.Read(propertyJson.RootElement).Properties;
                var firstInstance = new PresetComponent("org.example.time", "workshop:123", "instance-a",
                    Layout: new ComponentLayout("manual", "center", 0.5, 0.5, 0.25, 0.15, Scale: 1, Rotation: 0, Opacity: 1, ZIndex: 0, Locked: true));
                var secondInstance = new PresetComponent("org.example.time", "workshop:123", "instance-b");
                InstalledItem[] presetTestItems =
                [
                    new InstalledItem("local:host", "local", "host", "Demo host wallpaper", "host", null, "org.example.host", "1.0.0", null, null),
                    new InstalledItem("workshop:host-2", "workshop", "host", "Second demo host wallpaper", "host", "222", "org.example.host-two", "1.0.0", null, null),
                    new InstalledItem("local:non-bap-host", "local", "host", "Regular Web wallpaper", "host", null, null, null, null, null, "unverified"),
                    new InstalledItem("workshop:123", "workshop", "component", "Time widget", "component", "123", "org.example.time", "1.0.0", null, null, Properties: componentProperties),
                    new InstalledItem("local:calendar", "local", "component", "Calendar widget", "component", null, "org.example.calendar", "1.0.0", null, null)
                ];
                var initialPresetState = new PresetState(
                    [new ComponentPreset("preset-1", "Daily setup", [firstInstance, secondInstance])],
                    null, new Dictionary<string, string> { ["local:host"] = "preset-1" });
                window.RenderSnapshot(presetTestItems, initialPresetState);
                window.PresetsNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.PresetList.SelectedIndex = 0;
                window.SelectedComponents.SelectedIndex = 0;
                window.UpdateLayout();
                Assert.Equal("Component properties", window.PropertyHeading.Text);
                Assert.Contains(window.TransformPanel.Children.OfType<System.Windows.Controls.TextBlock>(), header => Equals(header.Tag, "TransformGroupHeader"));
                Assert.Equal(2, window.TransformPanel.Children.OfType<System.Windows.Controls.ComboBox>().Count());
                Assert.Single(window.TransformPanel.Children.OfType<System.Windows.Controls.TextBox>());
                Assert.Equal(7, window.TransformPanel.Children.OfType<System.Windows.Controls.Slider>().Count());
                Assert.True(Assert.Single(window.TransformPanel.Children.OfType<System.Windows.Controls.CheckBox>()).IsChecked);
                window.SelectedComponents.SelectedIndex = 1;
                window.UpdateLayout();
                Assert.Contains(window.TransformPanel.Children.OfType<System.Windows.Controls.TextBlock>(), label => label.Text == "Order");
                Assert.Equal(2, window.TransformPanel.Children.OfType<System.Windows.Controls.TextBox>().Count());
                Assert.Equal(3, window.TransformPanel.Children.OfType<System.Windows.Controls.Slider>().Count());
                window.SelectedComponents.SelectedIndex = 0;
                Assert.Equal(Visibility.Collapsed, window.HostsEmpty.Visibility);
                window.RenderSnapshot(presetTestItems, initialPresetState with { HostPresetIds = new Dictionary<string, string>() });
                Assert.Equal(Visibility.Collapsed, window.HostsEmpty.Visibility);
                Assert.Equal(Visibility.Collapsed, window.ComponentsEmpty.Visibility);
                window.RenderSnapshot(presetTestItems, initialPresetState);
                window.HostsModeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, window.SelectedComponents.Visibility);
                Assert.Equal(Visibility.Visible, window.PresetHostsList.Visibility);
                Assert.Single(window.PresetHostsList.Items);
                window.RenderSnapshot(presetTestItems, initialPresetState with
                {
                    HostPresetIds = new Dictionary<string, string>
                    {
                        ["local:host"] = "preset-1",
                        ["workshop:host-2"] = "preset-1",
                        ["local:non-bap-host"] = "preset-1"
                    }
                });
                Assert.False(window.AddHostButton.IsEnabled);
                window.RenderSnapshot(presetTestItems, initialPresetState);
                var hostsModeGrid = Assert.IsType<System.Windows.Controls.Grid>(System.Windows.Media.VisualTreeHelper.GetParent(window.HostsModeButton));
                var presetContentPanel = Assert.IsType<System.Windows.Controls.StackPanel>(System.Windows.Media.VisualTreeHelper.GetParent(hostsModeGrid));
                var presetContentDock = Assert.IsType<System.Windows.Controls.DockPanel>(System.Windows.Media.VisualTreeHelper.GetParent(presetContentPanel));
                var presetContentBorder = Assert.IsType<System.Windows.Controls.Border>(System.Windows.Media.VisualTreeHelper.GetParent(presetContentDock));
                Assert.Equal(1, System.Windows.Controls.Grid.GetColumn(presetContentBorder));
                window.PresetHostsList.SelectedIndex = 0;
                Assert.True(window.RemovePresetButton.IsEnabled);
                Assert.True(window.SetGlobalButton.IsEnabled);
                Assert.True(window.AddHostButton.IsEnabled);
                window.SetGlobalButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var globalFrame = new System.Windows.Threading.DispatcherFrame();
                var globalTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                globalTimer.Tick += (_, _) => { globalTimer.Stop(); globalFrame.Continue = false; };
                globalTimer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(globalFrame);
                Assert.False(window.SetGlobalButton.IsEnabled);
                Assert.Single(window.PresetHostsList.Items);
                Assert.Equal("[All hosts]", window.PresetHostsList.Items[0].GetType().GetProperty("Title")?.GetValue(window.PresetHostsList.Items[0]));
                Assert.Equal(Visibility.Visible, window.AddHostButton.Visibility);
                Assert.False(window.AddHostButton.IsEnabled);
                Assert.True(window.RemovePresetButton.IsEnabled);
                var savedGlobalState = presetStore.ReadAsync().GetAwaiter().GetResult();
                Assert.Equal("preset-1", savedGlobalState.GlobalPresetId);
                Assert.Empty(savedGlobalState.HostPresetIds);
                var globalHost = Assert.Single(window.PresetHostsList.Items);
                Assert.NotNull(globalHost);
                Assert.Null(globalHost!.GetType().GetProperty("Preview")?.GetValue(globalHost));
                Assert.Equal("", globalHost.GetType().GetProperty("NoPreview")?.GetValue(globalHost));
                window.RemovePresetButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var removeGlobalFrame = new System.Windows.Threading.DispatcherFrame();
                var removeGlobalTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                removeGlobalTimer.Tick += (_, _) => { removeGlobalTimer.Stop(); removeGlobalFrame.Continue = false; };
                removeGlobalTimer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(removeGlobalFrame);
                Assert.Null(presetStore.ReadAsync().GetAwaiter().GetResult().GlobalPresetId);
                window.RenderSnapshot(presetTestItems, initialPresetState);
                window.PresetHostsList.SelectedIndex = 0;
                window.RemovePresetButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var removeHostFrame = new System.Windows.Threading.DispatcherFrame();
                var removeHostTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                removeHostTimer.Tick += (_, _) => { removeHostTimer.Stop(); removeHostFrame.Continue = false; };
                removeHostTimer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(removeHostFrame);
                Assert.Empty(presetStore.ReadAsync().GetAwaiter().GetResult().HostPresetIds);
                window.RenderSnapshot(presetTestItems, savedGlobalState);
                window.ComponentsModeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.UpdateLayout();
                Assert.Equal(2, window.SelectedComponents.Items.Count);
                Assert.Equal(0, window.SelectedComponents.SelectedIndex);
                Assert.True(window.InstancePropertyPanel.Children.Count > 0, window.PropertyHint.Text);
                Assert.Contains(window.InstancePropertyPanel.Children.OfType<System.Windows.Controls.TextBlock>(), label => label.Text == "Color scheme");
                Assert.Contains(window.InstancePropertyPanel.Children.OfType<System.Windows.Controls.Button>(), button => button.ToolTip?.ToString() == "Choose color");
                Assert.Equal(new[] { "Display", "Appearance" }, window.InstancePropertyPanel.Children.OfType<System.Windows.Controls.TextBlock>()
                    .Where(textBlock => Equals(textBlock.Tag, "PropertyGroupHeader")).Select(textBlock => textBlock.Text));
                Assert.Equal(5, window.InstancePropertyPanel.Children.OfType<System.Windows.Controls.CheckBox>().Count() +
                    window.InstancePropertyPanel.Children.OfType<System.Windows.Controls.ComboBox>().Count() +
                    window.InstancePropertyPanel.Children.OfType<System.Windows.Controls.TextBox>().Count() +
                    window.InstancePropertyPanel.Children.OfType<System.Windows.Controls.Slider>().Count() +
                    window.InstancePropertyPanel.Children.OfType<System.Windows.Controls.Button>().Count(button => button.ToolTip?.ToString() == "Choose color"));
                var customConfig = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["mode"] = System.Text.Json.JsonDocument.Parse("\"two\"").RootElement.Clone()
                };
                var customPreset = initialPresetState.Presets.Single() with
                {
                    Components = [firstInstance with { Config = customConfig }, secondInstance]
                };
                window.RenderSnapshot(presetTestItems, initialPresetState with { Presets = [customPreset] });
                window.SelectedComponents.SelectedIndex = 0;
                Assert.Single(window.InstancePropertyPanel.Children.OfType<System.Windows.Controls.TextBox>());
                Assert.Equal(6, window.InstancePropertyPanel.Children.OfType<System.Windows.Controls.CheckBox>().Count() +
                    window.InstancePropertyPanel.Children.OfType<System.Windows.Controls.ComboBox>().Count() +
                    window.InstancePropertyPanel.Children.OfType<System.Windows.Controls.TextBox>().Count() +
                    window.InstancePropertyPanel.Children.OfType<System.Windows.Controls.Slider>().Count() +
                    window.InstancePropertyPanel.Children.OfType<System.Windows.Controls.Button>().Count(button => button.ToolTip?.ToString() == "Choose color"));
                var enabledProperty = Assert.Single(window.InstancePropertyPanel.Children.OfType<System.Windows.Controls.CheckBox>());
                enabledProperty.IsChecked = false;
                var saveFrame = new System.Windows.Threading.DispatcherFrame();
                var saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                saveTimer.Tick += (_, _) => { saveTimer.Stop(); saveFrame.Continue = false; };
                saveTimer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(saveFrame);
                var savedPreset = presetStore.ReadAsync().GetAwaiter().GetResult().Presets.Single();
                Assert.Equal(System.Text.Json.JsonValueKind.False, savedPreset.Components[0].Config!["enabled"].ValueKind);
                Assert.False(savedPreset.Components[1].Config?.ContainsKey("enabled") == true);
                window.HostsList.SelectedIndex = 0;
                window.UpdateLayout();
                window.ComponentsList.SelectedIndex = 0;
                var creatorFrame = new System.Windows.Threading.DispatcherFrame();
                var creatorTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                creatorTimer.Tick += (_, _) => { creatorTimer.Stop(); creatorFrame.Continue = false; };
                creatorTimer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(creatorFrame);
                Assert.Contains("Creator: Creator Name", window.DetailMetadata.Text);
                window.HostsList.SelectedIndex = 0;
                var selectedHostCard = Assert.IsType<System.Windows.Controls.ListBoxItem>(window.HostsList.ItemContainerGenerator.ContainerFromIndex(0));
                var cardBorder = Assert.IsType<System.Windows.Controls.Border>(selectedHostCard.Template.FindName("Card", selectedHostCard));
                Assert.Equal(System.Windows.Media.Color.FromRgb(42, 71, 117), Assert.IsType<SolidColorBrush>(cardBorder.BorderBrush).Color);
                if (Environment.GetEnvironmentVariable("BAP_WPF_SCREENSHOT") is { Length: > 0 } screenshotPrefix)
                {
                    var visual = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                    if (visual is System.Windows.Controls.Panel panel) panel.Background = window.Background;
                    void SavePage(string suffix)
                    {
                        visual.Measure(new System.Windows.Size(1220, 790));
                        visual.Arrange(new Rect(0, 0, 1220, 790));
                        visual.UpdateLayout();
                        var bitmap = new RenderTargetBitmap(1220, 790, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(visual);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output = File.Create($"{screenshotPrefix}-{suffix}.png");
                        encoder.Save(output);
                    }
                    SavePage("installed");
                    window.PresetsNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    window.PresetList.SelectedIndex = 0;
                    SavePage("presets");
                    window.HostsModeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    SavePage("presets-hosts");
                    window.SettingsNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    SavePage("settings");
                }
                window.InstalledNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.RenderSnapshot(
                    Enumerable.Range(0, 30).Select(index => new InstalledItem(
                        $"local:host-{index}", "local", "host", $"Host wallpaper {index}", "host", null,
                        $"org.example.host-{index}", "1.0.0", null, null))
                    .Concat([new InstalledItem("local:widget", "local", "component", "Widget", "component", null,
                        "org.example.widget", "1.0.0", null, null)]).ToArray(),
                    new PresetState([], null, new Dictionary<string, string>()));
                window.UpdateLayout();
                Assert.True(window.CatalogScrollViewer.ScrollableHeight > 0);
                var firstHost = Assert.IsType<System.Windows.Controls.ListBoxItem>(window.HostsList.ItemContainerGenerator.ContainerFromIndex(0));
                var wheel = new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.InputManager.Current.PrimaryMouseDevice, Environment.TickCount, -120)
                { RoutedEvent = UIElement.PreviewMouseWheelEvent };
                firstHost.RaiseEvent(wheel);
                Assert.True(wheel.Handled, "The catalog list must take ownership of wheel input.");
                window.UpdateLayout();
                Assert.True(window.CatalogScrollViewer.VerticalOffset > 0,
                    $"offset={window.CatalogScrollViewer.VerticalOffset}, scrollable={window.CatalogScrollViewer.ScrollableHeight}, wheelLines={SystemParameters.WheelScrollLines}");
                var scrolledOffset = window.CatalogScrollViewer.VerticalOffset;
                firstHost.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.InputManager.Current.PrimaryMouseDevice, Environment.TickCount, 120)
                { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                window.UpdateLayout();
                Assert.True(window.CatalogScrollViewer.VerticalOffset < scrolledOffset);
                window.CatalogScrollViewer.ScrollToBottom();
                window.UpdateLayout();
                var bottomOffset = window.CatalogScrollViewer.VerticalOffset;
                var widget = Assert.IsType<System.Windows.Controls.ListBoxItem>(window.ComponentsList.ItemContainerGenerator.ContainerFromIndex(0));
                widget.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.InputManager.Current.PrimaryMouseDevice, Environment.TickCount, 120)
                { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                window.UpdateLayout();
                Assert.True(window.CatalogScrollViewer.VerticalOffset < bottomOffset);
                window.Close();
                application.Shutdown();
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        try { Assert.Null(failure); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }


    private sealed class StaticHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
