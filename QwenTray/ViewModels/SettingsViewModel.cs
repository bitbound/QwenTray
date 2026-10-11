using Bitbound.SystemAbstractions.Processes;
using Microsoft.Extensions.Logging;
using QwenTray.Services;
using QwenTray.Updates;
using QwenTray.Views;

namespace QwenTray.ViewModels;

/// <summary>
/// The tray app's own preferences: how it watches the daemon, when it checks for a new QwenTray, who it
/// trusts to sign one, and where its own log and settings live.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase<SettingsView>
{
    private readonly Settings _settings;
    private readonly RegistryAutostartService _autostart;
    private readonly ICertificateTrustService _certTrust;
    private readonly IProcessManager _processes;
    private readonly DaemonSupervisor _supervisor;
    private readonly IUpdateService _updates;
    private readonly ISnackbarService _snackbars;
    private readonly SettingsSaveViewModel _save;
    private readonly ILogger<SettingsViewModel> _logger;

    /// <summary>The save footer this page hosts, shared with every other settings page.</summary>
    public SettingsSaveViewModel Save => _save;

    [ObservableProperty]
    private string _healthPollSeconds = string.Empty;

    [ObservableProperty]
    private bool _autoRestart;

    [ObservableProperty]
    private bool _checkForUpdates;

    [ObservableProperty]
    private bool _isCheckingForUpdates;

    [ObservableProperty]
    private string? _updateCheckStatus;

    [ObservableProperty]
    private string _maxBackoffSeconds = string.Empty;

    [ObservableProperty]
    private bool _startOnLogin;

    [ObservableProperty]
    private bool _isPublisherTrusted;

    [ObservableProperty]
    private bool _isTrustingPublisher;

    public SettingsViewModel(
        Settings settings,
        RegistryAutostartService autostart,
        ICertificateTrustService certTrust,
        IProcessManager processes,
        DaemonSupervisor supervisor,
        IUpdateService updates,
        ISnackbarService snackbars,
        SettingsSaveViewModel save,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _autostart = autostart;
        _certTrust = certTrust;
        _processes = processes;
        _supervisor = supervisor;
        _updates = updates;
        _snackbars = snackbars;
        _save = save;
        _logger = logger;
    }

    public string SettingsFilePath => SettingsStore.FilePath;

    public string LogFilePath => SettingsStore.LogFilePath;

    protected override Task OnInitializeAsync()
    {
        HealthPollSeconds = _settings.HealthPollSeconds.ToString();
        AutoRestart = _settings.AutoRestart;
        CheckForUpdates = _settings.CheckForUpdates;
        MaxBackoffSeconds = _settings.MaxBackoffSeconds.ToString();
        StartOnLogin = _autostart.IsEnabled();
        RefreshPublisherTrustState();

        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CheckForUpdatesNowAsync()
    {
        IsCheckingForUpdates = true;
        UpdateCheckStatus = "Asking GitHub for the latest release…";

        try
        {
            var result = await _updates.CheckAsync();

            if (result.Update is { } update)
            {
                UpdateCheckStatus = $"Version {update.Version} is available. Use the link in the status bar to install it.";
                _snackbars.Show(UpdateCheckStatus, SnackbarKind.Info);
            }
            else if (result.Status == QwenTray.Updates.UpdateCheckStatus.Failed)
            {
                UpdateCheckStatus = "Could not check for updates. See the log for details.";
                _snackbars.Show(UpdateCheckStatus, SnackbarKind.Error);
            }
            else
            {
                UpdateCheckStatus = "QwenTray is up to date.";
                _snackbars.Show(UpdateCheckStatus, SnackbarKind.Success);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The update check failed.");
            UpdateCheckStatus = $"Could not check for updates: {ex.Message}";
            _snackbars.Show(UpdateCheckStatus, SnackbarKind.Error);
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    // QwenTray is signed with a self-signed cert that public machines have no reason to trust, so
    // the settings page offers to plant the public key in the per-user Trusted Root store (no elevation).
    private void RefreshPublisherTrustState()
    {
        try
        {
            IsPublisherTrusted = _certTrust.IsPublisherTrusted();
        }
        catch (Exception ex)
        {
            _snackbars.Show($"Could not read the certificate stores: {ex.Message}", SnackbarKind.Error);
        }
    }

    [RelayCommand]
    private async Task TrustPublisherAsync()
    {
        IsTrustingPublisher = true;

        try
        {
            // Certificate store writes can hit slow group-policy callbacks, so keep the UI free.
            await Task.Run(_certTrust.TrustPublisher);

            IsPublisherTrusted = true;
            _snackbars.Show("Bitbound is now trusted for this user account. Already-downloaded copies of QwenTray show Bitbound as the verified publisher.", SnackbarKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trusting the publisher certificate failed.");
            _snackbars.Show($"Could not trust the certificate: {ex.Message}", SnackbarKind.Error);
        }
        finally
        {
            IsTrustingPublisher = false;
        }
    }

    [RelayCommand]
    private async Task OpenLogFileAsync()
    {
        var path = await _supervisor.ResolveLogPathAsync(CancellationToken.None);
        if (string.IsNullOrWhiteSpace(path))
        {
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
            _processes.Start("explorer.exe", $"\"{SettingsStore.DirectoryPath}\"", useShellExec: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening the QwenTray folder failed.");
            _snackbars.Show($"Could not open the QwenTray folder: {ex.Message}", SnackbarKind.Error);
        }
    }
}
