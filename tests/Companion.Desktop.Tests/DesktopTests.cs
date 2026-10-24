using Bap.Companion.Desktop;
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
}
