using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using QwenTray.Services;
using QwenTray.Views;

namespace QwenTray.ViewModels;

public interface IMainWindowViewModel : IViewModelBase
{
    ObservableCollection<NavItemViewModel> NavigationItems { get; }

    IViewModelBase? CurrentViewModel { get; set; }
}

public partial class MainWindowViewModel : ViewModelBase<MainWindow>, IMainWindowViewModel
{
    private readonly INavigationProvider _navigation;
    private readonly DaemonSupervisor _supervisor;
    private readonly Settings _settings;
    private readonly ILogger<MainWindowViewModel> _logger;

    [ObservableProperty]
    private IViewModelBase? _currentViewModel;

    [ObservableProperty]
    private string _daemonStatus = "Daemon stopped";

    [ObservableProperty]
    private string _statusIconKey = "stop_regular";

    [ObservableProperty]
    private string _endpoint = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isStarting;

    [ObservableProperty]
    private bool _isCrashed;

    [ObservableProperty]
    private bool _isStopped = true;

    public MainWindowViewModel(
        INavigationProvider navigation,
        DaemonSupervisor supervisor,
        Settings settings,
        ILogger<MainWindowViewModel> logger)
    {
        _navigation = navigation;
        _supervisor = supervisor;
        _settings = settings;
        _logger = logger;

        _supervisor.StateChanged += (_, state) => Dispatcher.UIThread.Post(() => ApplyState(state));
    }

    public ObservableCollection<NavItemViewModel> NavigationItems { get; } = [];

    protected override async Task OnInitializeAsync()
    {
        await base.OnInitializeAsync();

        NavigationItems.Add(new NavItemViewModel("play_regular", "Daemon", _navigation, typeof(DaemonViewModel)));
        NavigationItems.Add(new NavItemViewModel("settings_regular", "Settings", _navigation, typeof(SettingsViewModel)));
        NavigationItems.Add(new NavItemViewModel("info_regular", "About", _navigation, typeof(AboutViewModel)));

        ApplyState(_supervisor.State);
        await _navigation.NavigateTo<DaemonViewModel>();
    }

    [RelayCommand]
    private void OpenWebShell()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_settings.WebShellUri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // The address is logged without its fragment so the bearer token stays out of the log.
            _logger.LogError(ex, "Could not open the web shell at {WebUri}.", _settings.WebUri);
        }
    }

    private void ApplyState(DaemonState state)
    {
        IsRunning = state == DaemonState.Running;
        IsStarting = state == DaemonState.Starting;
        IsCrashed = state == DaemonState.Crashed;
        IsStopped = state == DaemonState.Stopped;

        DaemonStatus = state switch
        {
            DaemonState.Running => "Daemon running",
            DaemonState.Starting => "Daemon starting",
            DaemonState.Crashed => "Daemon crashed",
            _ => "Daemon stopped",
        };

        StatusIconKey = state switch
        {
            DaemonState.Running => "play_regular",
            DaemonState.Starting => "arrow_sync_regular",
            DaemonState.Crashed => "error_circle_regular",
            _ => "stop_regular",
        };

        Endpoint = $"{_settings.Host}:{_settings.Port}";
    }
}