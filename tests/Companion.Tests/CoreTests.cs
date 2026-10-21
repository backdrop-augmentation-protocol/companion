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

    [Fact]
    public async Task WorkshopDirectorySourceDiscoversOnlyValidComponents()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bap-workshop-{Guid.NewGuid():N}");
        var componentDirectory = Path.Combine(root, "clock");
        Directory.CreateDirectory(componentDirectory);
        await File.WriteAllTextAsync(Path.Combine(componentDirectory, "manifest.json"), """
            { "protocol": { "name": "bap", "version": "0.1.0", "profile": "core" }, "id": "org.example.clock", "name": "Clock", "version": "1.0.0", "role": "component", "capabilities": [], "entrypoints": { "default": "./index.html", "standalone": "./index.html", "embedded": "./index.html" }, "modes": ["standalone", "embedded"] }
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "invalid.json"), "{ \"id\": \"invalid\" }");
        try
        {
            var source = new WorkshopDirectorySource([root]);
            var components = await source.DiscoverAsync();
            Assert.Single(components);
            Assert.Equal("org.example.clock", components[0].Manifest.Id);
            Assert.Equal(componentDirectory, components[0].Location);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void WorkshopResourceResolverRejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bap-resource-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var resource = Path.Combine(root, "index.html");
        File.WriteAllText(resource, "ok");
        var component = new ComponentDescriptor(new ComponentManifest(new("bap", "0.1.0", "core"), "org.example.clock", "1.0.0", "Clock", "component", [], new Dictionary<string, string> { ["default"] = "index.html", ["standalone"] = "index.html", ["embedded"] = "index.html" }, ["standalone", "embedded"]), root);
        try
        {
            Assert.True(WorkshopResourceResolver.TryResolve(component, "index.html", out var resolved));
            Assert.Equal(resource, resolved);
            Assert.False(WorkshopResourceResolver.TryResolve(component, "../index.html", out _));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SteamWorkshopLocatorReadsLibraryFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bap-steam-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "library");
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        Directory.CreateDirectory(Path.Combine(library, "steamapps", "workshop", "content", "431960"));
        File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"), $"\"libraryfolders\" {{ \"1\" {{ \"path\" \"{library.Replace("\\", "\\\\")}\" }} }}");
        try
        {
            var roots = SteamWorkshopLocator.FindRoots(root);
            Assert.Contains(Path.Combine(library, "steamapps", "workshop", "content", "431960"), roots);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ResourceTicketIsScopedToComponent()
    {
        var tickets = new ResourceTickets();
        var ticket = tickets.Create("org.example.clock", TimeSpan.FromMinutes(1));
        Assert.True(tickets.Accepts(ticket, "org.example.clock"));
        Assert.False(tickets.Accepts(ticket, "org.example.other"));
        Assert.False(tickets.Accepts("invalid", "org.example.clock"));
    }
}
