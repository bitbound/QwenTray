using System.Diagnostics;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using QwenTray.Views;

namespace QwenTray.ViewModels;

public sealed partial class DaemonViewModel : ViewModelBase<DaemonView>
{
    private readonly Settings _settings;
    private readonly DaemonSupervisor _supervisor;
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
    private string _endpoint = string.Empty;

    [ObservableProperty]
    private string _healthUrl = string.Empty;

    [ObservableProperty]
    private string _webUrl = string.Empty;

    [ObservableProperty]
    private string _processId = "None";

    [ObservableProperty]
    private string _logPath = "Not reported yet.";

    public DaemonViewModel(Settings settings, DaemonSupervisor supervisor, ILogger<DaemonViewModel> logger)
    {
        _settings = settings;
        _supervisor = supervisor;
        _logger = logger;

        _supervisor.StateChanged += (_, state) => Dispatcher.UIThread.Post(() => ApplyState(state));
    }

    protected override Task OnInitializeAsync()
    {
        RefreshAddresses();
        ApplyState(_supervisor.State);

        return Task.CompletedTask;
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
        Process.Start(new ProcessStartInfo(_settings.WebUri.ToString()) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenLog()
    {
        var path = _supervisor.LogPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _logger.LogWarning("No daemon log is known yet.");
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;

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
            RefreshAddresses();
            ApplyState(_supervisor.State);
        }
    }

    private void RefreshAddresses()
    {
        Endpoint = $"{_settings.Host}:{_settings.Port}";
        HealthUrl = _settings.HealthUri.ToString();
        WebUrl = _settings.WebUri.ToString();
    }

    private void ApplyState(DaemonState state)
    {
        IsRunning = state == DaemonState.Running;
        IsStarting = state == DaemonState.Starting;
        IsCrashed = state == DaemonState.Crashed;
        IsStopped = state == DaemonState.Stopped;

        StateText = state switch
        {
            DaemonState.Running => "Running",
            DaemonState.Starting => "Starting",
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
        LogPath = _supervisor.LogPath ?? "Not reported yet.";
    }
}