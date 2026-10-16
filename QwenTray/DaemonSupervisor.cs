using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QwenTray.Services;

namespace QwenTray;

public sealed class DaemonSupervisor : IAsyncDisposable
{
    // How many consecutive failed health polls end the daemon's claim to be working. systemd restarts a
    // process that exits, so a sustained failure with the process still alive means it is wedged, which
    // is the one case systemd cannot notice.
    private const int HealthMissesBeforeCrash = 3;

    private readonly Settings _settings;
    private readonly IWslDistro _wsl;
    private readonly SystemdUserService _unit;
    private readonly ILogger<DaemonSupervisor> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly DaemonStateMachine _machine = new();
    private readonly SemaphoreSlim _localControlGate = new(1, 1);
    private int _healthMisses;
    private bool _intentionalExit;
    private bool _disposed;
    private string? _logPath;
    private int? _daemonProcessId;
    private string? _localControlPairingUrl;
    private bool _localControlRedactionLogged;
    private bool _localControlEnableAttempted;
    private Task? _restartTask;
    private CancellationTokenSource _shutdown = new();
    private DaemonState _lastPublished = DaemonState.Stopped;
    private readonly object _holdGate = new();
    private int _holdCount;
    private string? _holdReason;

    public DaemonSupervisor(Settings settings, IWslDistro wsl, SystemdUserService unit, ILogger<DaemonSupervisor>? logger = null)
    {
        _settings = settings;
        _wsl = wsl;
        _unit = unit;
        _logger = logger ?? NullLogger<DaemonSupervisor>.Instance;
    }

    public DaemonState State => _machine.State;
    public string? LogPath => _logPath;

    /// <summary>
    /// False while the daemon answering the port is not the unit this profile owns, which is what a stray
    /// process left behind by an older build looks like from here. A daemon in that state is serving but
    /// not persistent, and Stop cannot reach it.
    /// </summary>
    public bool IsDaemonManaged { get; private set; } = true;

    /// <summary>
    /// The last failure the operator has to act on, or null once a command gets somewhere. A distro
    /// whose Linux side is wedged is what this exists for. Without it a Start that never reaches the
    /// distro looks like it did nothing at all, and the reason is only ever in the log file.
    /// </summary>
    public string? LastProblem { get; private set; }

    public string? LocalControlPairingUrl => _localControlPairingUrl;
    public event EventHandler? LocalControlPairingUrlChanged;

    /// <summary>
    /// Where the daemon writes its log, as a path Windows can open. The systemd unit owns the daemon's
    /// output, so the tray never sees a startup banner and the conventional location is asked of the
    /// distro instead.
    /// </summary>
    public async Task<string?> ResolveLogPathAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_logPath))
        {
            return _logPath;
        }

        // Asking the distro costs a wsl.exe call, which boots it. A hold exists to keep it down, and a
        // log path is not worth breaking that for.
        if (IsHeld)
        {
            return null;
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

            // Cached, because the daemon prints no banner the tray can read and the answer cannot change
            // while the profile is the same. Without this every refresh of the Daemon page costs a call.
            var path = ToUncPath(_settings.WslDistroName, WslDistro.ParseAbsolutePath(result.Output) ?? string.Empty);
            if (path.Length > 0) _logPath = path;
            return path;
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

    /// <summary>
    /// The daemon's process id inside the distro, as systemd reported it the last time the daemon was
    /// seen healthy. The tray no longer owns the process, so this is a reading of the unit, not a handle.
    /// </summary>
    public int? DaemonProcessId => _daemonProcessId;

    public event EventHandler<DaemonState>? StateChanged;

    /// <summary>Raised when a hold is taken or the last one is released, so callers can re-check availability.</summary>
    public event EventHandler? HeldChanged;

    /// <summary>True while something works on the distro from outside it.</summary>
    public bool IsHeld
    {
        get { lock (_holdGate) return _holdCount > 0; }
    }

    private string? HoldReason
    {
        get { lock (_holdGate) return _holdReason; }
    }

    /// <summary>
    /// Stops the daemon and keeps it stopped until the returned scope is disposed. A caller that works
    /// on the distro itself, rather than through the daemon, needs its filesystem still: without a hold
    /// the stop looks like a crash to <see cref="PollAsync"/>, and the restart it schedules boots the
    /// distro again in the middle of the operation.
    /// </summary>
    public async Task<IAsyncDisposable> HoldAsync(string reason)
    {
        lock (_holdGate)
        {
            _holdCount++;
            _holdReason ??= reason;
        }

        RaiseHeldChanged();
        try
        {
            await StopAsync();

            // Stopping the unit is not enough here. An export or a delete needs the whole distro down,
            // and lingering means the user manager would bring the daemon back the moment it boots again.
            using var termination = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _wsl.TerminateAsync(_settings.WslDistroName, termination.Token);
        }
        catch
        {
            ReleaseHold();
            throw;
        }

        _logger.LogInformation("The distro is held for {Reason}.", reason);
        return new Hold(this);
    }

    private void ReleaseHold()
    {
        lock (_holdGate)
        {
            _holdCount--;
            if (_holdCount <= 0)
            {
                _holdCount = 0;
                _holdReason = null;
            }
        }

        RaiseHeldChanged();
    }

    private void RaiseHeldChanged()
    {
        if (!_disposed) HeldChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Hold(DaemonSupervisor owner) : IAsyncDisposable
    {
        private bool _released;

        public ValueTask DisposeAsync()
        {
            if (_released) return ValueTask.CompletedTask;
            _released = true;
            owner.ReleaseHold();
            return ValueTask.CompletedTask;
        }
    }

    public async Task StartAsync()
    {
        if (_disposed)
        {
            _logger.LogDebug("Start requested after disposal. Ignoring.");
            return;
        }
        if (IsHeld)
        {
            _logger.LogInformation("Start requested while the distro is held for {Reason}; ignoring.", HoldReason);
            return;
        }
        if (State is DaemonState.Starting or DaemonState.Running or DaemonState.Stopping)
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
        LastProblem = null;
        var profile = _settings.ResolveActiveProfile();

        // The distro has to exist before anything can be asked of it, and this is the one question that
        // does not boot it, so it comes first. Everything after here starts the instance.
        if (!await _wsl.DistroExistsAsync(_settings.WslDistroName, _shutdown.Token))
        {
            _logger.LogInformation("Start requested with no \"{Distro}\" distro to run the daemon in.", _settings.WslDistroName);
            return;
        }

        // Only one profile's daemon can hold the port, so every other profile's unit is stood aside before
        // anything is decided. Reading the state or the port first would mistake the previous profile still
        // serving for this profile already running, which would leave both units enabled and competing at
        // the next boot and would silently leave the switch unfinished. This also runs in Stop, since a
        // stopped tray has to mean no profile is serving.
        await _unit.StandAsideOtherProfilesAsync(_settings.WslDistroName, profile, _settings.Profiles, _shutdown.Token);

        // Then systemd, before the port. Something answering the health port is no proof that the process
        // behind it is this profile's unit, and now that the daemon outlives the tray this is the ordinary
        // first question rather than the unusual one.
        var state = await _unit.ReadStateAsync(_settings.WslDistroName, profile, _shutdown.Token);
        if (state.Unreachable)
        {
            // Windows keeps calling the distro Running while its Linux side answers nothing at all, and
            // every call after this one would stall the same way. Naming it here is what keeps a Start
            // from looking like it did nothing while the tray sits with its buttons disabled.
            ReportProblem($"The \"{_settings.WslDistroName}\" Linux environment did not answer. It may be wedged, which restarting Windows Subsystem for Linux clears.");
            _machine.UnexpectedExit();
            PublishState();
            return;
        }

        var healthy = await IsHealthyAsync();

        if (state.Active && healthy)
        {
            // Our unit is up and serving, which is the whole point of the handoff: a QwenTray that
            // restarts finds the daemon it started last time still there and leaves it alone.
            IsDaemonManaged = true;
            _healthMisses = 0;
            _machine.Adopt();
            _logger.LogInformation("The daemon unit for {User} is already serving; not starting another.", profile.LinuxUser);
            PublishState();
            _ = RefreshDaemonProcessIdAsync(profile, _shutdown.Token);
            return;
        }

        if (healthy)
        {
            // Something holds the port and it is not this profile's unit. Adopting it quietly would report
            // a daemon the tray cannot stop, because Stop only reaches the unit, and one that dies with the
            // distro instead of surviving it. So keep it serving, take the next start over, and say which
            // is which. Enabling without restarting is deliberate: a second daemon started now would fail
            // to bind and could take the working one down with it.
            IsDaemonManaged = false;
            _healthMisses = 0;
            _machine.Adopt();
            PublishState();
            _ = RefreshDaemonProcessIdAsync(profile, _shutdown.Token);

            await _unit.EnsureLingerAsync(_settings.WslDistroName, profile, _shutdown.Token);
            await _unit.ConfigureAsync(_settings, _settings.WslDistroName, profile, _shutdown.Token);
            _logger.LogWarning("Port {Port} is answered by a process outside the \"{Unit}\" unit. It is running but not managed, so it will not survive a distro restart and Stop will not stop it. The unit is configured and takes over on the next start.", _settings.Port, SystemdUserService.UnitName);
            return;
        }

        // Nothing is serving. This covers the unit never having been started and the unit systemd calls
        // active while the daemon behind it has stopped answering, and both are fixed the same way: apply
        // the settings and restart. Leaving the second case alone would strand it, since every retry would
        // find the unit active and return without ever replacing the process.
        // Everything above this line waited, so a hold taken while this start was in flight has to be
        // re-checked before the distro is booted any further.
        if (IsHeld)
        {
            _logger.LogInformation("A hold for {Reason} was taken while this start was checking the unit; the distro is not booted.", HoldReason);
            return;
        }

        IsDaemonManaged = true;
        _machine.BeginStart();
        PublishState();
        _logPath = null;
        _daemonProcessId = null;
        var serveArguments = BuildServeArguments(_settings, profile);
        foreach (var warning in ValidateSettings(_settings)) _logger.LogWarning("Daemon settings warning: {Warning}", warning);
        SetLocalControlPairingUrl(null);
        _localControlEnableAttempted = false;

        // Writing the unit is what applies the settings, so it happens on every start rather than only
        // the first one. Lingering is the root half of the same promise: without it the enabled unit does
        // not start on a boot that nobody has logged into.
        try
        {
            await _unit.EnsureLingerAsync(_settings.WslDistroName, profile, _shutdown.Token);
            var configured = await _unit.ConfigureAsync(_settings, _settings.WslDistroName, profile, _shutdown.Token);
            var started = configured && await _unit.RestartAsync(_settings.WslDistroName, profile, _shutdown.Token);
            if (!started)
            {
                _logger.LogError("Could not hand the daemon to systemd in {Distro}.", _settings.WslDistroName);
                _machine.UnexpectedExit();
                PublishState();
                return;
            }

            _logger.LogInformation("Handed the daemon to systemd in distro {Distro} as {User}. ArgCount={ArgCount}", _settings.WslDistroName, profile.LinuxUser, serveArguments.Count);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to hand the daemon to systemd in {Distro}.", _settings.WslDistroName);
            _machine.UnexpectedExit();
            PublishState();
            throw;
        }
    }

    /// <summary>
    /// Stops the unit and keeps it stopped through the next distro boot. Disabling is part of stopping: a
    /// unit that is merely stopped would come back on its own the next time anything boots the distro,
    /// which would undo a hold the moment the distro was touched again.
    /// </summary>
    public async Task StopAsync()
    {
        _logger.LogInformation("Stop requested; state {State}.", State);
        _intentionalExit = true;
        _healthMisses = 0;
        LastProblem = null;

        // The stopping state goes up before the shutdown token is cancelled. A poll that lands in
        // between then sees a cancelled health probe, and the state it reads has to say the daemon is
        // leaving rather than that a start never finished.
        _machine.BeginStop();
        PublishState();
        _shutdown.Cancel();
        _daemonProcessId = null;
        IsDaemonManaged = true;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            // Stopping the active profile's unit is not enough to mean stopped. Whichever profile the user
            // switched away from is still enabled and serving, so it is stood aside on the same terms as start.
            await _unit.StandAsideOtherProfilesAsync(_settings.WslDistroName, _settings.ResolveActiveProfile(), _settings.Profiles, timeout.Token);
            await _unit.StopAsync(_settings.WslDistroName, _settings.ResolveActiveProfile(), timeout.Token);
        }
        finally
        {
            // Every way out of the stop settles the state, or a throw leaves it at Stopping with every
            // command disabled and no poll that would clear it.
            SetLocalControlPairingUrl(null);
            _machine.UserStopped();
            PublishState();
        }
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

        // A Stopped supervisor owns no daemon, and the tray having stopped the unit means a stray
        // process answering the health port must not pull it back into Running here. A stop in flight is
        // skipped for the same reason: it cancelled the shutdown token this probe runs under, so a
        // missing answer says nothing about the daemon.
        if (State is DaemonState.Stopped or DaemonState.Stopping) return;

        var healthy = await IsHealthyAsync();
        if (healthy)
        {
            var wasRunning = State == DaemonState.Running;
            _healthMisses = 0;
            _machine.HealthSucceeded();
            PublishState();
            // systemd answers for the process, so the id is read once when the daemon becomes healthy
            // rather than on every poll.
            if (!wasRunning) await RefreshDaemonProcessIdAsync(_settings.ResolveActiveProfile(), _shutdown.Token);
            await RefreshLocalControlPairingAsync(_shutdown.Token);
        }
        else if (State is DaemonState.Starting or DaemonState.Running)
        {
            _healthMisses++;
            if (_healthMisses >= HealthMissesBeforeCrash)
            {
                _healthMisses = 0;
                _logger.LogError("The daemon at {HealthUri} stopped responding.", _settings.HealthUri);
                _daemonProcessId = null;
                HandleUnexpectedExit();
                return;
            }

            if (State == DaemonState.Running) _logger.LogWarning("Health check FAILED at {HealthUri}; daemon stopped responding.", _settings.HealthUri);
            _machine.HealthFailed();
            PublishState();
        }
    }

    private async Task RefreshDaemonProcessIdAsync(IdentityProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            _daemonProcessId = await _unit.ReadMainPidAsync(_settings.WslDistroName, profile, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogDebug(exception, "Could not read the daemon's process id from systemd.");
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

    /// <summary>
    /// The qwen launcher and everything passed to it. This is the command the distro runs, both for the
    /// systemd unit's launcher script and for anything else that has to reproduce the daemon's arguments.
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
        // The bearer token travels in the unit's environment file, never in these arguments, so it stays
        // off every command line and out of the unit and launcher text.
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
        if (_disposed || _intentionalExit) return;
        if (IsHeld)
        {
            _logger.LogInformation("The daemon host exited while the distro is held for {Reason}; no restart scheduled.", HoldReason);
            return;
        }
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
        // Nothing should react once teardown has begun, and a subscriber posting to the UI thread would
        // target a dispatcher that is no longer running.
        if (!_disposed) LocalControlPairingUrlChanged?.Invoke(this, EventArgs.Empty);
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
        if (!_disposed) StateChanged?.Invoke(this, State);
    }

    /// <summary>
    /// Records a failure the operator has to act on and puts it in the log too. The page shows it once
    /// the command returns, and the log keeps it for a command from the tray menu, where no page is open.
    /// </summary>
    private void ReportProblem(string problem)
    {
        LastProblem = problem;
        _logger.LogError("{Problem}", problem);
    }

    /// <summary>
    /// Stops the tray's own work without touching the daemon. This is what makes the daemon outlive
    /// QwenTray: the unit belongs to systemd, so exiting, a tray crash, and an update handoff that kills
    /// the process outright all leave it serving. Stopping the daemon is only ever an explicit Stop.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        // The cancel unwinds the backoff delay. Awaiting it keeps a pending restart from handing a new
        // daemon to systemd while the app is exiting.
        try
        {
            if (_restartTask is not null) await _restartTask;
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
        SetLocalControlPairingUrl(null);
        _shutdown.Dispose();
        _http.Dispose();
        _localControlGate.Dispose();
    }
}
