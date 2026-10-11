using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Bitbound.SystemAbstractions.Processes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QwenTray.Services;
using QwenTray.Updates;
using QwenTray.ViewModels;
using QwenTray.Views;

namespace QwenTray;

public partial class App : Application
{
    public static IServiceProvider? Services { get; set; }

    private DaemonSupervisor _supervisor = null!;
    private Settings _settings = null!;
    private SettingsStore _settingsStore = null!;
    private RegistryAutostartService _autostart = null!;
    private WslDistroProvisioner _provisioner = null!;
    private IProcessManager _processes = null!;
    private INavigationProvider _navigation = null!;
    private IUpdateService _updates = null!;
    private ILogger<App> _logger = null!;
    private TrayIcon? _icon;
    private MainWindow? _mainWindow;
    private IMainWindowViewModel? _mainViewModel;
    private DispatcherTimer? _timer;
    private DispatcherTimer? _updateTimer;
    private bool _daemonCommandBusy;
    private bool _isShellInitialized;
    private bool _isExiting;
    private bool _stopping;
    private string? _availableUpdateVersion;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The daemon is supervised from the tray while the window is closed, so closing every
            // window must not end the process on its own.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += OnExit;
        }

        var services = Services ?? throw new InvalidOperationException("The service container was not initialized before the UI started.");
        _settings = services.GetRequiredService<Settings>();
        _settingsStore = services.GetRequiredService<SettingsStore>();
        _logger = services.GetRequiredService<ILogger<App>>();
        _autostart = services.GetRequiredService<RegistryAutostartService>();
        _supervisor = services.GetRequiredService<DaemonSupervisor>();
        _provisioner = services.GetRequiredService<WslDistroProvisioner>();
        _processes = services.GetRequiredService<IProcessManager>();
        _navigation = services.GetRequiredService<INavigationProvider>();
        _updates = services.GetRequiredService<IUpdateService>();
        _supervisor.StateChanged += OnStateChanged;
        _autostart.Changed += OnAutostartChanged;
        _updates.UpdateAvailable += OnUpdateAvailable;
        _icon = GetValue(TrayIcon.IconsProperty)?.FirstOrDefault();
        UpdateTrayCommandAvailability(_supervisor.State);
        SetAutostartMenuChecked();

        if (services.GetRequiredService<MainWindow>() is { } mainWindow && services.GetRequiredService<IMainWindowViewModel>() is { } mainViewModel)
        {
            _mainWindow = mainWindow;
            _mainViewModel = mainViewModel;
            mainWindow.DataContext = mainViewModel;
            mainWindow.Opened += OnMainWindowOpened;
            mainWindow.Closing += OnMainWindowClosing;

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            {
                lifetime.MainWindow = mainWindow;
            }
        }

        var pollSeconds = Math.Clamp(_settings.HealthPollSeconds, 1, 60);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(pollSeconds) };
        _timer.Tick += async (_, _) => await _supervisor.PollAsync();
        _timer.Start();
        _logger.LogInformation("QwenTray tray ready. Distro={Distro} Profile={Profile} HealthUri={HealthUri} PollSeconds={PollSeconds}", _settings.WslDistroName, _settings.ResolveActiveProfile().Name, _settings.HealthUri, pollSeconds);
        _ = StartDaemonOnStartupAsync();

        if (_settings.CheckForUpdates)
        {
            StartAutomaticUpdateChecks();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Automatic checks run at most once every two days. The last run is persisted, so restarting the
    // app does not check again before the interval is up, and a long-running tray session checks once
    // the timer fires.
    private void StartAutomaticUpdateChecks()
    {
        _ = CheckForUpdatesIfDueAsync();

        _updateTimer = new DispatcherTimer { Interval = UpdateCheckSchedule.Interval };
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesIfDueAsync();
        _updateTimer.Start();
    }

    private async Task CheckForUpdatesIfDueAsync()
    {
        var now = DateTimeOffset.UtcNow;

        if (!UpdateCheckSchedule.IsDue(_settings.LastUpdateCheckUtc, now))
        {
            return;
        }

        // Recorded before the request so an unreachable GitHub is not retried on the next launch.
        _settings.LastUpdateCheckUtc = now;
        _settingsStore.Save(_settings);

        try
        {
            var result = await _updates.CheckAsync();

            if (result.Update is { } update)
            {
                _logger.LogInformation("A newer QwenTray is available: {Version}.", update.Version);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The update check failed.");
        }
    }

    // Launching into a distro that was never set up would spawn a daemon that immediately crashes and
    // back off-restarts, so the tray stays put until the Linux environment exists.
    private async Task StartDaemonOnStartupAsync()
    {
        if (!await _provisioner.IsConfiguredAsync(_settings.WslDistroName, CancellationToken.None))
        {
            _logger.LogInformation("Daemon not started on launch. The \"{Distro}\" Linux environment is not set up.", _settings.WslDistroName);
            return;
        }

        await _supervisor.StartAsync();
    }

    private async void OnStart(object? sender, EventArgs e)
    {
        ResetMenuItem(sender);
        await RunTrayCommandAsync(supervisor => supervisor.StartAsync());
    }

    private async void OnStop(object? sender, EventArgs e)
    {
        ResetMenuItem(sender);
        await RunTrayCommandAsync(supervisor => supervisor.StopAsync());
    }

    private async void OnRestart(object? sender, EventArgs e)
    {
        ResetMenuItem(sender);
        await RunTrayCommandAsync(supervisor => supervisor.RestartAsync());
    }

    private async Task RunTrayCommandAsync(Func<DaemonSupervisor, Task> command)
    {
        var supervisor = _supervisor;
        _daemonCommandBusy = true;
        UpdateTrayCommandAvailability(supervisor.State);
        try
        {
            await command(supervisor);
        }
        finally
        {
            _daemonCommandBusy = false;
            UpdateTrayCommandAvailability(supervisor.State);
        }
    }

    private void OnOpenWeb(object? sender, EventArgs e)
    {
        ResetMenuItem(sender);
        // Logged without the fragment so the bearer token stays out of the tray log.
        _logger.LogInformation("Opening web shell at {WebUri}", _settings.WebUri);
        _processes.LaunchUri(_settings.WebShellUri);
    }

    private async void OnOpenLog(object? sender, EventArgs e)
    {
        ResetMenuItem(sender);
        var path = await _supervisor.ResolveLogPathAsync(CancellationToken.None);
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogWarning("No daemon log found to open.");
            return;
        }

        _processes.Start(path, string.Empty, useShellExec: true);
    }

    private void OnOpenSettings(object? sender, EventArgs e)
    {
        ResetMenuItem(sender);
        _ = _navigation.NavigateTo<SettingsViewModel>();
        ShowMainWindow();
    }

    private async void OnCheckForUpdates(object? sender, EventArgs e)
    {
        ResetMenuItem(sender);

        try
        {
            // The window's banner is driven by the update service, so opening it is what reveals the news.
            if ((await _updates.CheckAsync()).Update is not null)
            {
                ShowMainWindow();
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The update check from the tray failed.");
        }
    }

    private void OnShowWindow(object? sender, EventArgs e)
    {
        ResetMenuItem(sender);
        ShowMainWindow();
    }

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void OnAutostart(object? sender, EventArgs e)
    {
        var enable = !_autostart.IsEnabled();
        _autostart.SetEnabled(enable);
        SetAutostartMenuChecked();
        _logger.LogInformation("Start with Windows {State}.", enable ? "enabled" : "disabled");
    }

    private void OnQuit(object? sender, EventArgs e)
    {
        ResetMenuItem(sender);
        _logger.LogInformation("Quit requested from tray menu.");
        _isExiting = true;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    private static void ResetMenuItem(object? sender)
    {
        if (sender is NativeMenuItem item && item.Header?.ToString() != "Start with Windows") item.IsChecked = false;
    }

    private void OnAutostartChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(SetAutostartMenuChecked);
    }

    private void OnStateChanged(object? sender, DaemonState state)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnStateChanged(sender, state));
            return;
        }

        var file = state switch
        {
            DaemonState.Starting => "status-starting.png",
            DaemonState.Running => "status-running.png",
            DaemonState.Crashed => "status-crashed.png",
            _ => "status-stopped.png"
        };
        if (_icon is not null)
        {
            _icon.Icon = new WindowIcon(AssetLoader.Open(new Uri($"avares://QwenTray/Assets/{file}")));
        }
        SetTrayToolTip(state);
        UpdateTrayCommandAvailability(state);
    }

    // Avalonia's TrayIcon has no balloon API, so an available update is surfaced where the tray can
    // reach without a window: the hover tooltip.
    private void SetTrayToolTip(DaemonState state)
    {
        if (_icon is null) return;

        var status = state.ToString().ToLowerInvariant();
        _icon.ToolTipText = _availableUpdateVersion is null
            ? $"QwenTray: {status}"
            : $"QwenTray: {status}. Update available ({_availableUpdateVersion}).";
    }

    private void OnUpdateAvailable(object? sender, UpdateInfo info)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnUpdateAvailable(sender, info));
            return;
        }

        _availableUpdateVersion = info.Version;
        SetTrayToolTip(_supervisor.State);
    }

    private void UpdateTrayCommandAvailability(DaemonState state)
    {
        var availability = DaemonCommandAvailability.From(state, _daemonCommandBusy);
        SetMenuEnabled("Start", availability.CanStart);
        SetMenuEnabled("Stop", availability.CanStop);
        SetMenuEnabled("Restart", availability.CanRestart);
    }

    private void SetMenuEnabled(string header, bool enabled)
    {
        var item = _icon?.Menu?.Items.OfType<NativeMenuItem>().FirstOrDefault(x => x.Header?.ToString() == header);
        if (item is not null) item.IsEnabled = enabled;
    }

    private void SetAutostartMenuChecked()
    {
        var item = _icon?.Menu?.Items.OfType<NativeMenuItem>().FirstOrDefault(x => x.Header?.ToString() == "Start with Windows");
        item?.IsChecked = _autostart.IsEnabled();
    }

    // Opened fires on every show, and hiding to the tray then showing again counts. The shell view
    // model is a singleton, so initializing per show appends a second set of nav items.
    private void OnMainWindowOpened(object? sender, EventArgs e)
    {
        if (_isShellInitialized) return;
        _isShellInitialized = true;
        _ = _mainViewModel?.InitializeAsync();
    }

    // Closing the window parks it in the tray. The daemon keeps running from there.
    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isExiting) return;
        e.Cancel = true;
        _mainWindow?.Hide();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (_stopping) return;
        _stopping = true;
        _timer?.Stop();
        _updateTimer?.Stop();
    }
}
