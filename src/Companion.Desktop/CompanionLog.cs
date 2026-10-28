using System.Diagnostics;

namespace Bap.Companion.Desktop;

internal sealed class CompanionLog : IDisposable
{
    // Fallback reporting channel that does NOT depend on the log file. When logging cannot
    // be initialized or written, the desktop front end can surface the reason to the user
    // through a dialog instead of failing silently.
    public static Action<string>? FallbackError;

    private readonly object gate = new();
    private StreamWriter? writer;
    public string Path { get; }

    public CompanionLog(string? path = null)
    {
        var directory = path is null
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BAP Companion", "logs")
            : System.IO.Path.GetDirectoryName(path)!;
        Path = path ?? System.IO.Path.Combine(directory, "companion.log");
        // If the log directory or file cannot be created/written, degrade to no logging
        // instead of crashing startup, but report the reason through the fallback channel
        // so the failure is visible to the user.
        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(Path) && new FileInfo(Path).Length > 5 * 1024 * 1024)
                File.Move(Path, System.IO.Path.Combine(directory, "companion.previous.log"), true);
            writer = OpenWriter();
        }
        catch (Exception error)
        {
            writer = null;
            Path = string.Empty;
            FallbackError?.Invoke($"无法创建或写入日志（{directory}）：{error.Message}。本次运行将不记录日志。");
        }
    }

    public void Write(string source, string line)
    {
        // Session bearer tokens can appear in request URLs; never persist them.
        var sanitized = System.Text.RegularExpressions.Regex.Replace(line, @"(?i)(token=)[^&\s]+", "$1[redacted]");
        lock (gate)
        {
            if (writer is null) return;
            try
            {
                writer.WriteLine($"{DateTimeOffset.Now:O} [{source}] {sanitized}");
                if (writer.BaseStream.Length <= 5 * 1024 * 1024) return;
                writer.Dispose();
                File.Move(Path, System.IO.Path.GetDirectoryName(Path)! is { } dir ? System.IO.Path.Combine(dir, "companion.previous.log") : Path + ".previous", true);
                writer = OpenWriter();
            }
            catch (Exception error)
            {
                writer = null;
                FallbackError?.Invoke($"日志写入失败，后续日志将不再记录：{error.Message}");
            }
        }
    }

    public void Capture(Process process)
    {
        _ = Task.Run(async () => { while (await process.StandardOutput.ReadLineAsync() is { } line) Write("server", line); });
        _ = Task.Run(async () => { while (await process.StandardError.ReadLineAsync() is { } line) Write("server-error", line); });
    }

    public void Dispose() { lock (gate) writer?.Dispose(); }

    private StreamWriter OpenWriter() => new(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
}
