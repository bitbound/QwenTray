using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace QwenTray;

public sealed class DaemonSupervisor : IAsyncDisposable
{
    private const int StderrTailLines = 20;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex LogPathRegex = new(@"daemon log\s*[→>\-]*\s*(.+?\.log)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly Settings _settings;
    private readonly ILogger<DaemonSupervisor> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly DaemonStateMachine _machine = new();
    private readonly List<string> _stderrTail = [];
    private readonly object _tailGate = new();
    private Process? _process;
    private int? _adoptedPid;
    private bool _intentionalExit;
    private bool _disposed;
    private string? _logPath;
    private Task? _restartTask;
    private CancellationTokenSource _shutdown = new();
    private DaemonState _lastPublished = DaemonState.Stopped;

    public DaemonSupervisor(Settings settings, ILogger<DaemonSupervisor>? logger = null)
    {
        _settings = settings;
        _logger = logger ?? NullLogger<DaemonSupervisor>.Instance;
    }

    public DaemonState State => _machine.State;
    public string? LogPath => _logPath;
    public event EventHandler<DaemonState>? StateChanged;

    public async Task StartAsync()
    {
        if (State is DaemonState.Starting or DaemonState.Running)
        {
            _logger.LogDebug("Start requested while already {State}; ignoring.", State);
            return;
        }
        if (_shutdown.IsCancellationRequested)
        {
            _shutdown.Dispose();
            _shutdown = new CancellationTokenSource();
        }
        _intentionalExit = false;
        if (await IsHealthyAsync())
        {
            _adoptedPid = await FindListenerPidAsync(_settings.Port);
            _machine.Adopt();
            _logger.LogInformation("Adopting existing daemon on port {Port} (pid {Pid}).", _settings.Port, _adoptedPid?.ToString() ?? "unknown");
            PublishState();
            return;
        }
        Spawn();
    }

    public async Task StopAsync()
    {
        _logger.LogInformation("Stop requested; state {State}.", State);
        _intentionalExit = true;
        _shutdown.Cancel();
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch (InvalidOperationException) { }
        }
        else if (_adoptedPid is int pid)
        {
            try { Process.GetProcessById(pid).Kill(entireProcessTree: true); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        _process?.Dispose();
        _process = null;
        if (_adoptedPid is int adoptedPid && !IsProcessRunning(adoptedPid)) _adoptedPid = null;
        _machine.UserStopped();
        PublishState();
    }

    public async Task RestartAsync()
    {
        _logger.LogInformation("Restart requested.");
        _intentionalExit = true;
        await StopAsync();
        await StartAsync();
    }

    public async Task PollAsync()
    {
        if (_disposed) return;
        if (_process is { HasExited: true })
        {
            int? exitCode = null;
            try { exitCode = _process.ExitCode; }
            catch { }
            var tail = TakeStderrTail();
            _process.Dispose();
            _process = null;
            if (_intentionalExit)
            {
                _logger.LogInformation("Daemon child exited with code {ExitCode} (intentional stop).", exitCode);
                _machine.UserStopped();
                PublishState();
            }
            else
            {
                _logger.LogError("Daemon child exited unexpectedly with code {ExitCode}.{Stderr}", exitCode, FormatTail(tail));
                HandleUnexpectedExit();
            }
            return;
        }

        if (_adoptedPid is int adoptedPid && !IsProcessRunning(adoptedPid))
        {
            _logger.LogError("Adopted daemon pid {Pid} is no longer running.", adoptedPid);
            _adoptedPid = null;
            HandleUnexpectedExit();
            return;
        }

        var healthy = await IsHealthyAsync();
        if (healthy)
        {
            _machine.HealthSucceeded();
            PublishState();
        }
        else if (State is DaemonState.Starting or DaemonState.Running)
        {
            if (State == DaemonState.Running) _logger.LogWarning("Health check FAILED at {HealthUri}; daemon stopped responding.", _settings.HealthUri);
            _machine.HealthFailed();
            PublishState();
        }
    }

    private async Task RestartAfterDelayAsync(int seconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
            if (!cancellationToken.IsCancellationRequested) Spawn();
        }
        catch (OperationCanceledException) { }
    }

    private void Spawn()
    {
        _machine.BeginStart();
        PublishState();
        var executable = _settings.QwenExecutable;
        var workspace = ResolveWorkspace();
        var arguments = new List<string> { "serve", "--hostname", _settings.Host, "--port", _settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        arguments.AddRange(_settings.ExtraArgs);
        var command = $"\"{executable}\" " + string.Join(" ", arguments.Select(QuoteArgument));
        var start = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /s /c \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8
        };
        if (workspace is not null)
        {
            try
            {
                Directory.CreateDirectory(workspace);
                start.WorkingDirectory = workspace;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not prepare daemon workspace {Workspace}; using the default working directory.", workspace);
            }
        }
        lock (_tailGate) _stderrTail.Clear();

        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process.OutputDataReceived += OnStdout;
        _process.ErrorDataReceived += OnStderr;
        try
        {
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _logger.LogInformation("Spawned daemon via cmd.exe. Pid={Pid} Workspace={Workspace} Command={Command}", _process.Id, start.WorkingDirectory ?? "(default)", start.Arguments);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to spawn daemon. Command={Command}", start.Arguments);
            _machine.UnexpectedExit();
            PublishState();
            throw;
        }
    }

    private void HandleUnexpectedExit()
    {
        if (_intentionalExit || _disposed) return;
        _machine.UnexpectedExit();
        PublishState();
        if (!_settings.AutoRestart)
        {
            _logger.LogWarning("Auto-restart disabled; leaving daemon stopped after crash.");
            return;
        }
        if (_restartTask is null || _restartTask.IsCompleted)
        {
            var delay = _machine.BackoffSeconds(_settings.MaxBackoffSeconds);
            _logger.LogWarning("Scheduling restart #{Failures} in {Delay}s.", _machine.ConsecutiveFailures, delay);
            _restartTask = RestartAfterDelayAsync(delay, _shutdown.Token);
        }
    }

    private void OnStdout(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        TryCaptureLogPath(e.Data);
        _logger.LogDebug("daemon stdout: {Line}", e.Data);
    }

    private void OnStderr(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        TryCaptureLogPath(e.Data);
        lock (_tailGate)
        {
            _stderrTail.Add(e.Data);
            if (_stderrTail.Count > StderrTailLines) _stderrTail.RemoveAt(0);
        }
        _logger.LogDebug("daemon stderr: {Line}", e.Data);
    }

    private void TryCaptureLogPath(string line)
    {
        var match = LogPathRegex.Match(line);
        if (!match.Success) return;
        var path = match.Groups[1].Value.Trim();
        if (path.Length == 0 || string.Equals(path, _logPath, StringComparison.Ordinal)) return;
        _logPath = path;
        _logger.LogInformation("Daemon reported its log file at {LogPath}.", path);
    }

    private string[] TakeStderrTail()
    {
        lock (_tailGate)
        {
            var snapshot = _stderrTail.ToArray();
            _stderrTail.Clear();
            return snapshot;
        }
    }

    private static string FormatTail(string[] tail) => tail.Length == 0 ? string.Empty : " Last stderr output:" + Environment.NewLine + string.Join(Environment.NewLine, tail);

    private async Task<bool> IsHealthyAsync()
    {
        try
        {
            using var response = await _http.GetAsync(_settings.HealthUri, _shutdown.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException) { return false; }
    }

    private static bool IsProcessRunning(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task<int?> FindListenerPidAsync(int port)
    {
        var start = new ProcessStartInfo("netstat.exe", "-ano") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(start);
        if (process is null) return null;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        var portSuffix = ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 5 && fields[0].Equals("TCP", StringComparison.OrdinalIgnoreCase) && fields[1].EndsWith(portSuffix, StringComparison.Ordinal) && fields[^2].Equals("LISTENING", StringComparison.OrdinalIgnoreCase) && int.TryParse(fields[^1], out var pid))
                return pid;
        }
        return null;
    }

    private string? ResolveWorkspace()
    {
        var raw = _settings.WorkspaceDirectory;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(raw).Trim();
        return expanded.Length == 0 ? null : expanded;
    }

    private static string QuoteArgument(string value) => value.Length > 0 && (value.Contains(' ', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)) ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;

    private void PublishState()
    {
        if (State == _lastPublished) return;
        var previous = _lastPublished;
        _lastPublished = State;
        _logger.LogInformation("State {Previous} -> {State} (consecutive failures {Failures}).", previous, State, _machine.ConsecutiveFailures);
        StateChanged?.Invoke(this, State);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _shutdown.Cancel();
        await StopAsync();
        _shutdown.Dispose();
        _http.Dispose();
    }
}
