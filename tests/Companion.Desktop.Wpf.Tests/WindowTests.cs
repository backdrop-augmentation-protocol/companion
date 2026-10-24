using System.IO;
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
                var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml")
                });
                var settings = new DesktopSettings(Path.Combine(directory, "desktop.json"), synchronizeStartup: false);
                using var log = new CompanionLog(Path.Combine(directory, "companion.log"));
                var window = new MainWindow(log, 4312, settings);
                Assert.Equal("Installed", window.InstalledNav.Content);
                settings.SetLanguage("zh-CN");
                Assert.Equal("已安装", window.InstalledNav.Content);
                settings.SetLanguage("en");
                Assert.Equal("Settings", window.SettingsNav.Content);
                window.RenderSnapshot(
                [
                    new InstalledItem("local:host", "local", "host", "Demo host wallpaper", "host", null, "org.example.host", "1.0.0", null, null),
                    new InstalledItem("workshop:123", "workshop", "component", "Time widget", "component", "123", "org.example.time", "1.0.0", null, null),
                    new InstalledItem("local:calendar", "local", "component", "Calendar widget", "component", null, "org.example.calendar", "1.0.0", null, null)
                ], new PresetState(
                    [new ComponentPreset("preset-1", "Daily setup", [new PresetComponent("org.example.time", "workshop:123")])],
                    "preset-1", new Dictionary<string, string>()));
                window.HostsList.SelectedIndex = 0;
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
}
