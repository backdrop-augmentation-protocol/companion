using System.Diagnostics;

namespace Bap.Companion.Desktop;

internal sealed class CompanionLog : IDisposable
{
    private readonly object gate = new();
    private StreamWriter? writer;
    public string Path { get; private set; } = string.Empty;
    public bool IsAvailable => writer is not null;

    public CompanionLog(string? path = null)
    {
        var localPath = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BAP Companion", "logs", "companion.log");
        if (TryOpen(localPath)) return;
        if (path is not null) throw new IOException($"Could not open the requested Companion log: {path}");

        var fallback = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BAP Companion", "logs", "companion.log");
        if (!TryOpen(fallback)) Path = fallback;
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
                File.Move(Path, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "companion.previous.log"), true);
                writer = OpenWriter(Path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                writer?.Dispose();
                writer = null;
            }
        }
    }

    public void Capture(Process process)
    {
        _ = CaptureStreamAsync(process.StandardOutput, "server-bootstrap");
        _ = CaptureStreamAsync(process.StandardError, "server-bootstrap-error");
    }

    public void Dispose() { lock (gate) { writer?.Dispose(); writer = null; } }

    private bool TryOpen(string path)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024)
                File.Move(path, System.IO.Path.Combine(directory, "companion.previous.log"), true);
            writer = OpenWriter(path);
            Path = path;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            writer = null;
            return false;
        }
    }

    private async Task CaptureStreamAsync(StreamReader reader, string source)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line) Write(source, line);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            Write("desktop-error", $"Could not continue reading server startup output: {error.Message}");
        }
    }

    private static StreamWriter OpenWriter(string path) => new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
}
