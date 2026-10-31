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
                using var creatorHttpClient = new HttpClient(new StaticHttpMessageHandler(request =>
                    request.RequestUri!.Host == "api.steampowered.com"
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"response\":{\"publishedfiledetails\":[{\"creator\":\"76561198000000000\"}]}}", Encoding.UTF8, "application/json") }
                        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<profile><steamID>Creator Name</steamID></profile>", Encoding.UTF8, "application/xml") }));
                var window = new MainWindow(log, 4312, settings, creatorLookup: new SteamWorkshopCreatorLookup(creatorHttpClient));
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
                window.RenderSnapshot(
                [
                    new InstalledItem("local:host", "local", "host", "Demo host wallpaper", "host", null, "org.example.host", "1.0.0", null, null),
                    new InstalledItem("workshop:123", "workshop", "component", "Time widget", "component", "123", "org.example.time", "1.0.0", null, null),
                    new InstalledItem("local:calendar", "local", "component", "Calendar widget", "component", null, "org.example.calendar", "1.0.0", null, null)
                ], new PresetState(
                    [new ComponentPreset("preset-1", "Daily setup", [new PresetComponent("org.example.time", "workshop:123")])],
                    "preset-1", new Dictionary<string, string>()));
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
