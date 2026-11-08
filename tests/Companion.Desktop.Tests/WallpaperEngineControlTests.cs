using Bap.Companion.Desktop;
using Xunit;

namespace Companion.Desktop.Tests;

public sealed class WallpaperEngineControlTests
{
    [Fact]
    public async Task ReadsSelectedWallpaperForRequestedMonitorFromConfig()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bap-we-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var config = Path.Combine(directory, "config.json");
        try
        {
            await File.WriteAllTextAsync(config, """
                {
                  "?installdirectory": "C:/Wallpaper Engine",
                  "test-user": {
                    "general": {
                      "wallpaperconfig": {
                        "selectedwallpapers": {
                          "Monitor0": { "file": "C:/wallpapers/host-a/index.html", "local": true },
                          "Monitor1": { "file": "D:/workshop/host-b/index.html", "local": false }
                        }
                      }
                    }
                  }
                }
                """);

            var current = await WallpaperEngineControl.ReadCurrentFromConfigAsync(config, 1);

            Assert.Equal(Path.GetFullPath("D:/workshop/host-b/index.html"), current);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ReturnsEmptyWhenRequestedMonitorHasNoSelectedWallpaper()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bap-we-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var config = Path.Combine(directory, "config.json");
        try
        {
            await File.WriteAllTextAsync(config, """
                { "test-user": { "general": { "wallpaperconfig": { "selectedwallpapers": {
                  "Monitor0": { "file": "C:/wallpapers/host-a/index.html" }
                } } } } }
                """);

            Assert.Equal("", await WallpaperEngineControl.ReadCurrentFromConfigAsync(config, 1));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
