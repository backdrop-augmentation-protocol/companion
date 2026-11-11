using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Bap.Companion.Core;
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
            var preview = CreateApplication();
            using var previewLog = new CompanionLog(Path.Combine(Path.GetTempPath(), "bap-companion-ui-preview.log"));
            preview.Run(new MainWindow(previewLog, 4311, new DesktopSettings(synchronizeStartup: false)));
            return;
        }
        using var singleInstance = new Mutex(true, @"Local\BAP.Companion.Desktop", out var firstInstance);
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\BAP.Companion.Activate");
        if (!firstInstance) { activation.Set(); return; }

        var application = CreateApplication();
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        using var context = new CompanionLifetime(application, activation, args);
        application.Run();
        context.MarkRunLoopReturned();
    }

    internal static System.Windows.Application CreateApplication()
    {
        // Keep the desktop independent of the experimental Fluent theme manager.
        // Its OS-theme resource refresh has caused a fatal ScrollViewer style cycle.
        return new System.Windows.Application();
    }
}

internal sealed class CompanionLifetime : IDisposable
{
    private readonly System.Windows.Application application;
    private readonly DesktopSettings settings;
    private readonly CompanionLog log;
    private readonly Process? server;
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
    private string shutdownReason = "not-recorded";

    public CompanionLifetime(System.Windows.Application application, EventWaitHandle activation, string[] args)
    {
        this.application = application;
        var portIndex = Array.IndexOf(args, "--port");
        var port = portIndex >= 0 && portIndex + 1 < args.Length && int.TryParse(args[portIndex + 1], out var parsed) ? parsed : 4311;
        settings = new DesktopSettings();
        log = new CompanionLog();
        log.Write("desktop", $"Starting Companion WPF front end; processId={Environment.ProcessId}; executable={Environment.ProcessPath ?? "unknown"}");
        application.DispatcherUnhandledException += OnDispatcherUnhandledException;
        application.Exit += OnApplicationExit;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        server = StartServer(port);
        window = new MainWindow(log, port, settings, new HostAuthorizationClient());
        window.Closing += (_, eventArgs) =>
        {
            if (!exiting) { eventArgs.Cancel = true; window.Hide(); }
            log.Write("desktop", $"Main window closing; canceled={eventArgs.Cancel}; exiting={exiting}");
        };
        window.Closed += (_, _) => log.Write("desktop", "Main window closed");

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
        shutdownReason = "tray-exit-command";
        log.Write("desktop", "Shutdown requested from tray Exit command");
        application.Shutdown();
    }

    public void MarkRunLoopReturned()
    {
        if (shutdownReason == "not-recorded") shutdownReason = "application-run-loop-returned-without-tray-exit";
        log.Write("desktop", $"Application.Run returned; shutdownReason={shutdownReason}");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        log.Write("desktop-error", $"DispatcherUnhandledException; handled={eventArgs.Handled}; exception={eventArgs.Exception}");
    }

    private void OnApplicationExit(object? sender, ExitEventArgs eventArgs) =>
        log.Write("desktop", $"WPF Application.Exit raised; exitCode={eventArgs.ApplicationExitCode}; shutdownReason={shutdownReason}");

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
    {
        log.Write("desktop-error", $"AppDomain.UnhandledException; terminating={eventArgs.IsTerminating}; exception={eventArgs.ExceptionObject}");
    }

    private void OnProcessExit(object? sender, EventArgs eventArgs)
    {
        log.Write("desktop", $"ProcessExit raised; processId={Environment.ProcessId}; shutdownReason={shutdownReason}");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs eventArgs) =>
        log.Write("desktop-error", $"TaskScheduler.UnobservedTaskException; exception={eventArgs.Exception}");

    private Process? StartServer(int port)
    {
        var control = new HostAuthorizationClient();
        try
        {
            control.GetServiceStatusAsync().GetAwaiter().GetResult();
            log.Write("desktop", "Attached to the existing Companion service.");
            return null;
        }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException)
        {
            log.Write("desktop", "No existing Companion service found; starting the service process.");
        }
        var path = Path.Combine(AppContext.BaseDirectory, "server", "Bap.Companion.Server.exe");
        if (!File.Exists(path)) throw new FileNotFoundException("Companion Server was not found in the server subdirectory.", path);
        var start = new ProcessStartInfo(path)
        {
            WorkingDirectory = Path.GetDirectoryName(path),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add($"http://127.0.0.1:{port}");
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Companion Server.");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                control.GetServiceStatusAsync().GetAwaiter().GetResult();
                return process;
            }
            catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException)
            {
                if (process.HasExited) break;
                Thread.Sleep(100);
            }
        }
        throw new InvalidOperationException("Companion service did not become ready within 8 seconds.");
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
        server?.Dispose();
        log.Write("desktop", $"Companion UI stopping; service remains available; shutdownReason={shutdownReason}; processId={Environment.ProcessId}");
        application.DispatcherUnhandledException -= OnDispatcherUnhandledException;
        application.Exit -= OnApplicationExit;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        log.Dispose();
    }
}
