using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Bap.Companion.Desktop.Wpf;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var uiPreview = args.Contains("--preview-ui");
#if DEBUG
        uiPreview = true;
#endif
        if (uiPreview)
        {
            var previewLog = new CompanionLog(Path.Combine(Path.GetTempPath(), "bap-companion-ui-preview.log"));
            previewLog.Write("boot", $"Desktop (WPF) preview UI starting; args=[{string.Join(" ", args)}]");
            var preview = CreateApplication();
            try { preview.Run(new MainWindow(previewLog, 4311, new DesktopSettings(synchronizeStartup: false))); }
            catch (Exception error) { previewLog.Write("boot-error", $"Preview UI failed: {error}"); throw; }
            previewLog.Write("boot", "Preview UI exited.");
            return;
        }
        // Surface logging failures through a dialog instead of failing silently.
        CompanionLog.FallbackError = message =>
        {
            try { System.Windows.MessageBox.Show(message, "BAP Companion", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); }
            catch { }
        };

        var log = new CompanionLog();
        log.Write("boot", $"Desktop (WPF) starting; args=[{string.Join(" ", args)}]");
        using var singleInstance = new Mutex(true, @"Local\BAP.Companion.Desktop", out var firstInstance);
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\BAP.Companion.Activate");
        if (!firstInstance)
        {
            log.Write("boot", "Another instance is already running; activating it and exiting.");
            activation.Set();
            try
            {
                System.Windows.MessageBox.Show(
                    "BAP Companion 已在运行。\n\nBAP Companion is already running.",
                    "BAP Companion", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            catch { }
            log.Dispose();
            return;
        }
        log.Write("boot", "Single instance acquired.");
        try
        {
            var application = CreateApplication();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            using var context = new CompanionLifetime(application, activation, args, log);
            log.Write("boot", "Companion lifetime created; running application loop.");
            application.Run();
            log.Write("boot", "Application loop exited cleanly.");
        }
        catch (Exception error)
        {
            log.Write("boot-error", $"Startup failed: {error}");
            // Fallback channel so the failure reason is shown even if the log file is unavailable.
            try { System.Windows.MessageBox.Show($"BAP Companion failed to start.\n\n{error.Message}", "BAP Companion", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error); }
            catch { }
            throw;
        }
    }

    private static System.Windows.Application CreateApplication()
    {
#pragma warning disable WPF0001 // .NET 10 Fluent dark mode keeps native controls interactive and visually consistent.
        return new System.Windows.Application { ThemeMode = ThemeMode.Dark };
#pragma warning restore WPF0001
    }
}

internal sealed class CompanionLifetime : IDisposable
{
    private readonly System.Windows.Application application;
    private readonly DesktopSettings settings;
    private readonly CompanionLog log;
    private readonly Process server;
    private readonly MainWindow window;
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
    private readonly Stream trayIconStream;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Forms.ToolStripItem openItem;
    private readonly Forms.ToolStripItem healthItem;
    private readonly Forms.ToolStripItem exitItem;
    private readonly DispatcherTimer activationTimer;
    private bool exiting;

    public CompanionLifetime(System.Windows.Application application, EventWaitHandle activation, string[] args, CompanionLog log)
    {
        this.application = application;
        this.log = log;
        var portIndex = Array.IndexOf(args, "--port");
        var port = portIndex >= 0 && portIndex + 1 < args.Length && int.TryParse(args[portIndex + 1], out var parsed) ? parsed : 4311;
        settings = new DesktopSettings();
        log.Write("desktop", "Starting Companion WPF front end");
        log.Write("boot", $"Starting server subprocess on port {port}.");
        server = StartServer(port);
        log.Write("boot", $"Server subprocess started (pid={server.Id}).");
        window = new MainWindow(log, port, settings);
        window.Closing += (_, eventArgs) => { if (!exiting) { eventArgs.Cancel = true; window.Hide(); } };

        menu = new Forms.ContextMenuStrip();
        openItem = menu.Items.Add("", null, (_, _) => ShowWindow());
        healthItem = menu.Items.Add("", null, (_, _) => Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/healthz") { UseShellExecute = true }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        exitItem = menu.Items.Add("", null, (_, _) => Exit());
        UpdateTrayLanguage();
        settings.LanguageChanged += OnLanguageChanged;
        trayIconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Bap.Companion.Desktop;component/Assets/bap-companion.ico"))?.Stream
            ?? throw new InvalidOperationException("BAP Companion icon resource is missing.");
        trayIcon = new System.Drawing.Icon(trayIconStream);
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "BAP Companion", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowWindow();

        activationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        activationTimer.Tick += (_, _) => { if (activation.WaitOne(0)) ShowWindow(); };
        activationTimer.Start();
        if (!args.Contains("--background")) ShowWindow();
        log.Write("boot", "Tray and window initialized; desktop ready.");
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => UpdateTrayLanguage();

    private void UpdateTrayLanguage()
    {
        openItem.Text = UiStrings.Get("openCompanion", settings.Language);
        healthItem.Text = UiStrings.Get("openHealth", settings.Language);
        exitItem.Text = UiStrings.Get("exit", settings.Language);
    }

    private void ShowWindow()
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void Exit()
    {
        if (exiting) return;
        exiting = true;
        application.Shutdown();
    }

    private Process StartServer(int port)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "server", "Bap.Companion.Server.exe");
        if (!File.Exists(path)) { log.Write("boot-error", $"Server executable not found at {path}."); throw new FileNotFoundException("Companion Server was not found in the server subdirectory.", path); }
        var start = new ProcessStartInfo(path)
        {
            WorkingDirectory = Path.GetDirectoryName(path), UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add($"http://127.0.0.1:{port}");
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Companion Server."); }
        catch (Exception error) { log.Write("boot-error", $"Failed to launch server subprocess: {error}"); throw; }
        log.Capture(process);
        return process;
    }

    public void Dispose()
    {
        activationTimer.Stop();
        settings.LanguageChanged -= OnLanguageChanged;
        tray.Visible = false;
        tray.Dispose();
        trayIcon.Dispose();
        trayIconStream.Dispose();
        menu.Dispose();
        if (!server.HasExited) server.Kill(true);
        server.Dispose();
        log.Write("desktop", "Companion stopped");
        log.Dispose();
    }
}
