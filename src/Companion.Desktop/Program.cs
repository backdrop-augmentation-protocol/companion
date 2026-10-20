using System.Diagnostics;
using System.Net.Http;
using System.Windows.Forms;

namespace Bap.Companion.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var context = new CompanionContext(ParsePort(args));
        Application.Run(context);
    }

    private static int ParsePort(string[] args)
    {
        var index = Array.IndexOf(args, "--port");
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var port) ? port : 4311;
    }
}

internal sealed class CompanionContext : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly int port;
    private readonly Process server;

    public CompanionContext(int port)
    {
        this.port = port;
        server = StartServer(port);
        var menu = new ContextMenuStrip();
        menu.Items.Add("BAP Companion", null, (_, _) => ShowStatus());
        menu.Items.Add("Open health check", null, (_, _) => Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/healthz") { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        tray = new NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "BAP Companion", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowStatus();
        tray.ShowBalloonTip(3000, "BAP Companion", $"Running on 127.0.0.1:{port}", ToolTipIcon.Info);
    }

    protected override void ExitThreadCore()
    {
        tray.Visible = false;
        tray.Dispose();
        if (!server.HasExited) server.Kill(true);
        server.Dispose();
        base.ExitThreadCore();
    }

    private Process StartServer(int port)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "server", "Bap.Companion.Server.exe");
        if (!File.Exists(path)) throw new FileNotFoundException("Companion Server was not found in the server subdirectory.", path);
        return Process.Start(new ProcessStartInfo(path, $"--urls http://127.0.0.1:{port}") { WorkingDirectory = Path.GetDirectoryName(path), UseShellExecute = false })
            ?? throw new InvalidOperationException("Could not start Companion Server.");
    }

    private void ShowStatus()
    {
        using var client = new HttpClient();
        try
        {
            var health = client.GetStringAsync($"http://127.0.0.1:{port}/healthz").GetAwaiter().GetResult();
            MessageBox.Show(health, "BAP Companion", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (HttpRequestException error) { MessageBox.Show(error.Message, "BAP Companion", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
