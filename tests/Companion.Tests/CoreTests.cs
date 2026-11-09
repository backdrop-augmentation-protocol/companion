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
                "global", new Dictionary<string, string> { ["org.example.host"] = "host" },
                new Dictionary<string, Dictionary<string, JsonElement>>
                {
                    ["workshop:123"] = new() { ["theme"] = JsonDocument.Parse("\"blue\"").RootElement }
                });
            await store.WriteAsync(state);
            var loaded = await store.ReadAsync();
            Assert.Equal("global", PresetSelection.Resolve(loaded, "org.example.other")?.Id);
            Assert.Equal("host", PresetSelection.Resolve(loaded, "org.example.host")?.Id);
            Assert.Equal("local:abc", PresetSelection.Resolve(loaded, "org.example.host")?.Components[0].SourceKey);
            Assert.Equal("blue", loaded.HostConfigs!["workshop:123"]["theme"].GetString());
            Assert.Throws<ArgumentException>(() => store.WriteAsync(state with { GlobalPresetId = "missing" }).GetAwaiter().GetResult());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void LegacyBapHostBindingsMigrateToEveryInstalledProjectCopy()
    {
        var state = new PresetState([new("daily", "Daily", [])], null,
            new Dictionary<string, string> { ["org.example.host"] = "daily" });
        InstalledItem[] installed =
        [
            new("local:abc", "local", "host", "Local host", "host", null, "org.example.host", null, null, null),
            new("workshop:123", "workshop", "host", "Subscribed host", "host", "123", "org.example.host", null, null, null),
            new("workshop:unrelated", "workshop", "host", "Other host", "host", "456", "org.example.other", null, null, null)
        ];

        var migrated = PresetSelection.MigrateInstalledHostBindings(state, installed);

        Assert.Equal("daily", migrated.HostPresetIds["local:abc"]);
        Assert.Equal("daily", migrated.HostPresetIds["workshop:123"]);
        Assert.DoesNotContain("org.example.host", migrated.HostPresetIds.Keys);
        Assert.Equal("daily", PresetSelection.Resolve(migrated, "local:abc")?.Id);
        Assert.Equal("daily", PresetSelection.Resolve(migrated, "workshop:123")?.Id);
    }

    [Fact]
    public void HostProjectUrlResolvesTheSpecificInstalledCopyWithoutChangingBapIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "bap-host-source-test");
        InstalledItem[] items =
        [
            new("local:abc", "local", "host", "Local host", root + "\\local", null, "org.example.host", null, null, null),
            new("workshop:123", "workshop", "host", "Workshop host", root + "\\workshop", "123", "org.example.host", null, null, null)
        ];
        var sourceUrl = new Uri(Path.Combine(root, "workshop", "index.html")).AbsoluteUri;

        Assert.Equal("workshop:123", InstalledCatalog.MatchHostKey(sourceUrl, "org.example.host", items));
        Assert.Null(InstalledCatalog.MatchHostKey("https://example.com/index.html", "org.example.host", items));
        Assert.Null(InstalledCatalog.MatchHostKey(sourceUrl, "org.example.other", items));
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
    public async Task InstalledCatalogDiscoversNonBapWebWallpaperAsUnverifiedHost()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bap-non-bap-host-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, "wallpaper");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "project.json"), """
                { "type": "web", "file": "index.html", "title": "Ordinary Web Wallpaper" }
                """);
            var catalog = new InstalledCatalog([], [root]);

            var item = Assert.Single(await catalog.DiscoverAsync());

            Assert.Equal("host", item.Role);
            Assert.Null(item.BapId);
            Assert.Equal("unverified", item.Status);
            Assert.Equal("Ordinary Web Wallpaper", item.Title);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InstalledCatalogDoesNotTreatManifestOnlyDirectoryAsWallpaperHost()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bap-manifest-only-host-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"), """
                { "role": "host", "id": "org.example.host", "version": "1.0.0" }
                """);

            Assert.Empty(await new InstalledCatalog([], [root]).DiscoverAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InstalledCatalogKeepsWebWallpaperWithBooleanFractionMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bap-boolean-fraction-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "project.json"), """
                { "type": "web", "title": "Existing WE wallpaper", "file": "index.html", "general": { "properties": {
                  "volume": { "type": "slider", "value": 50, "min": 0, "max": 100, "fraction": false }
                } } }
                """);

            var item = Assert.Single(await new InstalledCatalog([root], []).DiscoverAsync());
            var property = Assert.Single(item.Properties!);

            Assert.Equal("Existing WE wallpaper", item.Title);
            Assert.Equal("volume", property.Key);
            Assert.Null(property.Fraction);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void WallpaperPropertiesReadSupportedProjectMetadataWithoutLocalMediaOrCodeEvaluation()
    {
        using var document = JsonDocument.Parse("""
            { "general": { "properties": {
              "enabled": { "type": "bool", "text": "Enabled", "value": true, "order": 2 },
              "theme": { "type": "combo", "value": "blue", "order": 10, "options": [{ "value": "blue", "label": "Blue" }, { "value": "red", "text": "Red" }] },
              "opacity": { "type": "slider", "value": 0.5, "min": 0, "max": 1, "step": 0.1 },
              "caption": { "type": "textinput", "value": "Hello" },
              "tint": { "type": "color", "value": "0.1 0.2 0.3", "condition": "enabled.value == true" },
              "image": { "type": "file", "value": "secret/path.png" },
              "unsafe": { "type": "textinput", "value": "x", "condition": "window.alert(1)" }
            } } }
            """);

        var read = WallpaperPropertyReader.Read(document.RootElement);
        Assert.Equal(["caption", "opacity", "tint", "unsafe", "enabled", "theme"], read.Properties.Select(property => property.Key));
        Assert.Equal(10, read.Properties.Single(property => property.Key == "theme").Order);
        Assert.Equal("Blue", read.Properties.Single(property => property.Key == "theme").Options![0].Text);
        Assert.DoesNotContain(read.Properties, property => property.Key == "image");
        Assert.False(read.Properties.Single(property => property.Key == "unsafe").ConditionSupported);
        Assert.True(read.Properties.Single(property => property.Key == "tint").ConditionSupported);
        Assert.True(WallpaperPropertyReader.IsConditionMet(read.Properties.Single(property => property.Key == "tint"),
            new Dictionary<string, JsonElement> { ["enabled"] = JsonDocument.Parse("true").RootElement }));
        Assert.False(WallpaperPropertyReader.IsConditionMet(read.Properties.Single(property => property.Key == "tint"),
            new Dictionary<string, JsonElement> { ["enabled"] = JsonDocument.Parse("false").RootElement }));
        Assert.Contains(read.Diagnostics, diagnostic => diagnostic.Contains("unsupported display condition", StringComparison.Ordinal));
        Assert.True(WallpaperPropertyReader.IsValidValue(read.Properties.Single(property => property.Key == "theme"), JsonDocument.Parse("\"red\"").RootElement));
        Assert.False(WallpaperPropertyReader.IsValidValue(read.Properties.Single(property => property.Key == "opacity"), JsonDocument.Parse("2").RootElement));
    }

    [Fact]
    public void WallpaperPropertyReaderIgnoresBooleanFractionInsteadOfRejectingWholeWallpaper()
    {
        using var document = JsonDocument.Parse("""
            { "general": { "properties": {
              "volume": { "type": "slider", "value": 50, "min": 0, "max": 100, "fraction": false }
            } } }
            """);

        var read = WallpaperPropertyReader.Read(document.RootElement);

        Assert.Equal("volume", Assert.Single(read.Properties).Key);
        Assert.Null(read.Properties[0].Fraction);
    }

    [Fact]
    public void WallpaperPropertyLocalizationUsesFallbacksWithoutChangingValues()
    {
        using var project = JsonDocument.Parse("""{ "general": { "properties": { "format": { "type": "combo", "text": "Format", "value": "24h", "options": [{ "value": "24h", "text": "24-hour" }] }, "showDate": { "type": "bool", "text": "Show date", "value": true } } } }""");
        using var sidecar = JsonDocument.Parse("""{ "schemaVersion": "0.1.0", "properties": { "format": { "labels": { "en": "Time format", "zh-CN": "时间格式" }, "options": [{ "value": "24h", "labels": { "en": "24-hour", "zh-CN": "24 小时制" } }] }, "showDate": { "labels": { "zh": "显示日期" } } } }""");

        var read = WallpaperPropertyReader.Read(project.RootElement, sidecar.RootElement);
        var format = read.Properties.Single(property => property.Key == "format");
        var showDate = read.Properties.Single(property => property.Key == "showDate");
        Assert.Equal("时间格式", WallpaperPropertyReader.ResolveLocalizedLabel(format.LocalizedLabels, "zh-CN"));
        Assert.Equal("24 小时制", WallpaperPropertyReader.ResolveLocalizedLabel(format.Options![0].LocalizedLabels, "zh-CN"));
        Assert.Equal("显示日期", WallpaperPropertyReader.ResolveLocalizedLabel(showDate.LocalizedLabels, "zh-CN"));
        Assert.Null(WallpaperPropertyReader.ResolveLocalizedLabel(format.LocalizedLabels, "fr-FR"));
        Assert.Equal("Format", format.Text); // UI falls back to the original WE label when no sidecar locale matches.
        Assert.Equal("24h", format.DefaultValue.GetString());
        Assert.True(WallpaperPropertyReader.IsValidValue(format, format.DefaultValue));
        Assert.Empty(read.Diagnostics);
    }

    [Fact]
    public void InvalidPropertyLocalizationDoesNotHideWallpaperProperties()
    {
        using var project = JsonDocument.Parse("""{ "general": { "properties": { "caption": { "type": "textinput", "text": "Caption", "value": "hello" } } } }""");
        using var sidecar = JsonDocument.Parse("""{ "schemaVersion": "0.1.0", "properties": { "missing": { "labels": { "en": "Missing" } } } }""");

        var read = WallpaperPropertyReader.Read(project.RootElement, sidecar.RootElement);
        Assert.Equal("caption", Assert.Single(read.Properties).Key);
        Assert.Contains(read.Diagnostics, diagnostic => diagnostic.Contains("unknown property", StringComparison.Ordinal));
        Assert.Equal("Caption", read.Properties[0].Text);
    }

    [Fact]
    public async Task InstalledCatalogIgnoresMalformedPropertyLocalizationSidecar()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bap-localization-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "project.json"), """{ "type": "web", "title": "Localized component", "general": { "properties": { "caption": { "type": "textinput", "text": "Caption", "value": "hello" } } } }""");
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"), """{ "protocol": { "name": "bap", "version": "0.1.0", "profile": "core" }, "id": "org.example.localized", "name": "Localized", "version": "1.0.0", "role": "component", "capabilities": ["config"], "entrypoints": { "default": "index.html", "standalone": "standalone.html", "embedded": "embedded.html" }, "modes": ["standalone", "embedded"] }""");
            await File.WriteAllTextAsync(Path.Combine(root, "bap-properties.json"), "{broken");

            var item = Assert.Single(await new InstalledCatalog([], [root]).DiscoverAsync());
            Assert.Equal("valid", item.Status);
            Assert.Equal("caption", Assert.Single(item.Properties!).Key);
            Assert.Contains(item.PropertyDiagnostics!, diagnostic => diagnostic.Contains("invalid JSON", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InstalledCatalogReadsEditablePropertiesFromHostProjects()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bap-host-properties-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "project.json"), """{ "type": "web", "title": "Host", "general": { "properties": { "accent": { "type": "color", "text": "Accent", "value": "0.2 0.4 0.8" } } } }""");
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"), """{ "protocol": { "name": "bap", "version": "0.1.0", "profile": "core" }, "id": "org.example.host", "name": "Host", "version": "1.0.0", "role": "host", "capabilities": [], "entrypoints": { "default": "index.html" } }""");

            var item = Assert.Single(await new InstalledCatalog([], [root]).DiscoverAsync());
            Assert.Equal("host", item.Role);
            Assert.Equal("accent", Assert.Single(item.Properties!).Key);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
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
        Assert.Empty(EffectiveComponents.Resolve(new([], null, new Dictionary<string, string>()), "host", items));
    }

    [Fact]
    public void EffectiveCompositionPreservesRepeatedAndDisabledInstances()
    {
        var manifest = new ComponentManifest(new("bap", "0.1.0", "core"), "org.example.clock", "1.0.0", "Clock", "component", [],
            new Dictionary<string, string> { ["default"] = "index.html", ["standalone"] = "index.html", ["embedded"] = "embedded.html" }, ["standalone", "embedded"]);
        using var project = JsonDocument.Parse("""{ "general": { "properties": { "format": { "type": "combo", "value": "24h", "options": [{ "value": "24h", "text": "24-hour" }, { "value": "12h", "text": "12-hour" }] }, "showDate": { "type": "bool", "value": true }, "media": { "type": "file", "value": "not-imported.png" } } } }""");
        var definitions = WallpaperPropertyReader.Read(project.RootElement).Properties;
        var items = new[] { new InstalledItem("workshop:123", "workshop", "component", "Clock", "subscribed", "123", manifest.Id, manifest.Version, null, manifest, Properties: definitions, MaxInstances: 2) };
        var overridden = new Dictionary<string, JsonElement> { ["format"] = JsonDocument.Parse("\"12h\"").RootElement, ["media"] = JsonDocument.Parse("\"C:\\\\private.png\"").RootElement };
        var preset = new ComponentPreset("daily", "Daily",
        [
            new("org.example.clock", "workshop:123", "clock-a", Layout: new ComponentLayout("automatic", Order: 0), Config: overridden),
            new("org.example.clock", "workshop:123", "clock-b", Enabled: false, Layout: new ComponentLayout("automatic", Order: 1)),
            new("org.example.clock", "workshop:123", "clock-c", Layout: new ComponentLayout("automatic", Order: 2))
        ], Revision: 4);
        var result = EffectiveComponents.Resolve(
            new PresetState([preset], preset.Id, new Dictionary<string, string>()),
            "org.example.host", items);

        Assert.Equal(["clock-a", "clock-b", "clock-c"], result.Select(item => item.InstanceId));
        Assert.Equal(["available", "disabled", "unavailable"], result.Select(item => item.Status));
        Assert.All(result, item => Assert.Equal("org.example.clock", item.ComponentId));
        Assert.Equal([0, 1, 2], result.Select(item => item.Layout.Order));
        Assert.Equal("12h", result[0].Config["format"].GetString());
        Assert.Equal(4, result[0].ConfigRevision);
        Assert.True(result[0].Config["showDate"].GetBoolean());
        Assert.DoesNotContain("media", result[0].Config.Keys);
        Assert.Equal("24h", result[1].Config["format"].GetString());
        Assert.Equal("24h", result[2].Config["format"].GetString());
    }

    [Fact]
    public void ComponentInstanceLimitCountsDisabledInstancesAndUsesStrictestInstalledDeclaration()
    {
        var preset = new ComponentPreset("daily", "Daily",
        [
            new("org.example.game-ui", "workshop:1", "ui-a", Enabled: false),
            new("org.example.game-ui", "local:ui", "ui-b")
        ]);
        var items = new[]
        {
            new InstalledItem("workshop:1", "workshop", "component", "Game UI", "workshop", "1", "org.example.game-ui", "1.0.0", null, null, MaxInstances: 2),
            new InstalledItem("local:ui", "local", "component", "Game UI dev", "local", null, "org.example.game-ui", "1.1.0", null, null, MaxInstances: 1)
        };

        var errors = PresetSelection.ValidateInstanceLimits(preset, items);

        Assert.Single(errors);
        Assert.Contains("maximum instance count of 1", errors[0], StringComparison.Ordinal);
        Assert.Empty(PresetSelection.ValidateInstanceLimits(preset with { Components = [preset.Components[0]] }, items));
        Assert.Empty(PresetSelection.ValidateInstanceLimits(preset, items.Select(item => item with { MaxInstances = null }).ToArray()));
    }

    [Fact]
    public async Task InstalledCatalogReadsComponentInstanceLimitSidecarAndIgnoresMalformedSidecar()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bap-component-metadata-{Guid.NewGuid():N}");
        var component = Path.Combine(root, "component");
        Directory.CreateDirectory(component);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(component, "project.json"), """{"type":"web","title":"Widget","file":"index.html"}""");
            await File.WriteAllTextAsync(Path.Combine(component, "manifest.json"), """{"protocol":{"name":"bap","version":"0.1.0","profile":"core"},"id":"org.example.widget","name":"Widget","version":"1.0.0","role":"component","capabilities":[],"entrypoints":{"default":"index.html","standalone":"index.html","embedded":"embedded.html"},"modes":["standalone","embedded"]}""");
            await File.WriteAllTextAsync(Path.Combine(component, "bap-component.json"), """{"schemaVersion":"0.1.0","maxInstances":1}""");
            var catalog = new InstalledCatalog([], [root]);

            var item = Assert.Single(await catalog.DiscoverAsync());
            Assert.Equal(1, item.MaxInstances);

            await File.WriteAllTextAsync(Path.Combine(component, "bap-component.json"), "not-json");
            item = Assert.Single(await catalog.DiscoverAsync(force: true));
            Assert.Null(item.MaxInstances);
            Assert.Contains(item.PropertyDiagnostics!, diagnostic => diagnostic.Contains("invalid JSON", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void UnassignedHostReceivesEmptyGridComposition()
    {
        var composition = EffectiveComponents.ResolveComposition(
            new PresetState([], null, new Dictionary<string, string>()), "host", []);

        Assert.Equal("unassigned", composition.PresetId);
        Assert.Equal("grid", composition.LayoutMode);
        Assert.Equal(new GridLayoutSettings(), composition.Grid);
        Assert.Empty(composition.Instances);
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
