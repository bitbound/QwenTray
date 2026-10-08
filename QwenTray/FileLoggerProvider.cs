using Bitbound.SystemAbstractions.FileSystem;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace QwenTray;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int RetainedFiles = 3;
    private readonly IFileSystem _fileSystem;
    private readonly string _path;
    private readonly object _gate = new();
    private bool _disposed;

    public FileLoggerProvider(IFileSystem fileSystem, string path)
    {
        _fileSystem = fileSystem;
        _path = path;
        _fileSystem.CreateDirectory(Path.GetDirectoryName(path)!);
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
        var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);

        lock (_gate)
        {
            if (_disposed) return;
            RotateIfNeeded(bytes.Length);
            using var stream = _fileSystem.OpenFileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        if (GetLength(_path) + incomingBytes <= MaxFileBytes) return;
        for (var i = RetainedFiles - 1; i >= 1; i--)
        {
            var source = $"{_path}.{i}";
            if (_fileSystem.FileExists(source)) _fileSystem.MoveFile(source, $"{_path}.{i + 1}", overwrite: true);
        }
        _fileSystem.MoveFile(_path, $"{_path}.1", overwrite: true);
    }

    private long GetLength(string path)
    {
        if (!_fileSystem.FileExists(path)) return 0;
        using var stream = _fileSystem.OpenFileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return stream.Length;
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
