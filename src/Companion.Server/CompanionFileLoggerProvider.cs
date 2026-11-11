using Microsoft.Extensions.Logging;

namespace Bap.Companion.Server;

internal sealed class CompanionFileLoggerProvider : ILoggerProvider
{
    private const long MaximumLogBytes = 5 * 1024 * 1024;
    private readonly object gate = new();
    private readonly string path;
    private StreamWriter writer;
    private bool disposed;

    public CompanionFileLoggerProvider(string? path = null)
    {
        var directory = path is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BAP Companion", "logs")
            : Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        this.path = path ?? Path.Combine(directory, "server.log");
        if (File.Exists(this.path) && new FileInfo(this.path).Length > MaximumLogBytes)
            File.Move(this.path, Path.Combine(directory, "server.previous.log"), overwrite: true);
        writer = OpenWriter();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            writer.Dispose();
        }
    }

    private void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        lock (gate)
        {
            if (disposed) return;
            try
            {
                writer.WriteLine($"{DateTimeOffset.Now:O} [{level}] [{category}] [{eventId.Id}] {message}");
                if (exception is not null) writer.WriteLine(exception);
                if (writer.BaseStream.Length <= MaximumLogBytes) return;
                writer.Dispose();
                File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, "server.previous.log"), overwrite: true);
                writer = OpenWriter();
            }
            catch (IOException) { Disable(); }
            catch (UnauthorizedAccessException) { Disable(); }
        }
    }

    private void Disable()
    {
        writer.Dispose();
        disposed = true;
    }

    private StreamWriter OpenWriter() => new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
    {
        AutoFlush = true
    };

    private sealed class FileLogger(CompanionFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write(category, logLevel, eventId, formatter(state, exception), exception);
        }
    }
}
