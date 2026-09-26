using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace QwenTray;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int RetainedFiles = 3;
    private readonly string _path;
    private readonly object _gate = new();
    private bool _disposed;

    public FileLoggerProvider(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate) _disposed = true;
    }

    private void Write<TState>(string category, LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        var properties = state is IEnumerable<KeyValuePair<string, object?>> values
            ? values.Where(pair => pair.Key != "{OriginalFormat}").ToDictionary(pair => pair.Key, pair => pair.Value)
            : null;
        var line = $"{DateTimeOffset.Now:O} [{Abbreviation(level)}] {category} EventId={eventId.Id} {message}";
        if (properties is { Count: > 0 }) line += " " + JsonSerializer.Serialize(properties);
        if (exception is not null) line += Environment.NewLine + exception;

        lock (_gate)
        {
            if (_disposed) return;
            RotateIfNeeded(Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length);
            File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length + incomingBytes <= MaxFileBytes) return;
        for (var i = RetainedFiles - 1; i >= 1; i--)
        {
            var source = $"{_path}.{i}";
            if (File.Exists(source)) File.Move(source, $"{_path}.{i + 1}", overwrite: true);
        }
        File.Move(_path, $"{_path}.1", overwrite: true);
    }

    private static string Abbreviation(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "NON"
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write(category, logLevel, eventId, state, exception, formatter);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        public void Dispose() { }
    }
}
