using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace QwenTray;

public partial class App : Application
{
    public static IServiceProvider? Services { get; set; }

    private DaemonSupervisor? _supervisor;
    private Settings? _settings;
    private RegistryAutostartService _autostart = new();
    private ILogger<App> _logger = NullLogger<App>.Instance;
    private TrayIcon? _icon;
    private DispatcherTimer? _timer;
    private bool _stopping;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += OnExit;
        }

        var services = Services;
        _settings = services?.GetService<Settings>() ?? SettingsStore.Load();
        _logger = services?.GetService<ILogger<App>>() ?? NullLogger<App>.Instance;
        _autostart = services?.GetService<RegistryAutostartService>() ?? new RegistryAutostartService();
        _supervisor = services?.GetService<DaemonSupervisor>() ?? new DaemonSupervisor(_settings);
        _supervisor.StateChanged += OnStateChanged;
        _icon = GetValue(TrayIcon.IconsProperty)?.FirstOrDefault();
        SetAutostartMenuChecked();
        var pollSeconds = Math.Clamp(_settings.HealthPollSeconds, 1, 60);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(pollSeconds) };
        _timer.Tick += async (_, _) => await _supervisor.PollAsync();
        _timer.Start();
        _logger.LogInformation("QwenTray tray ready. Executable={Executable} HealthUri={HealthUri} PollSeconds={PollSeconds}", _settings.QwenExecutable, _settings.HealthUri, pollSeconds);
        _ = _supervisor.StartAsync();
        base.OnFrameworkInitializationCompleted();
    }

    private async void OnStart(object? sender, EventArgs e) { ResetMenuItem(sender); if (_supervisor is not null) await _supervisor.StartAsync(); }
    private async void OnStop(object? sender, EventArgs e) { ResetMenuItem(sender); if (_supervisor is not null) await _supervisor.StopAsync(); }
    private async void OnRestart(object? sender, EventArgs e) { ResetMenuItem(sender); if (_supervisor is not null) await _supervisor.RestartAsync(); }

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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    private static void ResetMenuItem(object? sender)
    {
        if (sender is NativeMenuItem item && item.Header?.ToString() != "Start with Windows") item.IsChecked = false;
    }

    private void OnStateChanged(object? sender, DaemonState state)
    {
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
    }

    private void SetAutostartMenuChecked()
    {
        var item = _icon?.Menu?.Items.OfType<NativeMenuItem>().FirstOrDefault(x => x.Header?.ToString() == "Start with Windows");
        item?.IsChecked = _autostart.IsEnabled();
    }

    private async void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (_stopping) return;
        _stopping = true;
        _timer?.Stop();
        if (_supervisor is not null) await _supervisor.DisposeAsync();
    }
}
