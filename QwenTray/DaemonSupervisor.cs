using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bitbound.SystemAbstractions.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QwenTray.Services;

namespace QwenTray;

public sealed class DaemonSupervisor : IAsyncDisposable
{
    // Health polls an adopted daemon may miss before it is treated as dead and restarted.
    private const int AdoptedMissesBeforeCrash = 3;

    private static readonly Regex LogPathRegex = new(@"daemon log\s*[→>\-]*\s*(.+?\.log)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly Settings _settings;
    private readonly IWslDistro _wsl;
    private readonly ILogger<DaemonSupervisor> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly DaemonStateMachine _machine = new();
    private readonly SemaphoreSlim _localControlGate = new(1, 1);
    private IProcess? _process;
    private bool _adopted;
    private int _adoptedMisses;
    private bool _intentionalExit;
    private bool _disposed;
    private string? _logPath;
    private string? _localControlPairingUrl;
    private bool _localControlRedactionLogged;
    private bool _localControlEnableAttempted;
    private Task? _restartTask;
    private CancellationTokenSource _shutdown = new();
    private DaemonState _lastPublished = DaemonState.Stopped;

    public DaemonSupervisor(Settings settings, IWslDistro wsl, ILogger<DaemonSupervisor>? logger = null)
    {
        _settings = settings;
        _wsl = wsl;
        _logger = logger ?? NullLogger<DaemonSupervisor>.Instance;
    }

    public DaemonState State => _machine.State;
    public string? LogPath => _logPath;
    public string? LocalControlPairingUrl => _localControlPairingUrl;
    public event EventHandler? LocalControlPairingUrlChanged;

    /// <summary>
    /// Where the daemon writes its log, as a path Windows can open. The startup banner is the
    /// authoritative answer. A daemon that was already running when the tray started has no banner, so
    /// its conventional location is asked of the distro instead.
    /// </summary>
    public async Task<string?> ResolveLogPathAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_logPath))
        {
            return _logPath;
        }

        var profile = _settings.ResolveActiveProfile();
        if (!IdentityProfile.IsValidLinuxUser(profile.LinuxUser))
        {
            return null;
        }

        try
        {
            var result = await _wsl.RunScriptAsync(_settings.WslDistroName, profile.LinuxUser, ConventionalLogPathScript, cancellationToken);
            if (!result.Succeeded)
            {
                return null;
            }

            return ToUncPath(_settings.WslDistroName, WslDistro.ParseAbsolutePath(result.Output) ?? string.Empty);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug(exception, "Could not ask the distro for the conventional daemon log path.");
            return null;
        }
    }

    internal const string ConventionalLogPathScript =
        "test -f ~/.qwen/debug/daemon/daemon.log && printf %s \"$HOME/.qwen/debug/daemon/daemon.log\"";

    public async Task RefreshLocalControlPairingAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.LocalControl || !IsLoopbackHost(_settings.Host) || !_settings.ServeWeb || State is not (DaemonState.Starting or DaemonState.Running))
        {
            _localControlEnableAttempted = false;
            SetLocalControlPairingUrl(null);
            return;
        }

        if (!await _localControlGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            using var statusRequest = CreateLocalControlRequest(HttpMethod.Get, "/workspace/local-control");
            using var statusResponse = await _http.SendAsync(statusRequest, cancellationToken);
            if (!statusResponse.IsSuccessStatusCode)
            {
                SetLocalControlPairingUrl(null);
                return;
            }

            using var statusJson = await JsonDocument.ParseAsync(await statusResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var status = statusJson.RootElement;
            if (!status.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.True)
            {
                if (_localControlEnableAttempted) return;
                _localControlEnableAttempted = true;
                using var enableRequest = CreateLocalControlRequest(HttpMethod.Post, "/workspace/local-control/enable");
                using var enableResponse = await _http.SendAsync(enableRequest, cancellationToken);
                if (!enableResponse.IsSuccessStatusCode)
                {
                    _localControlEnableAttempted = false;
                    SetLocalControlPairingUrl(null);
                    return;
                }

                using var enableJson = await JsonDocument.ParseAsync(await enableResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                UpdateLocalControlPairingUrl(enableJson.RootElement);
                return;
            }

            UpdateLocalControlPairingUrl(status);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            _localControlEnableAttempted = false;
            SetLocalControlPairingUrl(null);
            _logger.LogDebug(exception, "Could not retrieve the device pairing link from Qwen Code.");
        }
        finally
        {
            _localControlGate.Release();
        }
    }

    /// <summary>The supervised wsl.exe host process id, or nothing once it has exited.</summary>
    public int? DaemonProcessId
    {
        get
        {
            try { return _process is { HasExited: false } process ? process.Id : null; }
            catch (InvalidOperationException) { return null; }
        }
    }

    public event EventHandler<DaemonState>? StateChanged;

    public async Task StartAsync()
    {
        if (_disposed)
        {
            _logger.LogDebug("Start requested after disposal. Ignoring.");
            return;
        }
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

        // A daemon already answering is adopted, which covers a distro that outlived a QwenTray restart.
        if (await IsHealthyAsync())
        {
            _machine.Adopt();
            _adopted = true;
            _adoptedMisses = 0;
            _logger.LogInformation("Adopting the daemon already answering on port {Port}.", _settings.Port);
            PublishState();
            return;
        }

        _adopted = false;
        _machine.BeginStart();
        PublishState();
        _logPath = null;
        var profile = _settings.ResolveActiveProfile();
        var arguments = BuildArguments(_settings, _settings.WslDistroName, profile);
        foreach (var warning in ValidateSettings(_settings)) _logger.LogWarning("Daemon settings warning: {Warning}", warning);
        SetLocalControlPairingUrl(null);
        _localControlEnableAttempted = false;

        try
        {
            var process = _process = _wsl.StartDaemon(_settings, _settings.WslDistroName, profile);
            _ = PumpOutputAsync(process, _shutdown.Token);
            _logger.LogInformation("Spawned the daemon in distro {Distro} as {User}. Pid={Pid} ArgCount={ArgCount}", _settings.WslDistroName, profile.LinuxUser, DaemonProcessId, arguments.Count);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to spawn the daemon in {Distro}.", _settings.WslDistroName);
            _machine.UnexpectedExit();
            PublishState();
            throw;
        }
    }

    public async Task StopAsync()
    {
        _logger.LogInformation("Stop requested; state {State}.", State);
        _intentionalExit = true;
        _adopted = false;
        _shutdown.Cancel();

        var process = _process;
        _process = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
        }

        // Killing wsl.exe only ends the client. Terminating the distro is what guarantees the daemon
        // is gone and that the port is free for the next profile.
        using var termination = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _wsl.TerminateAsync(_settings.WslDistroName, termination.Token);

        SetLocalControlPairingUrl(null);
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
        if (_process is { HasExited: true } process)
        {
            int? exitCode = null;
            try { exitCode = process.ExitCode; }
            catch { }
            _process = null;
            if (_intentionalExit || exitCode != 0) SetLocalControlPairingUrl(null);
            if (_intentionalExit)
            {
                _logger.LogInformation("Daemon host exited with code {ExitCode} (intentional stop).", exitCode);
                _machine.UserStopped();
                PublishState();
            }
            else
            {
                _logger.LogError("Daemon host exited unexpectedly with code {ExitCode}.", exitCode);
                HandleUnexpectedExit();
            }
            return;
        }

        // A Stopped supervisor owns no daemon. Adoption happens only in StartAsync, so a stray process
        // answering the health port must not pull a stopped tray into Running here.
        if (State == DaemonState.Stopped) return;

        var healthy = await IsHealthyAsync();
        if (healthy)
        {
            _adoptedMisses = 0;
            _machine.HealthSucceeded();
            PublishState();
            await RefreshLocalControlPairingAsync(_shutdown.Token);
        }
        else if (State is DaemonState.Starting or DaemonState.Running)
        {
            // An adopted daemon has no child process to watch, so sustained failed health is the only
            // signal that it died. Without this the state would sit at Starting and auto-restart
            // would never fire.
            if (_adopted)
            {
                _adoptedMisses++;
                if (_adoptedMisses >= AdoptedMissesBeforeCrash)
                {
                    _adopted = false;
                    _adoptedMisses = 0;
                    _logger.LogError("The adopted daemon at {HealthUri} stopped responding.", _settings.HealthUri);
                    HandleUnexpectedExit();
                    return;
                }
            }
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
            if (!cancellationToken.IsCancellationRequested) await StartAsync();
        }
        catch (OperationCanceledException) { }
    }

    // The process abstraction surfaces stdout only, so the daemon's stderr tail that used to be
    // logged on a crash is gone. Reading stdout keeps the log path capture working.
    private async Task PumpOutputAsync(IProcess process, CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length == 0) continue;
                TryCaptureLogPath(line);
                if (!_settings.LocalControl) _logger.LogDebug("daemon stdout: {Line}", line);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    internal static List<string> BuildArguments(Settings settings, string distro, IdentityProfile profile)
    {
        var args = new List<string>
        {
            "-d", distro,
            "-u", profile.LinuxUser,
            "-e",
        };
        args.AddRange(BuildServeArguments(settings, profile));
        return args;
    }

    /// <summary>
    /// The qwen launcher and everything passed to it, with no wsl.exe arguments in front. This is the
    /// list the distro runs, so anything executed inside it is quoted from here.
    /// </summary>
    internal static List<string> BuildServeArguments(Settings settings, IdentityProfile profile)
    {
        var args = new List<string>
        {
            profile.ResolvedQwenExecutable,
            "serve",
            "--hostname", settings.Host,
            "--port", settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        // Without this the daemon registers process.cwd(), which is the profile's home, so the
        // configured workspace directory would never become the workspace.
        args.Add("--workspace");
        args.Add(profile.ResolvedWorkspaceDirectory);
        if (!string.IsNullOrWhiteSpace(settings.Token)) { args.Add("--token"); args.Add(settings.Token.Trim()); }
        if (settings.RequireAuth) args.Add("--require-auth");
        if (settings.EnableSessionShell) args.Add("--enable-session-shell");
        if (!settings.ServeWeb) args.Add("--no-web");
        if (settings.LocalControl) args.Add("--local-control");
        foreach (var origin in settings.AllowedOrigins.Where(origin => !string.IsNullOrWhiteSpace(origin)))
        {
            args.Add("--allow-origin");
            args.Add(origin.Trim());
        }
        if (!string.IsNullOrWhiteSpace(settings.TlsCertPath)) { args.Add("--tls-cert"); args.Add(settings.TlsCertPath.Trim()); }
        if (!string.IsNullOrWhiteSpace(settings.TlsKeyPath)) { args.Add("--tls-key"); args.Add(settings.TlsKeyPath.Trim()); }
        if (settings.MaxSessions != 32) { args.Add("--max-sessions"); args.Add(settings.MaxSessions.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        if (settings.MaxConnections != 256) { args.Add("--max-connections"); args.Add(settings.MaxConnections.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        if (settings.EventRingSize != 8000) { args.Add("--event-ring-size"); args.Add(settings.EventRingSize.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        if (settings.RateLimit) args.Add("--rate-limit");
        if (settings.DebugMode) args.Add("--debug");
        args.AddRange(settings.ExtraArgs.Where(extra => !string.IsNullOrWhiteSpace(extra)));
        return args;
    }

    private static bool IsLoopbackHost(string host) => string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<string> ValidateSettings(Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Host)) yield return "Hostname is empty; the daemon refuses to start.";
        if (string.IsNullOrWhiteSpace(settings.WslDistroName)) yield return "The WSL distro name is empty; the daemon has no host.";
        if (string.IsNullOrWhiteSpace(settings.ResolveActiveProfile().LinuxUser)) yield return "The active profile has no Linux user.";
        if (!settings.ResolveActiveProfile().IsShellSafe) yield return "The active profile's Linux user or directory holds characters that are not allowed.";
        if (settings.LocalControl && !IsLoopbackHost(settings.Host)) yield return "Device pairing requires the main daemon address to be 127.0.0.1.";
        if (settings.LocalControl && !settings.ServeWeb) yield return "Device pairing requires the Web Shell to be enabled.";
        if (settings.LocalControl && settings.ExtraArgs.Any(argument => argument.StartsWith("--hostname", StringComparison.Ordinal))) yield return "Remove the --hostname override from extra arguments to enable device pairing.";
        if (settings.LocalControl && settings.ExtraArgs.Any(argument => string.Equals(argument, "--no-web", StringComparison.Ordinal))) yield return "Remove --no-web from extra arguments to enable device pairing.";
        if (settings.LocalControl && settings.ExtraArgs.Any(argument => string.Equals(argument, "--local-control", StringComparison.Ordinal))) yield return "Remove --local-control from extra arguments because the setting already enables it.";
        if (settings.RequireAuth && string.IsNullOrWhiteSpace(settings.Token)) yield return "Require auth is set without a token; the daemon only fails fast on a loopback bind.";
        var hasCert = !string.IsNullOrWhiteSpace(settings.TlsCertPath);
        var hasKey = !string.IsNullOrWhiteSpace(settings.TlsKeyPath);
        if (hasCert != hasKey) yield return "--tls-cert and --tls-key must both be set or both empty.";
        if (!settings.RateLimit && settings.ExtraArgs.Any(a => a.StartsWith("--rate-limit-", StringComparison.Ordinal))) yield return "rate-limit tier flags are present without rate limiting enabled.";
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

    private HttpRequestMessage CreateLocalControlRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_settings.LoopbackUri, path));
        if (!string.IsNullOrWhiteSpace(_settings.Token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.Token.Trim());
        }
        return request;
    }

    internal static bool TryReadLocalControlPairingUrl(JsonElement root, out string? url)
    {
        url = null;
        if (!root.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.True) return false;
        if (!root.TryGetProperty("url", out var value) || value.ValueKind != JsonValueKind.String) return false;
        if (!Uri.TryCreate(value.GetString(), UriKind.Absolute, out _)) return false;
        url = value.GetString();
        return true;
    }

    private void UpdateLocalControlPairingUrl(JsonElement status)
    {
        if (TryReadLocalControlPairingUrl(status, out var url))
        {
            SetLocalControlPairingUrl(url);
        }
        else if (status.TryGetProperty("urlRedacted", out var redacted) && redacted.ValueKind == JsonValueKind.True)
        {
            if (!_localControlRedactionLogged)
            {
                _logger.LogWarning("Qwen Code withheld the device pairing link. Check the daemon authentication settings.");
                _localControlRedactionLogged = true;
            }
        }
        else
        {
            SetLocalControlPairingUrl(null);
        }
    }

    private void SetLocalControlPairingUrl(string? url)
    {
        if (string.Equals(_localControlPairingUrl, url, StringComparison.Ordinal)) return;
        _localControlPairingUrl = url;
        if (url is null) _localControlRedactionLogged = false;
        LocalControlPairingUrlChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TryCaptureLogPath(string line)
    {
        if (!TryReadLogPath(line, _settings.WslDistroName, out var path)) return;
        if (string.Equals(path, _logPath, StringComparison.Ordinal)) return;
        _logPath = path;
        _logger.LogInformation("Daemon reported its log file at {LogPath}.", path);
    }

    /// <summary>
    /// Reads the log path out of the banner the daemon prints at startup. The banner is written to
    /// stderr, so whoever spawns the daemon has to fold stderr into the stream being read here.
    /// </summary>
    internal static bool TryReadLogPath(string line, string distro, out string logPath)
    {
        var match = LogPathRegex.Match(line);
        logPath = match.Success ? ToUncPath(distro, match.Groups[1].Value.Trim()) : string.Empty;
        return logPath.Length > 0;
    }

    /// <summary>Turns <c>/home/qwen/.qwen/daemon.log</c> into the distro share Windows can open.</summary>
    internal static string ToUncPath(string distro, string linuxPath)
    {
        if (string.IsNullOrWhiteSpace(linuxPath) || linuxPath[0] != '/') return string.Empty;
        return $@"\\wsl.localhost\{distro}" + linuxPath.Replace('/', '\\');
    }

    private async Task<bool> IsHealthyAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _settings.HealthUri);
            if (_settings.UsesBearerAuth && !string.IsNullOrWhiteSpace(_settings.Token))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.Token.Trim());
            using var response = await _http.SendAsync(request, _shutdown.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }

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
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        // The cancel unwinds the backoff delay. Awaiting it keeps a pending restart from spawning a
        // daemon while the app is exiting.
        try
        {
            if (_restartTask is not null) await _restartTask;
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
        await StopAsync();
        SetLocalControlPairingUrl(null);
        _shutdown.Dispose();
        _http.Dispose();
        _localControlGate.Dispose();
    }
}
