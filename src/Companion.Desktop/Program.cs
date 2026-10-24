using System.Diagnostics;
using System.Net.Http;
using System.Windows.Forms;

namespace Bap.Companion.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstance = new Mutex(true, @"Local\BAP.Companion.Desktop", out var firstInstance);
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\BAP.Companion.Activate");
        if (!firstInstance) { activation.Set(); return; }
        ApplicationConfiguration.Initialize();
        using var context = new CompanionContext(ParsePort(args), activation, args.Contains("--background"));
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
    private readonly CompanionLog log;
    private readonly MainForm window;
    private readonly System.Windows.Forms.Timer activationTimer;

    public CompanionContext(int port, EventWaitHandle activation, bool background)
    {
        this.port = port;
        log = new CompanionLog();
        log.Write("desktop", "Starting Companion");
        server = StartServer(port);
        window = new MainForm(log, port);
        window.FormClosing += (_, eventArgs) => { if (eventArgs.CloseReason == CloseReason.UserClosing) { eventArgs.Cancel = true; window.Hide(); } };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open BAP Companion", null, (_, _) => ShowWindow());
        menu.Items.Add("Open health check", null, (_, _) => Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/healthz") { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        tray = new NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "BAP Companion", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowWindow();
        activationTimer = new System.Windows.Forms.Timer { Interval = 300 };
        activationTimer.Tick += (_, _) => { if (activation.WaitOne(0)) ShowWindow(); };
        activationTimer.Start();
        if (!background) ShowWindow();
    }

    private void ShowWindow()
    {
        if (!window.Visible) window.Show();
        window.WindowState = FormWindowState.Normal;
        window.Activate();
    }

    protected override void ExitThreadCore()
    {
        activationTimer.Stop();
        activationTimer.Dispose();
        window.Dispose();
        tray.Visible = false;
        tray.Dispose();
        if (!server.HasExited) server.Kill(true);
        server.Dispose();
        log.Write("desktop", "Companion stopped");
        log.Dispose();
        base.ExitThreadCore();
    }

    private Process StartServer(int port)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "server", "Bap.Companion.Server.exe");
        if (!File.Exists(path)) throw new FileNotFoundException("Companion Server was not found in the server subdirectory.", path);
        var start = new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path), UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add($"http://127.0.0.1:{port}");
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Companion Server.");
        log.Capture(process);
        return process;
    }
}
