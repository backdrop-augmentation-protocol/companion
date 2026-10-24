using System.Diagnostics;

namespace Bap.Companion.Desktop;

internal sealed class CompanionLog : IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter writer;
    public string Path { get; }

    public CompanionLog()
    {
        var directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BAP Companion", "logs");
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "companion.log");
        if (File.Exists(Path) && new FileInfo(Path).Length > 5 * 1024 * 1024)
            File.Move(Path, System.IO.Path.Combine(directory, "companion.previous.log"), true);
        writer = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public void Write(string source, string line)
    {
        // Session bearer tokens can appear in request URLs; never persist them.
        var sanitized = System.Text.RegularExpressions.Regex.Replace(line, @"(?i)(token=)[^&\s]+", "$1[redacted]");
        lock (gate) writer.WriteLine($"{DateTimeOffset.Now:O} [{source}] {sanitized}");
    }

    public void Capture(Process process)
    {
        _ = Task.Run(async () => { while (await process.StandardOutput.ReadLineAsync() is { } line) Write("server", line); });
        _ = Task.Run(async () => { while (await process.StandardError.ReadLineAsync() is { } line) Write("server-error", line); });
    }

    public void Dispose() { lock (gate) writer.Dispose(); }
}
