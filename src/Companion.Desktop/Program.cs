using System.Diagnostics;
using System.Net.Http;
using System.Windows.Forms;

namespace Bap.Companion.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var log = new CompanionLog();
        log.Write("boot", $"Desktop (WinForms) starting; args=[{string.Join(" ", args)}]");
        using var singleInstance = new Mutex(true, @"Local\BAP.Companion.Desktop", out var firstInstance);
        EventWaitHandle? activation = firstInstance
            ? new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\BAP.Companion.Activate")
            : null;
        if (!firstInstance)
        {
            log.Write("boot", "Another instance is already running; activating it and exiting.");
            activation?.Set();
            activation?.Dispose();
            try { MessageBox.Show("BAP Companion is already running.", "BAP Companion", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            catch { }
            log.Dispose();
            return;
        }
        log.Write("boot", "Single instance acquired.");
        try
        {
            ApplicationConfiguration.Initialize();
            log.Write("boot", "Application configuration initialized.");
            var settings = new DesktopSettings();
            log.Write("boot", "Desktop settings loaded.");
            var port = ParsePort(args);
            log.Write("boot", $"Resolved listen port {port}.");
            using var context = new CompanionContext(port, activation!, args.Contains("--background"), settings, log);
            log.Write("boot", "Companion context created; running application loop.");
            Application.Run(context);
            log.Write("boot", "Application loop exited cleanly.");
        }
        catch (Exception error)
        {
            log.Write("boot-error", $"Startup failed: {error}");
            throw;
        }
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
    private readonly DesktopSettings settings;
    private readonly ToolStripItem openItem;
    private readonly ToolStripItem healthItem;
    private readonly ToolStripItem exitItem;

    public CompanionContext(int port, EventWaitHandle activation, bool background, DesktopSettings settings, CompanionLog log)
    {
        this.port = port;
        this.settings = settings;
        this.log = log;
        log.Write("desktop", "Starting Companion");
        log.Write("boot", $"Starting server subprocess on port {port}.");
        server = StartServer(port);
        log.Write("boot", $"Server subprocess started (pid={server.Id}).");
        window = new MainForm(log, port, settings);
        window.FormClosing += (_, eventArgs) => { if (eventArgs.CloseReason == CloseReason.UserClosing) { eventArgs.Cancel = true; window.Hide(); } };
        var menu = new ContextMenuStrip();
        openItem = menu.Items.Add("", null, (_, _) => ShowWindow());
        healthItem = menu.Items.Add("", null, (_, _) => Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/healthz") { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        exitItem = menu.Items.Add("", null, (_, _) => ExitThread());
        UpdateTrayLanguage();
        settings.LanguageChanged += (_, _) => UpdateTrayLanguage();
        tray = new NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "BAP Companion", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowWindow();
        activationTimer = new System.Windows.Forms.Timer { Interval = 300 };
        activationTimer.Tick += (_, _) => { if (activation.WaitOne(0)) ShowWindow(); };
        activationTimer.Start();
        if (!background) ShowWindow();
        log.Write("boot", "Tray and window initialized; desktop ready.");
    }

    private void ShowWindow()
    {
        if (!window.Visible) window.Show();
        window.WindowState = FormWindowState.Normal;
        window.Activate();
    }

    private void UpdateTrayLanguage()
    {
        openItem.Text = UiStrings.Get("openCompanion", settings.Language);
        healthItem.Text = UiStrings.Get("openHealth", settings.Language);
        exitItem.Text = UiStrings.Get("exit", settings.Language);
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
        if (!File.Exists(path)) { log.Write("boot-error", $"Server executable not found at {path}."); throw new FileNotFoundException("Companion Server was not found in the server subdirectory.", path); }
        var start = new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path), UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add($"http://127.0.0.1:{port}");
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Companion Server."); }
        catch (Exception error) { log.Write("boot-error", $"Failed to launch server subprocess: {error}"); throw; }
        log.Capture(process);
        return process;
    }
}
