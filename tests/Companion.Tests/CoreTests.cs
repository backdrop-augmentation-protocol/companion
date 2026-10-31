using Bap.Companion.Core;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Bap.Companion.Tests;

public sealed class CoreTests
{
    [Fact]
    public async Task SteamWorkshopCreatorLookupResolvesAndCachesPublicCreatorName()
    {
        var requests = 0;
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requests++;
            if (request.RequestUri!.Host == "api.steampowered.com")
                return JsonResponse("""{"response":{"publishedfiledetails":[{"creator":"76561198000000000"}]}}""");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<profile><steamID><![CDATA[Creator Name]]></steamID></profile>", Encoding.UTF8, "application/xml")
            };
        }));
        var lookup = new SteamWorkshopCreatorLookup(client);

        Assert.Equal("Creator Name", await lookup.GetCreatorNameAsync("123456"));
        Assert.Equal("Creator Name", await lookup.GetCreatorNameAsync("123456"));
        Assert.Equal(2, requests);
        Assert.Null(await lookup.GetCreatorNameAsync("not-an-id"));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task SteamWorkshopCreatorLookupReturnsNullWhenItemHasNoCreator()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ => JsonResponse("""{"response":{"publishedfiledetails":[]}}""")));
        var lookup = new SteamWorkshopCreatorLookup(client);
        Assert.Null(await lookup.GetCreatorNameAsync("123456"));
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    [Fact]
    public async Task PresetsResolveHostOverrideAndSurviveStorage()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bap-presets-{Guid.NewGuid():N}");
        var store = new JsonPresetStore(Path.Combine(directory, "presets.json"));
        try
        {
            var state = new PresetState(
                [new("global", "Everywhere", [new("org.example.clock")]),
                 new("host", "For Host", [new("org.example.calendar", "local:abc")])],
                "global", new Dictionary<string, string> { ["org.example.host"] = "host" });
            await store.WriteAsync(state);
            var loaded = await store.ReadAsync();
            Assert.Equal("global", PresetSelection.Resolve(loaded, "org.example.other")?.Id);
            Assert.Equal("host", PresetSelection.Resolve(loaded, "org.example.host")?.Id);
            Assert.Equal("local:abc", PresetSelection.Resolve(loaded, "org.example.host")?.Components[0].SourceKey);
            Assert.Throws<ArgumentException>(() => store.WriteAsync(state with { GlobalPresetId = "missing" }).GetAwaiter().GetResult());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task LegacyPresetMigratesOnceWithBackupAndAllowsRepeatedComponentInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bap-preset-migration-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "presets.json");
        Directory.CreateDirectory(directory);
        const string legacy = """
            {"presets":[{"id":"daily","name":"Daily","components":[{"componentId":"org.example.clock","sourceKey":"workshop:1"},{"componentId":"org.example.clock","sourceKey":"local:clock"}]}],"globalPresetId":"daily","hostPresetIds":{}}
            """;
        try
        {
            await File.WriteAllTextAsync(path, legacy);
            var store = new JsonPresetStore(path);
            var migrated = await store.ReadAsync();
            var instances = migrated.Presets.Single().Components;
            Assert.Equal(2, instances.Count);
            Assert.NotEqual(instances[0].InstanceId, instances[1].InstanceId);
            Assert.All(instances, instance =>
            {
                Assert.True(instance.Enabled);
                Assert.True(instance.UserVisible);
                Assert.Equal("automatic", instance.Layout?.Mode);
                Assert.NotNull(instance.Config);
            });
            Assert.Equal("workshop:1", instances[0].SourceKey);
            Assert.Equal("local:clock", instances[1].SourceKey);
            Assert.Equal(2, migrated.Presets.Single().Grid?.Columns);

            var backup = Assert.Single(Directory.GetFiles(directory, "presets.json.pre-instance-migration-*.bak"));
            Assert.Equal(legacy, await File.ReadAllTextAsync(backup));
            var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(instances[0].InstanceId, persisted.RootElement.GetProperty("presets")[0].GetProperty("components")[0].GetProperty("instanceId").GetString());

            var secondRead = await store.ReadAsync();
            Assert.Equal(instances.Select(instance => instance.InstanceId), secondRead.Presets.Single().Components.Select(instance => instance.InstanceId));
            Assert.Single(Directory.GetFiles(directory, "presets.json.pre-instance-migration-*.bak"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PresetStoreAdvancesRevisionOnlyWhenPresetContentChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bap-preset-revision-{Guid.NewGuid():N}");
        var store = new JsonPresetStore(Path.Combine(directory, "presets.json"));
        try
        {
            var initial = new ComponentPreset("daily", "Daily", []);
            var state = new PresetState([initial], initial.Id, new Dictionary<string, string>());
            await store.WriteAsync(state);
            var first = await store.ReadAsync();
            Assert.Equal(0, first.Presets[0].Revision);
            Assert.NotNull(first.Presets[0].UpdatedAt);

            await store.WriteAsync(first with { Presets = [first.Presets[0] with { Name = "Work" }] });
            var changed = await store.ReadAsync();
            Assert.Equal(1, changed.Presets[0].Revision);
            var updatedAt = changed.Presets[0].UpdatedAt;

            await store.WriteAsync(changed);
            var unchanged = await store.ReadAsync();
            Assert.Equal(1, unchanged.Presets[0].Revision);
            Assert.Equal(updatedAt, unchanged.Presets[0].UpdatedAt);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task InstalledCatalogPreservesLocalAndWorkshopCopiesOfSameComponent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bap-catalog-{Guid.NewGuid():N}");
        var workshop = Path.Combine(root, "workshop");
        var local = Path.Combine(root, "local");
        try
        {
            foreach (var directory in new[] { Path.Combine(workshop, "12345"), Path.Combine(local, "clock") })
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "project.json"), """
                    { "type": "web", "file": "index.html", "title": "Clock", "preview": "preview.jpg" }
                    """);
                await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), """
                    { "protocol": { "name": "bap", "version": "0.1.0", "profile": "core" }, "id": "org.example.clock", "name": "Clock", "version": "1.0.0", "role": "component", "capabilities": [], "entrypoints": { "default": "index.html", "standalone": "index.html", "embedded": "embedded.html" }, "modes": ["standalone", "embedded"] }
                    """);
            }
            var catalog = new InstalledCatalog([workshop], [local]);
            var items = await catalog.DiscoverAsync();
            Assert.Equal(2, items.Count);
            Assert.All(items, item => Assert.Equal("org.example.clock", item.BapId));
            Assert.Contains(items, item => item.Key == "workshop:12345");
            Assert.Contains(items, item => item.Key.StartsWith("local:"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InstalledCatalogRecognizesLegacyPublishedDemoHost()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bap-host-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, "host");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "project.json"), """
                { "type": "Web", "file": "index.html", "title": "BAP Host Wallpaper Demo", "preview": "preview.jpg" }
                """);
            var item = Assert.Single(await new InstalledCatalog([], [root]).DiscoverAsync());
            Assert.Equal("host", item.Role);
            Assert.Equal("org.bap.examples.host-wallpaper", item.BapId);
            Assert.Equal("legacy", item.Status);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void WallpaperPropertiesReadSupportedProjectMetadataWithoutLocalMediaOrCodeEvaluation()
    {
        using var document = JsonDocument.Parse("""
            { "general": { "properties": {
              "enabled": { "type": "bool", "text": "Enabled", "value": true, "order": 2 },
              "theme": { "type": "combo", "value": "blue", "options": [{ "value": "blue", "text": "Blue" }, { "value": "red", "text": "Red" }] },
              "opacity": { "type": "slider", "value": 0.5, "min": 0, "max": 1, "step": 0.1 },
              "caption": { "type": "textinput", "value": "Hello" },
              "tint": { "type": "color", "value": "0.1 0.2 0.3", "condition": "enabled.value == true" },
              "image": { "type": "file", "value": "secret/path.png" },
              "unsafe": { "type": "textinput", "value": "x", "condition": "window.alert(1)" }
            } } }
            """);

        var read = WallpaperPropertyReader.Read(document.RootElement);
        Assert.Equal(["caption", "opacity", "theme", "tint", "unsafe", "enabled"], read.Properties.Select(property => property.Key));
        Assert.DoesNotContain(read.Properties, property => property.Key == "image");
        Assert.False(read.Properties.Single(property => property.Key == "unsafe").ConditionSupported);
        Assert.True(read.Properties.Single(property => property.Key == "tint").ConditionSupported);
        Assert.Contains(read.Diagnostics, diagnostic => diagnostic.Contains("unsupported display condition", StringComparison.Ordinal));
        Assert.True(WallpaperPropertyReader.IsValidValue(read.Properties.Single(property => property.Key == "theme"), JsonDocument.Parse("\"red\"").RootElement));
        Assert.False(WallpaperPropertyReader.IsValidValue(read.Properties.Single(property => property.Key == "opacity"), JsonDocument.Parse("2").RootElement));
    }

    [Fact]
    public void CatalogPreviewIsConfinedToProjectAndMustBeDisplayable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bap-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var image = Path.Combine(root, "preview.jpg");
            File.WriteAllBytes(image, [0xFF, 0xD8]);
            Assert.Equal(image, InstalledCatalog.ResolvePreviewPath(root, "preview.jpg"));
            Assert.Null(InstalledCatalog.ResolvePreviewPath(root, "../outside.jpg"));
            Assert.Null(InstalledCatalog.ResolvePreviewPath(root, image));
            Assert.Null(InstalledCatalog.ResolvePreviewPath(root, "missing.png"));
            Assert.Null(InstalledCatalog.ResolvePreviewPath(root, "index.html"));
            Assert.Null(InstalledCatalog.ResolvePreviewPath(root, "bad\0name.jpg"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InstalledCatalogShowsInvalidComponentWithoutServingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bap-invalid-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"), """
                { "protocol": { "name": "bap", "version": "0.1.0", "profile": "core" }, "id": "org.example.invalid", "name": "Invalid", "version": "not-semver", "role": "component", "capabilities": [], "entrypoints": { "default": "index.html" }, "modes": ["standalone"] }
                """);
            var item = Assert.Single(await new InstalledCatalog([], [root]).DiscoverAsync());
            Assert.Equal("invalid", item.Status);
            Assert.Null(item.ComponentManifest);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void EffectivePresetKeepsOrderAndExplicitLocalSource()
    {
        var manifest = new ComponentManifest(new("bap", "0.1.0", "core"), "org.example.clock", "1.0.0", "Clock", "component", [],
            new Dictionary<string, string> { ["default"] = "index.html", ["standalone"] = "index.html", ["embedded"] = "embedded.html" }, ["standalone", "embedded"]);
        var items = new[] {
            new InstalledItem("workshop:123", "workshop", "component", "Clock subscribed", "subscribed", "123", manifest.Id, manifest.Version, null, manifest),
            new InstalledItem("local:abc", "local", "component", "Clock local", "local", null, manifest.Id, manifest.Version, null, manifest)
        };
        var state = new PresetState([new("chosen", "Local", [new("org.example.missing"), new(manifest.Id, "local:abc")])], "chosen", new Dictionary<string, string>());
        var effective = EffectiveComponents.Resolve(state, "host", items);
        Assert.Equal(["org.example.missing", manifest.Id], effective.Select(item => item.ComponentId).ToArray());
        Assert.Equal("missing", effective[0].Status);
        Assert.Equal("local:abc", effective[1].SourceKey);
        Assert.Equal("embedded.html", effective[1].EmbeddedPath);
        Assert.Equal("workshop:123", EffectiveComponents.Resolve(new([], null, new Dictionary<string, string>()), "host", items)[0].SourceKey);
    }

    [Fact]
    public void EffectiveCompositionPreservesRepeatedAndDisabledInstances()
    {
        var manifest = new ComponentManifest(new("bap", "0.1.0", "core"), "org.example.clock", "1.0.0", "Clock", "component", [],
            new Dictionary<string, string> { ["default"] = "index.html", ["standalone"] = "index.html", ["embedded"] = "embedded.html" }, ["standalone", "embedded"]);
        var items = new[] { new InstalledItem("workshop:123", "workshop", "component", "Clock", "subscribed", "123", manifest.Id, manifest.Version, null, manifest) };
        var preset = new ComponentPreset("daily", "Daily",
        [
            new("org.example.clock", "workshop:123", "clock-a", Layout: new ComponentLayout("automatic", Order: 0)),
            new("org.example.clock", "workshop:123", "clock-b", Enabled: false, Layout: new ComponentLayout("automatic", Order: 1))
        ]);
        var result = EffectiveComponents.Resolve(
            new PresetState([preset], preset.Id, new Dictionary<string, string>()),
            "org.example.host", items);

        Assert.Equal(["clock-a", "clock-b"], result.Select(item => item.InstanceId));
        Assert.Equal(["available", "disabled"], result.Select(item => item.Status));
        Assert.All(result, item => Assert.Equal("org.example.clock", item.ComponentId));
        Assert.Equal([0, 1], result.Select(item => item.Layout.Order));
    }

    [Fact]
    public void EffectiveCompositionIncludesPresetScopedLayoutAndRevision()
    {
        var preset = new ComponentPreset("work", "Work", [],
            LayoutMode: "grid", Grid: new GridLayoutSettings(Columns: 3), Revision: 7,
            UpdatedAt: DateTimeOffset.Parse("2026-10-31T08:00:00Z"));
        var snapshot = EffectiveComponents.ResolveComposition(
            new PresetState([preset], preset.Id, new Dictionary<string, string>()),
            "org.example.host", []);

        Assert.Equal("work", snapshot.PresetId);
        Assert.Equal(7, snapshot.Revision);
        Assert.Equal(preset.UpdatedAt, snapshot.UpdatedAt);
        Assert.Equal(3, snapshot.Grid?.Columns);
        Assert.Empty(snapshot.Instances);
    }

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
        Assert.Empty(found.GrantedPermissions);
        Assert.True(store.SetPermissions(session.Token, ["component.discovery", "config.read"]));
        Assert.True(store.TryGet(session.Token, out found));
        Assert.Contains("config.read", found.GrantedPermissions);
        Assert.DoesNotContain("config.write", found.GrantedPermissions);
        store.ClearPermissions(session.Token);
        Assert.True(store.TryGet(session.Token, out found));
        Assert.Empty(found.GrantedPermissions);
        Assert.False(store.TryGet("invalid", out _));
        Assert.False(store.SetPermissions("invalid", ["config.write"]));
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
    public async Task WorkshopDirectorySourceKeepsTwoSubscribedComponentsAndResourcesSeparate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bap-multi-{Guid.NewGuid():N}");
        try
        {
            foreach (var (folder, id) in new[] { ("111", "org.example.clock"), ("222", "org.example.calendar") })
            {
                var package = Path.Combine(root, folder);
                Directory.CreateDirectory(package);
                await File.WriteAllTextAsync(Path.Combine(package, "manifest.json"), $$"""
                    { "protocol": { "name": "bap", "version": "0.1.0", "profile": "core" }, "id": "{{id}}", "name": "{{folder}}", "version": "1.0.0", "role": "component", "capabilities": [], "entrypoints": { "default": "index.html", "standalone": "index.html", "embedded": "embedded.html" }, "modes": ["standalone", "embedded"] }
                    """);
                await File.WriteAllTextAsync(Path.Combine(package, "embedded.html"), folder);
            }
            var source = new WorkshopDirectorySource([root]);
            var components = await source.DiscoverAsync();
            Assert.Equal(["org.example.calendar", "org.example.clock"], components.Select(c => c.Manifest.Id).ToArray());
            var clock = await source.GetAsync("org.example.clock");
            var calendar = await source.GetAsync("org.example.calendar");
            Assert.NotNull(clock);
            Assert.NotNull(calendar);
            Assert.True(WorkshopResourceResolver.TryResolve(clock, "embedded.html", out var clockResource));
            Assert.True(WorkshopResourceResolver.TryResolve(calendar, "embedded.html", out var calendarResource));
            Assert.Equal("111", await File.ReadAllTextAsync(clockResource));
            Assert.Equal("222", await File.ReadAllTextAsync(calendarResource));
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
    public void PackageFilesRejectLinksOutsideThePackage()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"bap-package-links-{Guid.NewGuid():N}");
        var root = Path.Combine(parent, "package");
        var outside = Path.Combine(parent, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(root, "valid.html"), "ok");
        File.WriteAllText(Path.Combine(outside, "private.html"), "outside");
        File.WriteAllText(Path.Combine(outside, "preview.jpg"), "outside");
        var directoryLink = Path.Combine(root, "linked-directory");
        var fileLink = Path.Combine(root, "linked-preview.jpg");
        var component = new ComponentDescriptor(new ComponentManifest(new("bap", "0.1.0", "core"), "org.example.clock", "1.0.0", "Clock", "component", [], new Dictionary<string, string> { ["default"] = "valid.html", ["standalone"] = "valid.html", ["embedded"] = "valid.html" }, ["standalone", "embedded"]), root);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Junction creation does not require the developer-mode symlink privilege.
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/c mklink /J \"{directoryLink}\" \"{outside}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                });
                Assert.NotNull(process);
                process.WaitForExit();
                Assert.True(process.ExitCode == 0, $"mklink failed: {process.StandardOutput.ReadToEnd()} {process.StandardError.ReadToEnd()}");
            }
            else
            {
                Directory.CreateSymbolicLink(directoryLink, outside);
                File.CreateSymbolicLink(fileLink, Path.Combine(outside, "preview.jpg"));
            }
            Assert.True(WorkshopResourceResolver.TryResolve(component, "valid.html", out _));
            Assert.False(WorkshopResourceResolver.TryResolve(component, "linked-directory/private.html", out _));
            Assert.Null(InstalledCatalog.ResolvePreviewPath(root, "linked-directory/preview.jpg"));
            if (!OperatingSystem.IsWindows())
            {
                Assert.False(WorkshopResourceResolver.TryResolve(component, "linked-preview.jpg", out _));
                Assert.Null(InstalledCatalog.ResolvePreviewPath(root, "linked-preview.jpg"));
            }
        }
        finally
        {
            if (Directory.Exists(directoryLink)) Directory.Delete(directoryLink);
            if (File.Exists(fileLink)) File.Delete(fileLink);
            Directory.Delete(parent, true);
        }
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
    public void WorkshopRootsDiscoversSteamByDefaultWithoutConfiguredPaths()
    {
        var steam = Path.Combine(Path.GetTempPath(), $"bap-steam-{Guid.NewGuid():N}");
        var workshop = Path.Combine(steam, "steamapps", "workshop", "content", "431960");
        Directory.CreateDirectory(workshop);
        try
        {
            Assert.Contains(workshop, WorkshopRoots.FromEnvironment("", "", steam));
            Assert.DoesNotContain(workshop, WorkshopRoots.FromEnvironment("", "0", steam));
        }
        finally { Directory.Delete(steam, true); }
    }

    [Fact]
    public void WorkshopRootsExplicitPathOverridesAutomaticDiscovery()
    {
        var configured = Path.Combine(Path.GetTempPath(), $"bap-explicit-{Guid.NewGuid():N}");
        var steam = Path.Combine(Path.GetTempPath(), $"bap-steam-{Guid.NewGuid():N}");
        var workshop = Path.Combine(steam, "steamapps", "workshop", "content", "431960");
        Directory.CreateDirectory(workshop);
        try
        {
            Assert.Equal([configured], WorkshopRoots.FromEnvironment(configured, "", steam));
            Assert.Contains(workshop, WorkshopRoots.FromEnvironment(configured, "1", steam));
        }
        finally { Directory.Delete(steam, true); }
    }

    [Fact]
    public async Task WorkshopDirectorySourceFindsComponentSubscribedAfterStartup()
    {
        var steam = Path.Combine(Path.GetTempPath(), $"bap-steam-{Guid.NewGuid():N}");
        var component = Path.Combine(steam, "steamapps", "workshop", "content", "431960", "test-item");
        var source = new WorkshopDirectorySource(() => WorkshopRoots.FromEnvironment("", "", steam));
        try
        {
            Assert.Empty(await source.DiscoverAsync());
            Directory.CreateDirectory(component);
            await File.WriteAllTextAsync(Path.Combine(component, "manifest.json"), """
                { "protocol": { "name": "bap", "version": "0.1.0", "profile": "core" }, "id": "org.example.new-component", "name": "New Component", "version": "1.0.0", "role": "component", "capabilities": [], "entrypoints": { "default": "./index.html", "embedded": "./index.html" }, "modes": ["embedded"] }
                """);
            Assert.Equal("org.example.new-component", Assert.Single(await source.DiscoverAsync()).Manifest.Id);
        }
        finally { if (Directory.Exists(steam)) Directory.Delete(steam, true); }
    }

}
