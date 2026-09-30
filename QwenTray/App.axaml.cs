using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QwenTray.Services;
using QwenTray.ViewModels;
using QwenTray.Views;

namespace QwenTray;

public partial class App : Application
{
    public static IServiceProvider? Services { get; set; }

    private DaemonSupervisor? _supervisor;
    private Settings? _settings;
    private RegistryAutostartService _autostart = new();
    private INavigationProvider? _navigation;
    private ILogger<App> _logger = NullLogger<App>.Instance;
    private TrayIcon? _icon;
    private MainWindow? _mainWindow;
    private IMainWindowViewModel? _mainViewModel;
    private DispatcherTimer? _timer;
    private bool _daemonCommandBusy;
    private bool _isShellInitialized;
    private bool _isExiting;
    private bool _stopping;

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

        var services = Services;
        _settings = services?.GetService<Settings>() ?? SettingsStore.Load();
        _logger = services?.GetService<ILogger<App>>() ?? NullLogger<App>.Instance;
        _autostart = services?.GetService<RegistryAutostartService>() ?? new RegistryAutostartService();
        _supervisor = services?.GetService<DaemonSupervisor>() ?? new DaemonSupervisor(_settings);
        _navigation = services?.GetService<INavigationProvider>();
        _supervisor.StateChanged += OnStateChanged;
        _autostart.Changed += OnAutostartChanged;
        _icon = GetValue(TrayIcon.IconsProperty)?.FirstOrDefault();
        UpdateTrayCommandAvailability(_supervisor.State);
        SetAutostartMenuChecked();

        if (services?.GetService<MainWindow>() is { } mainWindow && services.GetService<IMainWindowViewModel>() is { } mainViewModel)
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
        _logger.LogInformation("QwenTray tray ready. Executable={Executable} HealthUri={HealthUri} PollSeconds={PollSeconds}", _settings.QwenExecutable, _settings.HealthUri, pollSeconds);
        _ = _supervisor.StartAsync();
        base.OnFrameworkInitializationCompleted();
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
        if (supervisor is null) return;
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
        if (_settings is null) return;
        _logger.LogInformation("Opening web shell at {WebUri}", _settings.WebUri);
        Process.Start(new ProcessStartInfo(_settings.WebUri.ToString()) { UseShellExecute = true });
    }

    private void OnOpenLog(object? sender, EventArgs e)
    {
        ResetMenuItem(sender);
        var path = _supervisor?.LogPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".qwen", "debug", "daemon", "runs");
            if (Directory.Exists(root)) path = Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogWarning("No daemon log found to open.");
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OnOpenSettings(object? sender, EventArgs e)
    {
        ResetMenuItem(sender);
        _ = _navigation?.NavigateTo<SettingsViewModel>();
        ShowMainWindow();
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
            _icon.ToolTipText = $"QwenTray: {state.ToString().ToLowerInvariant()}";
        }
        UpdateTrayCommandAvailability(state);
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
    }
}