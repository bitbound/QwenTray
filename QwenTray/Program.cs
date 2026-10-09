using Avalonia;
using Bitbound.SystemAbstractions;
using Bitbound.SystemAbstractions.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QwenTray.Services;
using QwenTray.Updates;
using QwenTray.ViewModels;
using QwenTray.Views;
using System.Net.Http;
using System.Threading;

namespace QwenTray;

internal static class Program
{
    private static Mutex? _mutex;
    private static ServiceProvider? _services;

    [STAThread]
    public static void Main(string[] args)
    {
        // A downloaded copy is launched with the handoff arguments and acts as the installer. It has to
        // run before the single-instance mutex and the UI, because the process it replaces still holds
        // the mutex until it is stopped.
        if (UpdateHandoff.TryParse(args) is not null)
        {
            RunUpdateHandoff(args);
            return;
        }

        _mutex = new Mutex(initiallyOwned: true, "Local\\QwenTray.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            return;
        }

        try
        {
            _services = BuildServices();
            App.Services = _services;

            var logger = _services
              .GetRequiredService<ILoggerFactory>()
              .CreateLogger("QwenTray.Program");

            var settings = _services.GetRequiredService<Settings>();

            logger.LogInformation(
              "Starting QwenTray. ProcessId={ProcessId} Executable={Executable} LogFile={LogFile} Distro={Distro} Profile={Profile}", 
              Environment.ProcessId, 
              Environment.ProcessPath, 
              SettingsStore.LogFilePath,
              settings.WslDistroName, 
              settings.ResolveActiveProfile().Name);

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            logger.LogInformation("QwenTray application lifetime ended.");
        }
        catch (Exception exception)
        {
            _services
              ?.GetService<ILoggerFactory>()
              ?.CreateLogger("QwenTray.Program")
              .LogCritical(exception, "QwenTray terminated after an unhandled exception.");
              
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

    internal static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSystemAbstractions();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information));
        // The container's IFileSystem takes ILogger<T>, which the container builds from the
        // ILoggerFactory that consumes this provider, so handing the provider an IFileSystem
        // here would loop. It captures the provider and resolves IFileSystem on first write,
        // after the ILoggerFactory singleton is cached.
        services.AddSingleton(provider => new FileLoggerProvider(() => provider, SettingsStore.LogFilePath));
        services.AddSingleton<ILoggerProvider>(provider => provider.GetRequiredService<FileLoggerProvider>());

        // Settings are read once on first use, which keeps the load off the container-building path.
        services.AddSingleton<SettingsStore>();
        services.AddSingleton(provider => provider.GetRequiredService<SettingsStore>().Load());

        services.AddSingleton<WslDistro>();
        services.AddSingleton<IWslDistro>(provider => provider.GetRequiredService<WslDistro>());
        services.AddSingleton<WslDistroProvisioner>();
        services.AddSingleton<IdentityService>();
        services.AddSingleton<WslConfigSyncService>();
        services.AddSingleton<RegistryAutostartService>();
        services.AddSingleton<DaemonSupervisor>();
        services.AddSingleton<ICertificateTrustService, BitboundCertificateTrustService>();

        services.AddSingleton(AppInfo.Current());
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
        services.AddSingleton<IUpdateService, GitHubReleaseUpdateService>();

        services.AddSingleton<ISnackbarService, SnackbarService>();
        services.AddSingleton<IConfirmDialogService, ConfirmDialogService>();

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

    /// <summary>
    /// Runs the downloaded copy as the installer. A minimal container is enough, and it uses the same
    /// file logger as the main app so a failed update lands in qwentray.log.
    /// </summary>
    private static void RunUpdateHandoff(string[] args)
    {
        using var services = BuildHandoffServices();
        var runner = services.GetRequiredService<UpdateHandoffRunner>();

        runner.RunAsync(args, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static ServiceProvider BuildHandoffServices()
    {
        var services = new ServiceCollection();
        services.AddSystemAbstractions();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information));
        services.AddSingleton(provider => new FileLoggerProvider(() => provider, SettingsStore.LogFilePath));
        services.AddSingleton<ILoggerProvider>(provider => provider.GetRequiredService<FileLoggerProvider>());
        services.AddSingleton<UpdateHandoffRunner>();

        return services.BuildServiceProvider();
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
