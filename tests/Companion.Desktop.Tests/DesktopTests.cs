using Bap.Companion.Desktop;
using Bap.Companion.Core;
using System.IO.Pipes;
using System.Text.Json;
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
    public async Task AuthorizationClientExchangesSnapshotsAndExplicitDecisionsOverNamedPipe()
    {
        var name = $"Bap.Companion.Test.{Guid.NewGuid():N}";
        var client = new HostAuthorizationClient(name);
        var request = new HostAuthorizationRequest("session-1", new("example.host", "Example", "1.0"),
            ["component-discovery"], ["component.discovery"], DateTimeOffset.UtcNow);
        var snapshotServer = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var snapshotExchange = Task.Run(async () =>
        {
            await using (snapshotServer)
            {
                await snapshotServer.WaitForConnectionAsync();
                using var reader = new StreamReader(snapshotServer, leaveOpen: true);
                await using var writer = new StreamWriter(snapshotServer) { AutoFlush = true };
                Assert.Equal("snapshot", JsonDocument.Parse(await reader.ReadLineAsync() ?? "{}").RootElement.GetProperty("operation").GetString());
                var response = JsonSerializer.Serialize(new AuthorizationControlResponse(true, [request]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                await writer.WriteLineAsync(response);
            }
        });

        var pending = await client.GetPendingAsync();
        await snapshotExchange;
        Assert.Equal("session-1", Assert.Single(pending).SessionId);

        var decisionServer = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var decisionExchange = Task.Run(async () =>
        {
            await using (decisionServer)
            {
                await decisionServer.WaitForConnectionAsync();
                using var reader = new StreamReader(decisionServer, leaveOpen: true);
                await using var writer = new StreamWriter(decisionServer) { AutoFlush = true };
                using var message = JsonDocument.Parse(await reader.ReadLineAsync() ?? "{}");
                Assert.Equal("approve", message.RootElement.GetProperty("operation").GetString());
                Assert.Equal("session-1", message.RootElement.GetProperty("sessionId").GetString());
                await writer.WriteLineAsync("{\"success\":true,\"requests\":[]}");
            }
        });

        Assert.True(await client.DecideAsync("session-1", true, ["component.discovery"]));
        await decisionExchange;
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
    public void DisplayPreferencePersistsWithoutChangingExistingPreferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bap-display-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "desktop.json");
        try
        {
            var settings = new DesktopSettings(path, synchronizeStartup: false);
            settings.SetDisplayIndex(2);
            settings.SetLanguage("zh-CN");
            Assert.Equal(2, settings.DisplayIndex);
            Assert.Equal(new DesktopPreferences("zh-CN", true, 2), DesktopSettings.ReadPreferences(path));
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.SetDisplayIndex(-1));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
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
                using var view = new CatalogView(key => UiStrings.Get(key, "en"), (_, _) => Task.FromResult(WallpaperSwitchResult.CommandAccepted));
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
                var catalog = Assert.Single(Descendants(form).OfType<CatalogView>());
                catalog.SetItems([
                    new InstalledItem("workshop:3825045374", "workshop", "component", "Clock", "component", "3825045374", "org.example.clock", "1.0.0", null, null)
                ]);
                settings.SetLanguage("zh-CN");
                var tabs = Assert.Single(form.Controls.OfType<TabControl>());
                Assert.Equal("设置", tabs.TabPages[2].Text);
                settings.SetLanguage("en");
                Assert.Equal("Settings", tabs.TabPages[2].Text);
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        try { Assert.Null(failure); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static IEnumerable<Control> Descendants(Control parent) =>
        parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
}
