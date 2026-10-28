using Microsoft.Extensions.Logging;
using QwenTray.Services;
using QwenTray.Views;

namespace QwenTray.ViewModels;

/// <summary>
/// The two backups. A WSL backup carries the whole distro plus this machine's settings as one bundle,
/// and a configuration backup carries a ZIP of the files listed from the active profile's home.
/// </summary>
public sealed partial class BackupViewModel : ViewModelBase<BackupView>
{
    private readonly Settings _settings;
    private readonly DaemonSupervisor _supervisor;
    private readonly DistroBackupService _distroBackup;
    private readonly IQwenConfigArchiveService _archive;
    private readonly IDistroHold _hold;
    private readonly EnvironmentViewModel _environment;
    private readonly SettingsSaveViewModel _save;
    private readonly ISnackbarService _snackbars;
    private readonly IConfirmDialogService _confirm;
    private readonly ILogger<BackupViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExportBundle))]
    [NotifyPropertyChangedFor(nameof(CanImportBundle))]
    private string _bundlePath = string.Empty;

    [ObservableProperty]
    private string? _bundleProgress;

    [ObservableProperty]
    private string _configPaths = string.Empty;

    [ObservableProperty]
    private string _configArchivePath = string.Empty;

    public BackupViewModel(
        Settings settings,
        DaemonSupervisor supervisor,
        DistroBackupService distroBackup,
        IQwenConfigArchiveService archive,
        IDistroHold hold,
        EnvironmentViewModel environment,
        SettingsSaveViewModel save,
        ISnackbarService snackbars,
        IConfirmDialogService confirm,
        ILogger<BackupViewModel> logger)
    {
        _settings = settings;
        _supervisor = supervisor;
        _distroBackup = distroBackup;
        _archive = archive;
        _hold = hold;
        _environment = environment;
        _save = save;
        _snackbars = snackbars;
        _confirm = confirm;
        _logger = logger;

        // Each command here works the distro as a whole, so each waits on the shared hold that every
        // command taking the distro raises. Only the distro's existence comes from the environment page.
        _hold.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IDistroHold.IsHeld)) RaiseCommandGates();
        };
        _environment.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EnvironmentViewModel.DistroExists)) RaiseCommandGates();
        };

        // The shared save reads these fields even when the user never opened this page, so the singleton
        // has to carry them from construction.
        BundlePath = DistroBackupService.DefaultBundleDirectory(_settings.BundleDirectory);
        ConfigPaths = string.Join(Environment.NewLine, EffectiveConfigPaths());
        ConfigArchivePath = QwenConfigArchiveService.DefaultArchivePath(_settings.ConfigArchiveDirectory);
    }

    /// <summary>The save footer this page hosts, shared with every other settings page.</summary>
    public SettingsSaveViewModel Save => _save;

    public bool CanExportBundle => _environment.DistroExists && !_hold.IsHeld;

    public bool CanImportBundle => !_hold.IsHeld && !string.IsNullOrWhiteSpace(BundlePath);

    public bool CanArchiveConfig => !_hold.IsHeld;

    private void RaiseCommandGates()
    {
        OnPropertyChanged(nameof(CanExportBundle));
        OnPropertyChanged(nameof(CanImportBundle));
        OnPropertyChanged(nameof(CanArchiveConfig));
    }

    protected override async Task OnInitializeAsync()
    {
        // Export gates on the distro existing, and that answer only comes from the Environment page's
        // probe. A session that opens this page first never runs it, which left Export disabled for the
        // rest of the session. A probe already made is not repeated, so a revisit costs nothing.
        if (!_environment.HasCheckedDistro) await _environment.RefreshEnvironmentAsync();

        // A typed path survives a revisit, while an emptied field gets a fresh name in the stored folder.
        if (string.IsNullOrWhiteSpace(BundlePath))
        {
            BundlePath = DistroBackupService.DefaultBundleDirectory(_settings.BundleDirectory);
        }

        ConfigPaths = string.Join(Environment.NewLine, EffectiveConfigPaths());
        if (string.IsNullOrWhiteSpace(ConfigArchivePath))
        {
            ConfigArchivePath = QwenConfigArchiveService.DefaultArchivePath(_settings.ConfigArchiveDirectory);
        }
    }

    /// <summary>
    /// Copies the distro's disk and this machine's settings into a bundle folder. Nothing is destroyed, but
    /// the distro is stopped so its disk is not copied while it is mounted, and it stays stopped.
    /// </summary>
    [RelayCommand]
    private async Task ExportBundleAsync()
    {
        var path = BundlePath.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            _snackbars.Show("Set a bundle path first.", SnackbarKind.Error);
            return;
        }

        _settings.Profiles = _save.CommitProfilesForDistroWork();
        _hold.IsHeld = true;
        BundleProgress = "Stopping the distro and copying its disk into the bundle folder. This takes a few minutes.";
        try
        {
            await using var distroHold = await _supervisor.HoldAsync("exporting the bundle");
            var result = await _distroBackup.ExportAsync(path, CancellationToken.None);
            _environment.DistroStatus = $"The \"{_environment.WslDistroName.Trim()}\" distro is set up and stopped by the export.";
            _snackbars.Show($"Wrote {DistroBackupService.FormatBytes(result.BundleBytes)} to {result.BundlePath}. It holds live credentials, so keep it somewhere private.", SnackbarKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exporting the distro failed.");
            _snackbars.Show($"Export failed: {ex.Message}", SnackbarKind.Error);
        }
        finally
        {
            BundleProgress = null;
            _hold.IsHeld = false;
            await _environment.InitializeAsync();
        }
    }

    /// <summary>
    /// Registers the bundle's distro and adopts its settings. The bundle is read and checked before
    /// anything is destroyed, so the prompt can name exactly what is about to be replaced.
    /// </summary>
    [RelayCommand]
    private async Task ImportBundleAsync()
    {
        var path = BundlePath.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            _snackbars.Show("Set a bundle path first.", SnackbarKind.Error);
            return;
        }

        BundleContents contents;
        try
        {
            contents = await _distroBackup.ProbeAsync(path, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading the bundle failed.");
            _snackbars.Show($"That bundle cannot be imported: {ex.Message}", SnackbarKind.Error);
            return;
        }

        var distro = contents.Manifest.WslDistroName;
        var confirmed = await _confirm.ConfirmAsync(new ConfirmDialogRequest(
            $"Import the bundle made {contents.Manifest.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}?",
            "It registers the distro from the bundle and takes QwenTray's settings from it, including the bearer token and every profile. Settings are then protected for the Windows account on this machine.",
            "Import",
            $"A distro already named \"{distro}\" is unregistered first, taking its profiles' keys, GitHub sign-ins, workspaces, and Qwen configuration with it. The daemon is stopped."));
        if (!confirmed)
        {
            return;
        }

        _hold.IsHeld = true;
        BundleProgress = "Copying the disk out of the bundle and registering the distro. This takes a few minutes.";
        try
        {
            await using var distroHold = await _supervisor.HoldAsync("importing the bundle");
            var manifest = await _distroBackup.ImportAsync(path, CancellationToken.None);
            await InitializeAsync();
            _snackbars.Show($"Imported the \"{manifest.WslDistroName}\" distro. Sign in to Windows again or run 'wsl --shutdown' so the mirrored networking change takes effect.", SnackbarKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Importing the distro failed.");
            _snackbars.Show($"Import failed: {ex.Message}", SnackbarKind.Error);
        }
        finally
        {
            BundleProgress = null;
            _hold.IsHeld = false;
            await _environment.InitializeAsync();
        }
    }

    [RelayCommand]
    private async Task ExportConfigAsync()
    {
        var distro = _settings.WslDistroName.Trim();
        if (string.IsNullOrWhiteSpace(distro))
        {
            _snackbars.Show("Set the WSL distro name first.", SnackbarKind.Error);
            return;
        }

        var archivePath = ConfigArchivePath.Trim();
        if (archivePath.Length == 0)
        {
            _snackbars.Show("Set an archive path first.", SnackbarKind.Error);
            return;
        }

        var profile = _settings.ResolveActiveProfile();
        _hold.IsHeld = true;
        try
        {
            var result = await _archive.ExportAsync(distro, profile, SplitLines(ConfigPaths), archivePath, CancellationToken.None);
            // The next export gets a fresh timestamp rather than landing on the one just written.
            ConfigArchivePath = QwenConfigArchiveService.DefaultArchivePath(_settings.ConfigArchiveDirectory);
            _snackbars.Show(
                $"Wrote {result.FilesWritten} file(s) from the \"{profile.Name}\" profile to {result.ArchivePath}.{Notes(result.Missing, result.Invalid)}",
                result.FilesWritten > 0 ? SnackbarKind.Success : SnackbarKind.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exporting the Qwen configuration failed.");
            _snackbars.Show($"Export failed: {ex.Message}", SnackbarKind.Error);
        }
        finally
        {
            _hold.IsHeld = false;
        }
    }

    /// <summary>
    /// Writes the archive back into the active profile's home. The archive is read before anything is
    /// written, so the prompt can name what is about to be replaced.
    /// </summary>
    [RelayCommand]
    private async Task RestoreConfigAsync()
    {
        var distro = _settings.WslDistroName.Trim();
        if (string.IsNullOrWhiteSpace(distro))
        {
            _snackbars.Show("Set the WSL distro name first.", SnackbarKind.Error);
            return;
        }

        var archivePath = ConfigArchivePath.Trim();
        if (archivePath.Length == 0)
        {
            _snackbars.Show("Set an archive path first.", SnackbarKind.Error);
            return;
        }

        var profile = _settings.ResolveActiveProfile();
        _hold.IsHeld = true;
        try
        {
            var manifest = await _archive.ProbeAsync(archivePath, CancellationToken.None);
            var confirmed = await _confirm.ConfirmAsync(new ConfirmDialogRequest(
                "Restore the configuration?",
                $"The files in this archive are written into the \"{profile.Name}\" profile and replace any file at the same path.",
                "Restore",
                $"Archive: {archivePath}. Written from {manifest.WslDistroName} as {manifest.LinuxUser} on {manifest.CreatedAtUtc.ToLocalTime():g}, holding {manifest.Files.Length} file(s)."));
            if (!confirmed) return;

            var result = await _archive.RestoreAsync(distro, profile, archivePath, CancellationToken.None);
            _snackbars.Show(
                $"Restored {result.FilesWritten} file(s) into the \"{profile.Name}\" profile.{Notes(result.Missing, result.Invalid)}",
                result.FilesWritten > 0 ? SnackbarKind.Success : SnackbarKind.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restoring the Qwen configuration failed.");
            _snackbars.Show($"Restore failed: {ex.Message}", SnackbarKind.Error);
        }
        finally
        {
            _hold.IsHeld = false;
        }
    }

    /// <summary>
    /// Names the listed paths the export could not use, so a stale entry in the list is visible rather
    /// than silently absent from the archive.
    /// </summary>
    private static string Notes(string[] missing, string[] invalid)
    {
        var notes = new List<string>();
        if (missing.Length > 0) notes.Add($"Not found: {string.Join(", ", missing)}.");
        if (invalid.Length > 0) notes.Add($"Not absolute Linux paths: {string.Join(", ", invalid)}.");
        return notes.Count > 0 ? " " + string.Join(" ", notes) : string.Empty;
    }

    private string[] EffectiveConfigPaths() =>
        _settings.ConfigBackupPaths.Length > 0
            ? _settings.ConfigBackupPaths
            : QwenConfigArchiveService.DefaultPaths(_settings.ResolveActiveProfile());

    private static string[] SplitLines(string? text) =>
        (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
