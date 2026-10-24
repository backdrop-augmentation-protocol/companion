using Bap.Companion.Desktop;
using Bap.Companion.Core;
using Xunit;

namespace Companion.Desktop.Tests;

public sealed class DesktopTests
{
    [Fact]
    public void PreferencesDefaultToEnglishAndStartupEnabled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bap-desktop-{Guid.NewGuid():N}.json");
        Assert.Equal(new DesktopPreferences("en", true), DesktopSettings.ReadPreferences(path));
    }

    [Fact]
    public void PreferencesPreserveExplicitStartupOptOutAndChinese()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bap-desktop-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"Language":"zh-CN","AutoStart":false}""");
            Assert.Equal(new DesktopPreferences("zh-CN", false), DesktopSettings.ReadPreferences(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void LocalizedStringsUseRequestedLanguage()
    {
        Assert.Equal("Settings", UiStrings.Get("settings", "en"));
        Assert.Equal("设置", UiStrings.Get("settings", "zh-CN"));
    }

    [Fact]
    public void CatalogViewConstructsCardsForBothRolesWithoutPreviewFiles()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var view = new CatalogView(key => UiStrings.Get(key, "en"), (_, _) => Task.CompletedTask);
                view.SetItems([
                    new InstalledItem("local:host", "local", "host", "Demo host", "host", null, "org.example.host", "1.0.0", null, null),
                    new InstalledItem("workshop:123", "workshop", "component", "Clock", "component", "123", "org.example.clock", "1.0.0", null, null)
                ]);
                view.RefreshLanguage();
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void MainWindowConstructsWithoutStartingServiceOrChangingStartupRegistration()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bap-window-{Guid.NewGuid():N}");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var settings = new DesktopSettings(Path.Combine(directory, "desktop.json"), synchronizeStartup: false);
                using var log = new CompanionLog(Path.Combine(directory, "companion.log"));
                using var form = new MainForm(log, 4312, settings);
                Assert.Equal("BAP Companion", form.Text);
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
