using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using QwenTray.Services;
using QwenTray.Views;

namespace QwenTray.ViewModels;

/// <summary>
/// The Linux environment: the distro itself and the developer tools installed inside it.
/// </summary>
public sealed partial class EnvironmentViewModel : ViewModelBase<EnvironmentView>
{
    private readonly Settings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly DaemonSupervisor _supervisor;
    private readonly IWslDistro _wsl;
    private readonly WslDistroProvisioner _provisioner;
    private readonly DevToolInstaller _devTools;
    private readonly IBackupHold _hold;
    private readonly ISnackbarService _snackbars;
    private readonly IConfirmDialogService _confirm;
    private readonly SettingsSaveViewModel _save;
    private readonly ILogger<EnvironmentViewModel> _logger;

    [ObservableProperty]
    private string _wslDistroName = string.Empty;

    /// <summary>One row per installable developer tool. Built once, since this page is a singleton.</summary>
    public ObservableCollection<DevToolRowViewModel> DevTools { get; } = [];

    /// <summary>The save footer this page hosts, shared with every other settings page.</summary>
    public SettingsSaveViewModel Save => _save;

    [ObservableProperty]
    private string _distroStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DistroIsChecking))]
    [NotifyPropertyChangedFor(nameof(CanSetupDistro))]
    [NotifyPropertyChangedFor(nameof(CanRebuildDistro))]
    [NotifyPropertyChangedFor(nameof(CanDeleteDistro))]
    [NotifyPropertyChangedFor(nameof(SetupDistroLabel))]
    [NotifyPropertyChangedFor(nameof(SetupDistroHelp))]
    private bool _distroExists;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DistroIsReady))]
    [NotifyPropertyChangedFor(nameof(DistroNeedsAttention))]
    [NotifyPropertyChangedFor(nameof(DistroIsChecking))]
    [NotifyPropertyChangedFor(nameof(DistroBannerTitle))]
    [NotifyPropertyChangedFor(nameof(DistroNextStep))]
    [NotifyPropertyChangedFor(nameof(DistroStatusIconKey))]
    [NotifyPropertyChangedFor(nameof(CanRebuildDistro))]
    [NotifyPropertyChangedFor(nameof(CanDeleteDistro))]
    [NotifyPropertyChangedFor(nameof(SetupDistroLabel))]
    [NotifyPropertyChangedFor(nameof(SetupDistroHelp))]
    private bool _isDistroProvisioned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DistroIsReady))]
    [NotifyPropertyChangedFor(nameof(DistroNeedsAttention))]
    [NotifyPropertyChangedFor(nameof(DistroIsChecking))]
    [NotifyPropertyChangedFor(nameof(DistroBannerTitle))]
    [NotifyPropertyChangedFor(nameof(DistroNextStep))]
    [NotifyPropertyChangedFor(nameof(DistroStatusIconKey))]
    [NotifyPropertyChangedFor(nameof(CanSetupDistro))]
    [NotifyPropertyChangedFor(nameof(CanRebuildDistro))]
    [NotifyPropertyChangedFor(nameof(CanDeleteDistro))]
    [NotifyPropertyChangedFor(nameof(CanCheckDistro))]
    private bool _isDistroBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DistroIsChecking))]
    [NotifyPropertyChangedFor(nameof(DistroBannerTitle))]
    [NotifyPropertyChangedFor(nameof(DistroStatusIconKey))]
    private bool _hasCheckedDistro;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SetupDistroLabel))]
    [NotifyPropertyChangedFor(nameof(SetupDistroHelp))]
    private string? _distroProgress;

    [ObservableProperty]
    private bool _isToolBusy;

    [ObservableProperty]
    private string? _toolProgress;

    public EnvironmentViewModel(
        Settings settings,
        SettingsStore settingsStore,
        DaemonSupervisor supervisor,
        IWslDistro wsl,
        WslDistroProvisioner provisioner,
        DevToolInstaller devTools,
        IBackupHold hold,
        ISnackbarService snackbars,
        IConfirmDialogService confirm,
        SettingsSaveViewModel save,
        ILogger<EnvironmentViewModel> logger)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _supervisor = supervisor;
        _wsl = wsl;
        _provisioner = provisioner;
        _devTools = devTools;
        _hold = hold;
        _snackbars = snackbars;
        _confirm = confirm;
        _save = save;
        _logger = logger;

        // A backup holds the distro from outside it, so nothing that writes into it may run alongside.
        _hold.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(IBackupHold.IsHeld)) return;
            OnPropertyChanged(nameof(CanSetupDistro));
            OnPropertyChanged(nameof(CanRebuildDistro));
            OnPropertyChanged(nameof(CanDeleteDistro));
            OnPropertyChanged(nameof(CanCheckDistro));
        };

        foreach (var tool in DevToolInstaller.Catalog)
        {
            DevTools.Add(new DevToolRowViewModel(tool, InstallToolAsync, () => !IsToolBusy && !_hold.IsHeld));
        }

        // The shared save reads the distro name even when the user never opened this page, so the
        // singleton has to carry the stored value from construction.
        WslDistroName = _settings.WslDistroName;
    }

    public bool DistroIsChecking => !HasCheckedDistro || IsDistroBusy;

    public bool DistroIsReady => HasCheckedDistro && IsDistroProvisioned && !IsDistroBusy;

    public bool DistroNeedsAttention => HasCheckedDistro && !IsDistroProvisioned && !IsDistroBusy;

    public bool CanSetupDistro => !IsDistroBusy && !_hold.IsHeld;

    public bool CanRebuildDistro => DistroExists && !IsDistroBusy && !_hold.IsHeld;

    public bool CanDeleteDistro => DistroExists && !IsDistroBusy && !_hold.IsHeld;

    // Every command below reads or writes inside the distro, so each needs the same room a backup needs.
    public bool CanCheckDistro => !IsDistroBusy && !_hold.IsHeld;

    /// <summary>
    /// Re-running the setup on a distro that already exists is a repair, not a rebuild, so the label
    /// says which of the two the button is about to do.
    /// </summary>
    public string SetupDistroLabel => DistroExists && IsDistroProvisioned ? "Re-run setup" : "Set up distro";

    public string SetupDistroHelp => DistroProgress is not null
        ? "The distro is being worked on. Leave this page open until it finishes."
        : !DistroExists
            ? "Creates the distro, installs Qwen and the gh CLI, and adds a Linux user for every profile."
            : IsDistroProvisioned
                ? "Adds any profile that is missing its Linux user and reinstalls Qwen and the gh CLI. It deletes nothing, so re-running it is safe."
                : "Finishes what this distro is missing: the isolation settings, Qwen, the gh CLI, and a Linux user for every profile.";

    public string DistroBannerTitle => !HasCheckedDistro
        ? "Checking the Linux environment…"
        : IsDistroBusy
            ? "Working on the Linux environment…"
            : IsDistroProvisioned
                ? "Linux environment is ready"
                : "Linux environment is not set up";

    public string DistroNextStep => DistroIsChecking
        ? string.Empty
        : IsDistroProvisioned
            ? "Next: start the daemon from the Daemon page."
            : "Next: press \"Set up distro\" to create the Linux environment.";

    public string DistroStatusIconKey => !HasCheckedDistro || IsDistroBusy
        ? "arrow_sync_regular"
        : IsDistroProvisioned
            ? "checkmark_circle_regular"
            : "warning_regular";

    protected override Task OnInitializeAsync()
    {
        WslDistroName = _settings.WslDistroName;

        // Surface the Linux environment state without waiting on wsl.exe, so the page opens instantly and
        // fills in as the distro answers.
        _ = RefreshEnvironmentAsync();

        return Task.CompletedTask;
    }

    private async Task RefreshEnvironmentAsync()
    {
        // Opening the page probes inside the distro, which would boot it beside a running backup. The
        // backup commands call this again once they are done, so nothing stays stale.
        if (_hold.IsHeld) return;

        await RefreshDistroAsync();
    }

    [RelayCommand]
    private async Task RefreshDistroAsync()
    {
        IsDistroBusy = true;
        try
        {
            var distro = WslDistroName.Trim();
            var exists = await _wsl.DistroExistsAsync(distro, CancellationToken.None);
            DistroExists = exists;
            if (!exists)
            {
                DistroStatus = $"The \"{distro}\" distro does not exist yet.";
                IsDistroProvisioned = false;
                return;
            }

            IsDistroProvisioned = await _provisioner.IsProvisionedAsync(distro, CancellationToken.None);
            DistroStatus = IsDistroProvisioned ? $"The \"{distro}\" distro is set up." : $"The \"{distro}\" distro exists but is not set up.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checking the distro failed.");
            DistroStatus = $"Could not check the distro: {ex.Message}";
            DistroExists = false;
            IsDistroProvisioned = false;
        }
        finally
        {
            HasCheckedDistro = true;
            IsDistroBusy = false;
        }
    }

    [RelayCommand]
    private Task SetupDistroAsync() => ProvisionAsync(rebuild: false);

    [RelayCommand]
    private async Task RebuildDistroAsync()
    {
        var distro = WslDistroName.Trim();
        var confirmed = await _confirm.ConfirmAsync(new ConfirmDialogRequest(
            $"Rebuild the \"{distro}\" distro?",
            "The current distro is deleted and a fresh one is created under the same name, then set up again.",
            "Rebuild",
            "Everything inside is destroyed and does not come back on its own: every profile's ssh and signing keys, GitHub sign-ins, workspaces, and Qwen configuration. The daemon is stopped first."));
        if (!confirmed)
        {
            return;
        }

        await ProvisionAsync(rebuild: true);
    }

    /// <summary>
    /// Creates or repairs the distro. A rebuild takes the same path after deleting it, so the two stay
    /// honest about producing the same result.
    /// </summary>
    private async Task ProvisionAsync(bool rebuild)
    {
        var distro = WslDistroName.Trim();
        if (string.IsNullOrWhiteSpace(distro))
        {
            _snackbars.Show("Set the WSL distro name first.", SnackbarKind.Error);
            return;
        }

        IsDistroBusy = true;
        DistroProgress = rebuild
            ? "Deleting the old distro and building a new one, then installing Qwen and the gh CLI. This takes a few minutes."
            : "Creating the distro and installing Qwen and the gh CLI. This takes a few minutes on first run.";
        try
        {
            // The setup runs off this profile list, so the Identity page's pending edits are committed
            // first: a profile added but not saved still gets its Linux user and its qwen install.
            _settings.Profiles = _save.CommitProfilesForDistroWork();
            await using var distroHold = await _supervisor.HoldAsync("setting up the distro");
            _provisioner.WriteWslConfig();
            if (rebuild)
            {
                await _provisioner.RebuildAsync(distro, _settings.Profiles, CancellationToken.None);
            }
            else
            {
                await _provisioner.ProvisionAsync(distro, _settings.Profiles, CancellationToken.None);
            }

            var resolved = await _provisioner.ResolveQwenPathAsync(distro, _settings.ResolveActiveProfile(), CancellationToken.None);
            if (resolved is not null) _settings.ResolveActiveProfile().QwenExecutable = resolved;
            DistroExists = true;
            IsDistroProvisioned = true;
            DistroStatus = $"The \"{distro}\" distro is set up.";
            _settingsStore.Save(_settings);
            _snackbars.Show($"{(rebuild ? "Rebuilt" : "Set up")} the \"{distro}\" distro. Sign in to Windows again or run 'wsl --shutdown' so the mirrored networking change takes effect.", SnackbarKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Provisioning the distro failed.");
            DistroStatus = "Setup did not complete.";
            _snackbars.Show($"Setup failed: {ex.Message}", SnackbarKind.Error);
            IsDistroProvisioned = false;
        }
        finally
        {
            DistroProgress = null;
            HasCheckedDistro = true;
            IsDistroBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteDistroAsync()
    {
        var distro = WslDistroName.Trim();
        var confirmed = await _confirm.ConfirmAsync(new ConfirmDialogRequest(
            $"Delete the \"{distro}\" distro?",
            "The distro is unregistered and its filesystem is discarded. Nothing is created afterwards, so QwenTray goes back to needing a setup.",
            "Delete distro",
            "This destroys every profile's ssh and signing keys, GitHub sign-ins, workspaces, and Qwen configuration inside it. Keys you added to GitHub have to be removed there by hand."));
        if (!confirmed)
        {
            return;
        }

        IsDistroBusy = true;
        try
        {
            // wsl refuses to unregister a running distro, and a daemon writing into a distro that is
            // about to be deleted would lose its log the same way.
            await using var distroHold = await _supervisor.HoldAsync("deleting the distro");
            await _provisioner.DeleteAsync(distro, CancellationToken.None);
            DistroExists = false;
            IsDistroProvisioned = false;
            DistroStatus = $"The \"{distro}\" distro has been deleted.";
            _snackbars.Show($"Deleted the \"{distro}\" distro.", SnackbarKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deleting the distro failed.");
            _snackbars.Show($"Could not delete the distro: {ex.Message}", SnackbarKind.Error);
        }
        finally
        {
            HasCheckedDistro = true;
            IsDistroBusy = false;
        }
    }

    /// <summary>
    /// Installs one developer tool into the distro. Most tools go on the shared path as root; a tool
    /// scoped to the profile homes is installed for every profile, so any identity can use it.
    /// </summary>
    private async Task InstallToolAsync(DevTool tool)
    {
        var distro = WslDistroName.Trim();
        if (string.IsNullOrWhiteSpace(distro))
        {
            _snackbars.Show("Set the WSL distro name first.", SnackbarKind.Error);
            return;
        }

        if (HasCheckedDistro && !IsDistroProvisioned)
        {
            _snackbars.Show("Set up the Linux environment before installing developer tools.", SnackbarKind.Warning);
            return;
        }

        IsToolBusy = true;
        ToolProgress = $"Installing {tool.Name} in the distro. This can take a few minutes.";
        try
        {
            var result = await _devTools.InstallAsync(distro, tool, _settings.Profiles, CancellationToken.None);
            if (!result.Succeeded)
            {
                _logger.LogWarning("Installing developer tool {Tool} reported: {Output}", tool.Id, result.Output);
                _snackbars.Show($"Installing {tool.Name} failed: {result.Output}", SnackbarKind.Error);
                return;
            }

            _snackbars.Show($"{tool.Name} is installed.", SnackbarKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Installing developer tool {Tool} failed.", tool.Id);
            _snackbars.Show($"Installing {tool.Name} failed: {ex.Message}", SnackbarKind.Error);
        }
        finally
        {
            ToolProgress = null;
            IsToolBusy = false;
        }
    }

    partial void OnIsToolBusyChanged(bool value)
    {
        // One install at a time: apt and the toolchain installers share locks and a PATH.
        foreach (var tool in DevTools) tool.InstallCommand.NotifyCanExecuteChanged();
    }
}
