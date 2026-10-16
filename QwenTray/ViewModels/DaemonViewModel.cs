using Avalonia.Threading;
using Bitbound.SystemAbstractions.Processes;
using Microsoft.Extensions.Logging;
using QwenTray.Services;
using QwenTray.Views;

namespace QwenTray.ViewModels;

public sealed partial class DaemonViewModel : ViewModelBase<DaemonView>
{
    private readonly Settings _settings;
    private readonly DaemonSupervisor _supervisor;
    private readonly IProcessManager _processes;
    private readonly ISnackbarService _snackbars;
    private readonly ILogger<DaemonViewModel> _logger;

    [ObservableProperty]
    private string _stateText = "Stopped";

    [ObservableProperty]
    private string _stateIconKey = "stop_regular";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isStarting;

    [ObservableProperty]
    private bool _isCrashed;

    [ObservableProperty]
    private bool _isStopped = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _canStart;

    [ObservableProperty]
    private bool _canStop;

    [ObservableProperty]
    private bool _canRestart;

    private bool _daemonCommandBusy;

    private string? _resolvedLogPath;

    [ObservableProperty]
    private string _endpoint = string.Empty;

    [ObservableProperty]
    private string _healthUrl = string.Empty;

    [ObservableProperty]
    private string _webUrl = string.Empty;

    [ObservableProperty]
    private string _processId = "None";

    [ObservableProperty]
    private string _logPath = "Not reported yet.";

    public DaemonViewModel(
        Settings settings,
        DaemonSupervisor supervisor,
        IProcessManager processes,
        ISnackbarService snackbars,
        ILogger<DaemonViewModel> logger)
    {
        _settings = settings;
        _supervisor = supervisor;
        _processes = processes;
        _snackbars = snackbars;
        _logger = logger;

        _supervisor.StateChanged += (_, state) => Dispatcher.UIThread.Post(() => ApplyState(state));
        _supervisor.HeldChanged += (_, _) => Dispatcher.UIThread.Post(() => ApplyState(_supervisor.State));
    }

    protected override Task OnInitializeAsync()
    {
        RefreshAddresses();
        ApplyState(_supervisor.State);

        // A daemon the tray adopted never printed its banner here, so ask for the log path once.
        _ = ResolveLogPathAsync();

        return Task.CompletedTask;
    }

    private async Task ResolveLogPathAsync()
    {
        try
        {
            _resolvedLogPath = await _supervisor.ResolveLogPathAsync(CancellationToken.None);
            if (_supervisor.LogPath is null)
            {
                LogPath = _resolvedLogPath ?? "Not reported yet.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve the daemon log path.");
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        _logger.LogInformation("Start requested from the daemon page.");
        await RunAsync(() => _supervisor.StartAsync());
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        _logger.LogInformation("Stop requested from the daemon page.");
        await RunAsync(() => _supervisor.StopAsync());
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        _logger.LogInformation("Restart requested from the daemon page.");
        await RunAsync(() => _supervisor.RestartAsync());
    }

    [RelayCommand]
    private void OpenWebShell()
    {
        _processes.LaunchUri(_settings.WebShellUri);
    }

    [RelayCommand]
    private async Task OpenLogAsync()
    {
        var path = await _supervisor.ResolveLogPathAsync(CancellationToken.None);
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogInformation("No daemon log is known yet.");
            _snackbars.Show("No daemon log is known yet. Start the daemon and try again.", SnackbarKind.Warning);
            return;
        }

        try
        {
            _processes.Start(path, string.Empty, useShellExec: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening the daemon log at {LogPath} failed.", path);
            _snackbars.Show($"Could not open the daemon log: {ex.Message}", SnackbarKind.Error);
        }
    }

    [RelayCommand]
    private void OpenTrayFolder()
    {
        try
        {
            // The tray's own folder on the Windows host: qwentray.log and settings.json.
            _processes.Start("explorer.exe", $"\"{SettingsStore.DirectoryPath}\"", useShellExec: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening the QwenTray folder failed.");
            _snackbars.Show($"Could not open the QwenTray folder: {ex.Message}", SnackbarKind.Error);
        }
    }

    [RelayCommand]
    private void OpenInWindowsTerminal()
    {
        var distro = _settings.WslDistroName;
        var profile = _settings.ResolveActiveProfile();
        if (!Settings.IsSafeDistroName(distro) || !IdentityProfile.IsValidLinuxUser(profile.LinuxUser))
        {
            _snackbars.Show("Fix the distro name or the profile's Linux user before opening a terminal.", SnackbarKind.Warning);
            return;
        }

        try
        {
            _processes.Start("wt.exe", $"wsl.exe -d {distro} -u {profile.LinuxUser}", useShellExec: true);
        }
        catch (Exception ex)
        {
            // Windows Terminal is not installed everywhere, so fall back to a plain console.
            _logger.LogDebug(ex, "Windows Terminal could not be started; falling back to wsl.exe.");
            _processes.Start("wsl.exe", $"-d {distro} -u {profile.LinuxUser}", useShellExec: true);
        }
    }

    // A root shell for the operator to manage the box itself, distinct from the profile's shell, which
    // is the daemon's unprivileged identity.
    [RelayCommand]
    private void OpenAdminShell()
    {
        var distro = _settings.WslDistroName;
        if (!Settings.IsSafeDistroName(distro))
        {
            _snackbars.Show("Fix the distro name before opening an admin shell.", SnackbarKind.Warning);
            return;
        }

        try
        {
            _processes.Start("wt.exe", $"wsl.exe -d {distro} -u root", useShellExec: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Windows Terminal could not be started; falling back to wsl.exe.");
            _processes.Start("wsl.exe", $"-d {distro} -u root", useShellExec: true);
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        _daemonCommandBusy = true;
        UpdateCommandAvailability(_supervisor.State);

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Daemon command failed.");
        }
        finally
        {
            IsBusy = false;
            _daemonCommandBusy = false;
            UpdateCommandAvailability(_supervisor.State);
            RefreshAddresses();
            ApplyState(_supervisor.State);

            // A failure that leaves the state chip reading Stopped or Crashed is exactly the case the
            // page cannot explain on its own, so the reason the command did not get anywhere is shown.
            if (_supervisor.LastProblem is { } problem) _snackbars.Show(problem, SnackbarKind.Error);
        }
    }

    private void RefreshAddresses()
    {
        Endpoint = $"{_settings.Host}:{_settings.Port}";
        HealthUrl = _settings.HealthUri.ToString();
        WebUrl = _settings.WebUri.ToString();
    }

    private void UpdateCommandAvailability(DaemonState state)
    {
        var availability = DaemonCommandAvailability.From(state, _daemonCommandBusy || _supervisor.IsHeld);
        CanStart = availability.CanStart;
        CanStop = availability.CanStop;
        CanRestart = availability.CanRestart;
    }

    private void ApplyState(DaemonState state)
    {
        IsRunning = state == DaemonState.Running;
        IsStarting = state == DaemonState.Starting;
        IsCrashed = state == DaemonState.Crashed;
        IsStopped = state == DaemonState.Stopped;
        UpdateCommandAvailability(state);

        StateText = state switch
        {
            DaemonState.Running => "Running",
            DaemonState.Starting => "Starting",
            DaemonState.Stopping => "Stopping",
            DaemonState.Crashed => "Crashed",
            _ => "Stopped",
        };

        StateIconKey = state switch
        {
            DaemonState.Running => "play_regular",
            DaemonState.Starting => "arrow_sync_regular",
            DaemonState.Crashed => "error_circle_regular",
            _ => "stop_regular",
        };

        ProcessId = _supervisor.DaemonProcessId?.ToString() ?? "None";
        LogPath = _supervisor.LogPath ?? _resolvedLogPath ?? "Not reported yet.";
    }
}
