using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QwenTray.Services;
using QwenTray.ViewModels;
using QwenTray.Views;
using System.Threading;

namespace QwenTray;

internal static class Program
{
    private static Mutex? _mutex;
    private static ServiceProvider? _services;

    [STAThread]
    public static void Main(string[] args)
    {
        _mutex = new Mutex(initiallyOwned: true, "Local\\QwenTray.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            return;
        }

        try
        {
            _services = BuildServices(SettingsStore.Load(), SettingsStore.LogFilePath);
            App.Services = _services;
            var logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger("QwenTray.Program");
            logger.LogInformation("Starting QwenTray. ProcessId={ProcessId} Executable={Executable} LogFile={LogFile}", Environment.ProcessId, Environment.ProcessPath, SettingsStore.LogFilePath);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            logger.LogInformation("QwenTray application lifetime ended.");
        }
        catch (Exception exception)
        {
            _services?.GetService<ILoggerFactory>()?.CreateLogger("QwenTray.Program").LogCritical(exception, "QwenTray terminated after an unhandled exception.");
            throw;
        }
        finally
        {
            App.Services = null;
            // DaemonSupervisor is IAsyncDisposable-only, so the container must be disposed asynchronously.
            _services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    internal static ServiceProvider BuildServices(Settings settings, string logFilePath)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(logFilePath));
        });
        services.AddSingleton(settings);
        services.AddSingleton<RegistryAutostartService>();
        services.AddSingleton<DaemonSupervisor>();
        services.AddSingleton<ICertificateTrustService, BitboundCertificateTrustService>();

        services.AddSingleton<INavigationProvider, NavigationProvider>();
        services.AddSingleton<IMainWindowViewModel, MainWindowViewModel>();
        services.AddSingleton<DaemonViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AboutViewModel>();

        services.AddTransient<MainWindow>();
        services.AddTransient<DaemonView>();
        services.AddTransient<SettingsView>();
        services.AddTransient<AboutView>();

        return services.BuildServiceProvider();
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
