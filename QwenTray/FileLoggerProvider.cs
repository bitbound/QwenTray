using Bitbound.SystemAbstractions.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace QwenTray;

public sealed class FileLoggerProvider(Func<IServiceProvider> servicesFactory, string path) : ILoggerProvider
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int RetainedFiles = 3;
    private readonly Func<IServiceProvider> _servicesFactory = servicesFactory;
    private readonly string _path = path;
    private readonly object _gate = new();
    private IServiceProvider? _services;
    private bool _directoryReady;
    private bool _disposed;

  private IFileSystem ResolveFileSystem()
    {
        lock (_gate)
        {
            _services ??= _servicesFactory();
            return _services.GetRequiredService<IFileSystem>();
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate) _disposed = true;
    }

    private void Write<TState>(string category, LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            WriteCore(category, level, eventId, state, exception, formatter);
        }
        catch (Exception failure)
        {
            // A logging provider must not throw, and a failed write has nowhere else to report.
            Debug.WriteLine($"File logger write failed: {failure.Message}");
        }
    }

    private void WriteCore<TState>(string category, LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        var properties = state is IEnumerable<KeyValuePair<string, object?>> values
            ? values.Where(pair => pair.Key != "{OriginalFormat}").ToDictionary(pair => pair.Key, pair => pair.Value)
            : null;
        var line = $"{DateTimeOffset.Now:O} [{Abbreviation(level)}] {category} EventId={eventId.Id} {message}";
        if (properties is { Count: > 0 }) line += " " + SerializeProperties(properties);
        if (exception is not null) line += Environment.NewLine + exception;
        var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);

        lock (_gate)
        {
            if (_disposed) return;
            var fileSystem = ResolveFileSystem();
            EnsureDirectory(fileSystem);
            RotateIfNeeded(fileSystem, bytes.Length);
            using var stream = fileSystem.OpenFileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    private static string SerializeProperties(Dictionary<string, object?> properties)
    {
        try
        {
            return JsonSerializer.Serialize(properties);
        }
        catch (Exception)
        {
            // A log write must not throw, so fall back when a value has no JSON representation.
            return string.Join(" ", properties.Select(pair => $"{pair.Key}={pair.Value}"));
        }
    }

    private void EnsureDirectory(IFileSystem fileSystem)
    {
        if (_directoryReady) return;
        fileSystem.CreateDirectory(Path.GetDirectoryName(_path)!);
        _directoryReady = true;
    }

    private void RotateIfNeeded(IFileSystem fileSystem, int incomingBytes)
    {
        if (GetLength(fileSystem, _path) + incomingBytes <= MaxFileBytes) return;
        for (var i = RetainedFiles - 1; i >= 1; i--)
        {
            var source = $"{_path}.{i}";
            if (fileSystem.FileExists(source)) fileSystem.MoveFile(source, $"{_path}.{i + 1}", overwrite: true);
        }
        fileSystem.MoveFile(_path, $"{_path}.1", overwrite: true);
    }

    private long GetLength(IFileSystem fileSystem, string path)
    {
        if (!fileSystem.FileExists(path)) return 0;
        using var stream = fileSystem.OpenFileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
