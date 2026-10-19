using Bap.Companion.Core;
using Xunit;

namespace Bap.Companion.Tests;

public sealed class CoreTests
{
    [Fact]
    public void ManifestValidatorRejectsIncompleteManifest()
    {
        var errors = ComponentManifestValidator.Validate(new ComponentManifest(new("bap", "0.1.0", "core"), "Bad ID", "", "", "component", [], new Dictionary<string, string>(), []));
        Assert.Contains(errors, e => e.StartsWith("id "));
        Assert.Contains(errors, e => e == "version must be SemVer");
        Assert.Contains(errors, e => e == "modes must contain standalone or embedded");
    }

    [Fact]
    public void ManifestValidatorAcceptsFormalDualModeManifest()
    {
        var manifest = new ComponentManifest(
            new("bap", "0.1.0", "core"), "org.example.clock", "1.0.0", "Clock", "component",
            ["config"], new Dictionary<string, string> { ["default"] = "index.html", ["standalone"] = "standalone.html", ["embedded"] = "embedded.html" },
            ["standalone", "embedded"], ConfigScopes: ["global", "wallpaper"]);
        Assert.Empty(ComponentManifestValidator.Validate(manifest));
    }

    [Fact]
    public void SessionStoreOnlyAcceptsLiveTokens()
    {
        var store = new InMemorySessionStore();
        var session = store.Create("host", TimeSpan.FromMinutes(1));
        Assert.True(store.TryGet(session.Token, out var found));
        Assert.Equal(session.Id, found.Id);
        Assert.False(store.TryGet("invalid", out _));
    }

    [Fact]
    public async Task JsonConfigStorePersistsGlobalAndWallpaperScopes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bap-companion-{Guid.NewGuid():N}", "config.json");
        try
        {
            var store = new JsonConfigStore(path);
            await store.WriteGlobalAsync(new() { ["theme"] = "dark" });
            await store.WriteWallpaperAsync("wallpaper-a", new() { ["scale"] = 1.25 });
            var config = await store.ReadAsync();
            Assert.Equal("dark", config.Global["theme"]?.ToString());
            Assert.Equal("1.25", config.Wallpapers["wallpaper-a"]["scale"]?.ToString());
        }
        finally { var directory = Path.GetDirectoryName(path); if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
