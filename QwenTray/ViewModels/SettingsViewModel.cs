using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Bitbound.SystemAbstractions.Processes;
using Microsoft.Extensions.Logging;
using QRCoder;
using QwenTray.Services;
using QwenTray.Updates;
using QwenTray.Views;

namespace QwenTray.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase<SettingsView>
{
    private static readonly Regex DistroName = new("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.Compiled);

    private readonly Settings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly DaemonSupervisor _supervisor;
    private readonly RegistryAutostartService _autostart;
    private readonly ICertificateTrustService _certTrust;
    private readonly IWslDistro _wsl;
    private readonly WslDistroProvisioner _provisioner;
    private readonly IdentityService _identity;
    private readonly WslConfigSyncService _sync;
    private readonly DevToolInstaller _devTools;
    private readonly IProcessManager _processes;
    private readonly IUpdateService _updates;
    private readonly ISnackbarService _snackbars;
    private readonly IConfirmDialogService _confirm;
    private readonly ILogger<SettingsViewModel> _logger;

    [ObservableProperty]
    private string _wslDistroName = string.Empty;

    public ObservableCollection<IdentityProfile> Profiles { get; } = [];

    /// <summary>One row per installable developer tool. Built once, since this page is a singleton.</summary>
    public ObservableCollection<DevToolRowViewModel> DevTools { get; } = [];

    [ObservableProperty]
    private IdentityProfile? _selectedProfile;

    [ObservableProperty]
    private string _newProfileName = string.Empty;

    [ObservableProperty]
    private string _newProfileUser = string.Empty;

    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private string _profileLinuxUser = string.Empty;

    [ObservableProperty]
    private string _profileWorkspace = string.Empty;

    [ObservableProperty]
    private string _profileEmail = string.Empty;

    [ObservableProperty]
    private string _profileGpgName = string.Empty;

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
    private string _qwenVersion = "Unknown";

    [ObservableProperty]
    private bool _isQwenBusy;

    [ObservableProperty]
    private string? _qwenProgress;

    [ObservableProperty]
    private string _sshPublicKey = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIdentityKeys))]
    private bool _hasSshPublicKey;

    [ObservableProperty]
    private string _gpgPublicKey = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIdentityKeys))]
    private bool _hasGpgPublicKey;

    [ObservableProperty]
    private string _gitHubToken = string.Empty;

    [ObservableProperty]
    private string _gitHubAccountText = "Unknown";

    [ObservableProperty]
    private bool _isGitHubBusy;

    [ObservableProperty]
    private bool _isIdentityBusy;

    [ObservableProperty]
    private string _syncEntries = string.Empty;

    [ObservableProperty]
    private bool _isSyncing;

    [ObservableProperty]
    private bool _isToolBusy;

    [ObservableProperty]
    private string? _toolProgress;

    [ObservableProperty]
    private string _qwenExecutable = string.Empty;

    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private string _port = string.Empty;

    [ObservableProperty]
    private string _token = string.Empty;

    [ObservableProperty]
    private bool _requireAuth;

    [ObservableProperty]
    private string _allowedOrigins = string.Empty;

    [ObservableProperty]
    private string _tlsCertPath = string.Empty;

    [ObservableProperty]
    private string _tlsKeyPath = string.Empty;

    [ObservableProperty]
    private bool _enableSessionShell;

    [ObservableProperty]
    private bool _serveWeb;

    [ObservableProperty]
    private bool _localControl;

    [ObservableProperty]
    private Bitmap? _localControlPairingQr;

    [ObservableProperty]
    private bool _hasLocalControlPairingQr;

    [ObservableProperty]
    private bool _showLocalControlPairingPending;

    [ObservableProperty]
    private string _maxSessions = string.Empty;

    [ObservableProperty]
    private string _maxConnections = string.Empty;

    [ObservableProperty]
    private string _eventRingSize = string.Empty;

    [ObservableProperty]
    private bool _rateLimit;

    [ObservableProperty]
    private bool _debugMode;

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
    private string _extraArgs = string.Empty;

    [ObservableProperty]
    private bool _startOnLogin;

    [ObservableProperty]
    private string? _saveError;

    [ObservableProperty]
    private string? _saveMessage;

    [ObservableProperty]
    private double _saveMessageOpacity = 1d;

    [ObservableProperty]
    private string? _saveWarning;

    [ObservableProperty]
    private bool _isPublisherTrusted;

    [ObservableProperty]
    private bool _isTrustingPublisher;

    // The hold and fade lengths have to agree with the OpacityTransition on the save message in SettingsView.axaml.
    private static readonly TimeSpan SaveMessageHold = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SaveMessageFade = TimeSpan.FromMilliseconds(300);

    private CancellationTokenSource? _saveMessageFadeOut;

    public SettingsViewModel(
        Settings settings,
        SettingsStore settingsStore,
        DaemonSupervisor supervisor,
        RegistryAutostartService autostart,
        ICertificateTrustService certTrust,
        IWslDistro wsl,
        WslDistroProvisioner provisioner,
        IdentityService identity,
        WslConfigSyncService sync,
        DevToolInstaller devTools,
        IProcessManager processes,
        IUpdateService updates,
        ISnackbarService snackbars,
        IConfirmDialogService confirm,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _supervisor = supervisor;
        _autostart = autostart;
        _certTrust = certTrust;
        _wsl = wsl;
        _provisioner = provisioner;
        _identity = identity;
        _sync = sync;
        _devTools = devTools;
        _processes = processes;
        _updates = updates;
        _snackbars = snackbars;
        _confirm = confirm;
        _logger = logger;
        _supervisor.LocalControlPairingUrlChanged += OnLocalControlPairingUrlChanged;

        foreach (var tool in DevToolInstaller.Catalog)
        {
            DevTools.Add(new DevToolRowViewModel(tool, InstallToolAsync, () => !IsToolBusy));
        }
    }

    public string SettingsFilePath => SettingsStore.FilePath;

    public string LogFilePath => SettingsStore.LogFilePath;

    public bool DistroIsChecking => !HasCheckedDistro || IsDistroBusy;

    public bool DistroIsReady => HasCheckedDistro && IsDistroProvisioned && !IsDistroBusy;

    public bool DistroNeedsAttention => HasCheckedDistro && !IsDistroProvisioned && !IsDistroBusy;

    public bool CanSetupDistro => !IsDistroBusy;

    public bool CanRebuildDistro => DistroExists && !IsDistroBusy;

    public bool CanDeleteDistro => DistroExists && !IsDistroBusy;

    public bool HasIdentityKeys => HasSshPublicKey || HasGpgPublicKey;

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
        LoadProfiles();
        QwenExecutable = _settings.ResolveActiveProfile().ResolvedQwenExecutable;
        Host = _settings.Host;
        Port = _settings.Port.ToString();
        Token = _settings.Token;
        RequireAuth = _settings.RequireAuth;
        AllowedOrigins = string.Join(Environment.NewLine, _settings.AllowedOrigins);
        TlsCertPath = _settings.TlsCertPath;
        TlsKeyPath = _settings.TlsKeyPath;
        EnableSessionShell = _settings.EnableSessionShell;
        ServeWeb = _settings.ServeWeb;
        LocalControl = _settings.LocalControl;
        MaxSessions = _settings.MaxSessions.ToString();
        MaxConnections = _settings.MaxConnections.ToString();
        EventRingSize = _settings.EventRingSize.ToString();
        RateLimit = _settings.RateLimit;
        DebugMode = _settings.DebugMode;
        HealthPollSeconds = _settings.HealthPollSeconds.ToString();
        AutoRestart = _settings.AutoRestart;
        CheckForUpdates = _settings.CheckForUpdates;
        MaxBackoffSeconds = _settings.MaxBackoffSeconds.ToString();
        ExtraArgs = string.Join(Environment.NewLine, _settings.ExtraArgs);
        SyncEntries = string.Join(Environment.NewLine, EffectiveSyncPaths());
        StartOnLogin = _autostart.IsEnabled();
        UpdateLocalControlPairing(_supervisor.LocalControlPairingUrl);
        RefreshPublisherTrustState();

        // Surface the Linux environment state and the profile's keys without waiting on wsl.exe, so
        // the page opens instantly and fills in as the distro answers.
        _ = RefreshEnvironmentAsync();

        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        SaveError = null;
        SaveMessage = null;
        SaveWarning = null;

        if (!int.TryParse(Port.Trim(), out var port) || port is < 0 or > 65535)
        {
            SaveError = "Port must be a number between 0 and 65535.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            SaveError = "Hostname cannot be empty.";
            return;
        }

        if (LocalControl && !string.Equals(Host.Trim(), "127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            SaveError = "Device pairing requires the main address to be 127.0.0.1. Change the address or turn off device pairing.";
            return;
        }

        if (LocalControl && !ServeWeb)
        {
            SaveError = "Device pairing requires the Web Shell to be enabled.";
            return;
        }

        if (string.IsNullOrWhiteSpace(WslDistroName))
        {
            SaveError = "The WSL distro name cannot be empty.";
            return;
        }

        var distroName = WslDistroName.Trim();
        if (!IsSafeDistroName(distroName))
        {
            SaveError = "The WSL distro name may only contain letters, digits, dot, underscore, and hyphen.";
            return;
        }

        CommitProfileEdits();
        if (Profiles.Count == 0)
        {
            SaveError = "Add at least one identity profile.";
            return;
        }

        // The user name and directory reach root shell scripts, so reject anything unsafe before it is
        // persisted or handed to the provisioner.
        var unsafeProfile = Profiles.FirstOrDefault(profile => !profile.IsShellSafe);
        if (unsafeProfile is not null)
        {
            SaveError = $"The \"{unsafeProfile.Name}\" profile has a Linux user, workspace, signing key name or email, or qwen path that is not allowed. Use a lowercase user name, a plain absolute path, a plain email address, and a key name without quotes or shell characters.";
            return;
        }

        var duplicate = Profiles.GroupBy(profile => profile.LinuxUser, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            SaveError = $"Two profiles both use the Linux user \"{duplicate.Key}\". Each identity needs its own user.";
            return;
        }

        _settings.WslDistroName = distroName;
        _settings.Profiles = [.. Profiles];
        _settings.ActiveProfileId = SelectedProfile?.Id ?? Profiles[0].Id;
        _settings.Host = Host.Trim();
        _settings.Port = port;
        _settings.Token = Token.Trim();
        _settings.RequireAuth = RequireAuth;
        _settings.AllowedOrigins = SplitLines(AllowedOrigins);
        _settings.TlsCertPath = TlsCertPath.Trim();
        _settings.TlsKeyPath = TlsKeyPath.Trim();
        _settings.EnableSessionShell = EnableSessionShell;
        _settings.ServeWeb = ServeWeb;
        _settings.LocalControl = LocalControl;
        UpdateLocalControlPairing(_supervisor.LocalControlPairingUrl);
        _settings.MaxSessions = ParseInt(MaxSessions, _settings.MaxSessions);
        _settings.MaxConnections = ParseInt(MaxConnections, _settings.MaxConnections);
        _settings.EventRingSize = ParseInt(EventRingSize, _settings.EventRingSize);
        _settings.RateLimit = RateLimit;
        _settings.DebugMode = DebugMode;
        _settings.HealthPollSeconds = Math.Clamp(ParseInt(HealthPollSeconds, _settings.HealthPollSeconds), 1, 60);
        _settings.AutoRestart = AutoRestart;
        _settings.CheckForUpdates = CheckForUpdates;
        _settings.MaxBackoffSeconds = Math.Max(1, ParseInt(MaxBackoffSeconds, _settings.MaxBackoffSeconds));
        _settings.ExtraArgs = SplitLines(ExtraArgs);
        _settings.SyncPaths = SplitLines(SyncEntries);

        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving settings failed.");
            SaveError = $"Could not write settings.json: {ex.Message}";
            return;
        }

        if (_autostart.IsEnabled() != StartOnLogin)
        {
            _autostart.SetEnabled(StartOnLogin);
            _logger.LogInformation("Start with Windows {State}.", StartOnLogin ? "enabled" : "disabled");
        }

        _logger.LogInformation("Settings saved. Daemon argument count={ArgumentCount} LocalControl={LocalControl}.", DaemonSupervisor.BuildArguments(_settings, _settings.WslDistroName, _settings.ResolveActiveProfile()).Count, _settings.LocalControl);

        var warnings = DaemonSupervisor.ValidateSettings(_settings).ToList();
        foreach (var warning in warnings)
        {
            _logger.LogWarning("Daemon settings warning: {Warning}", warning);
        }

        if (warnings.Count > 0)
        {
            SaveWarning = string.Join(" ", warnings);
        }

        if (_supervisor.State is DaemonState.Starting or DaemonState.Running)
        {
            SaveMessage = "Saved. Restarting the daemon to apply.";
            await _supervisor.RestartAsync();
        }
        else
        {
            SaveMessage = "Saved.";
        }

        ScheduleSaveMessageFadeOut();
    }

    private async Task RefreshEnvironmentAsync()
    {
        await RefreshDistroAsync();
        if (IsDistroProvisioned)
        {
            await LoadIdentityCoreAsync(announce: false);
        }
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

        CommitProfileEdits();
        _settings.Profiles = [.. Profiles];
        IsDistroBusy = true;
        DistroProgress = rebuild
            ? "Deleting the old distro and building a new one, then installing Qwen and the gh CLI. This takes a few minutes."
            : "Creating the distro and installing Qwen and the gh CLI. This takes a few minutes on first run.";
        try
        {
            await StopDaemonForDistroChangeAsync();
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
            await StopDaemonForDistroChangeAsync();
            await _provisioner.DeleteAsync(distro, CancellationToken.None);
            DistroExists = false;
            IsDistroProvisioned = false;
            DistroStatus = $"The \"{distro}\" distro has been deleted.";
            ClearIdentity();
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

    // wsl refuses to unregister a running distro, and a daemon writing into a distro that is about to
    // be deleted would lose its log the same way.
    private async Task StopDaemonForDistroChangeAsync()
    {
        if (_supervisor.State is DaemonState.Starting or DaemonState.Running)
        {
            await _supervisor.StopAsync();
        }
    }

    [RelayCommand]
    private void AddProfile()
    {
        var name = string.IsNullOrWhiteSpace(NewProfileName) ? $"Profile {Profiles.Count + 1}" : NewProfileName.Trim();
        var user = string.IsNullOrWhiteSpace(NewProfileUser) ? SlugifyLinuxUser(name) : NewProfileUser.Trim().ToLowerInvariant();
        if (!IsValidLinuxUser(user))
        {
            SaveError = $"\"{user}\" is not a valid Linux user name. Use lowercase letters, digits, underscore, or hyphen, and start with a letter.";
            return;
        }
        if (Profiles.Any(profile => string.Equals(profile.LinuxUser, user, StringComparison.Ordinal)))
        {
            SaveError = $"A profile already uses the Linux user \"{user}\".";
            return;
        }

        var id = SlugifyLinuxUser(name);
        if (Profiles.Any(profile => string.Equals(profile.Id, id, StringComparison.Ordinal))) id = $"{id}-{Profiles.Count + 1}";
        var created = new IdentityProfile { Id = id, Name = name, LinuxUser = user, WorkspaceDirectory = $"/home/{user}/workspace" };
        Profiles.Add(created);
        SelectedProfile = created;
        NewProfileName = string.Empty;
        NewProfileUser = string.Empty;
    }

    /// <summary>
    /// Clones the selected profile's settings into a new Linux user. Credentials are deliberately not
    /// copied: a profile with the same keys and token would be the same identity, not a second one.
    /// </summary>
    [RelayCommand]
    private void DuplicateProfile()
    {
        var source = SelectedProfile;
        if (source is null)
        {
            _snackbars.Show("Select a profile to duplicate first.", SnackbarKind.Warning);
            return;
        }

        var name = string.IsNullOrWhiteSpace(NewProfileName) ? $"{source.Name} copy" : NewProfileName.Trim();
        var user = string.IsNullOrWhiteSpace(NewProfileUser) ? SlugifyLinuxUser(name) : NewProfileUser.Trim().ToLowerInvariant();
        if (!IsValidLinuxUser(user))
        {
            SaveError = $"\"{user}\" is not a valid Linux user name. Use lowercase letters, digits, underscore, or hyphen, and start with a letter.";
            return;
        }
        if (Profiles.Any(profile => string.Equals(profile.LinuxUser, user, StringComparison.Ordinal)))
        {
            SaveError = $"A profile already uses the Linux user \"{user}\".";
            return;
        }

        var id = SlugifyLinuxUser(name);
        if (Profiles.Any(profile => string.Equals(profile.Id, id, StringComparison.Ordinal))) id = $"{id}-{Profiles.Count + 1}";
        var created = new IdentityProfile { Id = id, Name = name, LinuxUser = user, WorkspaceDirectory = $"/home/{user}/workspace" };
        Profiles.Add(created);
        SelectedProfile = created;
        NewProfileName = string.Empty;
        NewProfileUser = string.Empty;
        _snackbars.Show($"Created \"{name}\" from \"{source.Name}\". Its Linux user, keys, and token are empty until you set up the distro and save.", SnackbarKind.Success);
    }

    [RelayCommand]
    private void RemoveProfile()
    {
        if (SelectedProfile is null) return;
        if (Profiles.Count <= 1)
        {
            SaveError = "At least one profile is required.";
            return;
        }

        var index = Profiles.IndexOf(SelectedProfile);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles[Math.Clamp(index, 0, Profiles.Count - 1)];
    }

    [RelayCommand]
    private async Task CheckQwenUpdateAsync()
    {
        IsQwenBusy = true;
        QwenProgress = "Reading the installed and latest versions…";
        try
        {
            var distro = WslDistroName.Trim();
            CommitProfileEdits();
            var profile = SelectedProfile ?? _settings.ResolveActiveProfile();
            var installed = await _provisioner.ReadQwenVersionAsync(distro, profile, QwenExecutable.Trim(), CancellationToken.None);
            QwenVersion = installed ?? "Unknown";
            var latest = await _provisioner.ReadLatestQwenVersionAsync(distro, profile, CancellationToken.None);
            if (installed is null)
            {
                _snackbars.Show("Qwen is not installed for this profile yet. Choose Update Qwen to install it.", SnackbarKind.Warning);
            }
            else if (latest is null)
            {
                _snackbars.Show($"Installed version {installed}. The latest version could not be determined.", SnackbarKind.Warning);
            }
            else
            {
                var upToDate = string.Equals(installed, latest, StringComparison.Ordinal);
                _snackbars.Show(upToDate ? $"Version {installed} is up to date." : $"Installed {installed}, latest {latest}.", upToDate ? SnackbarKind.Success : SnackbarKind.Info);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checking the qwen version failed.");
            _snackbars.Show($"Could not check for updates: {ex.Message}", SnackbarKind.Error);
        }
        finally
        {
            QwenProgress = null;
            IsQwenBusy = false;
        }
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

    [RelayCommand]
    private async Task UpdateQwenAsync()
    {
        IsQwenBusy = true;
        QwenProgress = "Installing or updating Qwen in the distro…";
        try
        {
            var distro = WslDistroName.Trim();
            CommitProfileEdits();
            var profile = SelectedProfile ?? _settings.ResolveActiveProfile();
            var result = await _provisioner.InstallOrUpdateQwenAsync(distro, profile, CancellationToken.None);
            if (!result.Succeeded)
            {
                _snackbars.Show($"Updating Qwen failed: {result.Output}", SnackbarKind.Error);
                return;
            }

            var resolved = await _provisioner.ResolveQwenPathAsync(distro, profile, CancellationToken.None);
            if (resolved is not null) { profile.QwenExecutable = resolved; QwenExecutable = resolved; }
            QwenVersion = await _provisioner.ReadQwenVersionAsync(distro, profile, QwenExecutable.Trim(), CancellationToken.None) ?? "Unknown";
            _snackbars.Show($"Qwen is now version {QwenVersion}.", SnackbarKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Updating qwen failed.");
            _snackbars.Show($"Updating Qwen failed: {ex.Message}", SnackbarKind.Error);
        }
        finally
        {
            QwenProgress = null;
            IsQwenBusy = false;
        }
    }

    /// <summary>
    /// Installs one developer tool into the distro as root. The tools are shared by every profile, so
    /// this does not depend on which one is selected.
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
            var result = await _devTools.InstallAsync(distro, tool, CancellationToken.None);
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

    [RelayCommand]
    private Task LoadIdentityAsync() => LoadIdentityCoreAsync(announce: true);

    private async Task LoadIdentityCoreAsync(bool announce)
    {
        var distro = WslDistroName.Trim();
        var profile = SelectedProfile ?? _settings.ResolveActiveProfile();
        IsIdentityBusy = true;
        try
        {
            SshPublicKey = await _identity.ReadPublicKeyAsync(distro, profile, CancellationToken.None) ?? string.Empty;
            HasSshPublicKey = SshPublicKey.Length > 0;
            GpgPublicKey = await _identity.ReadGpgPublicKeyAsync(distro, profile, CancellationToken.None) ?? string.Empty;
            HasGpgPublicKey = GpgPublicKey.Length > 0;
            var status = await _identity.GetGitHubAuthStatusAsync(distro, profile, CancellationToken.None);
            GitHubAccountText = status.IsAuthenticated ? $"Signed in as {status.Account}." : "Not signed in.";

            if (announce)
            {
                _snackbars.Show(HasIdentityKeys
                    ? $"Retrieved the keys for the \"{profile.Name}\" profile."
                    : $"The \"{profile.Name}\" profile has no keys yet.", HasIdentityKeys ? SnackbarKind.Success : SnackbarKind.Info);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading the identity for {Profile} failed.", profile.Name);
            _snackbars.Show($"Could not read the identity: {ex.Message}", SnackbarKind.Error);
        }
        finally
        {
            IsIdentityBusy = false;
        }
    }

    private void ClearIdentity()
    {
        SshPublicKey = string.Empty;
        HasSshPublicKey = false;
        GpgPublicKey = string.Empty;
        HasGpgPublicKey = false;
        GitHubAccountText = "Not signed in.";
    }

    /// <summary>The distro and profile a credential command acts on, or nothing if the name is missing.</summary>
    private (string? Distro, IdentityProfile Profile) CurrentIdentityTarget()
    {
        var distro = WslDistroName.Trim();
        if (string.IsNullOrWhiteSpace(distro))
        {
            _snackbars.Show("Set the WSL distro name first.", SnackbarKind.Error);
            return (null, _settings.ResolveActiveProfile());
        }

        return (distro, SelectedProfile ?? _settings.ResolveActiveProfile());
    }

    [RelayCommand]
    private async Task GenerateSshKeyAsync()
    {
        var (distro, profile) = CurrentIdentityTarget();
        if (distro is null) return;
        try
        {
            await _identity.EnsureSshKeyAsync(distro, profile, CancellationToken.None);
            await LoadIdentityCoreAsync(announce: false);
            _snackbars.Show($"Created an ssh key for the \"{profile.Name}\" profile.", SnackbarKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generating the ssh key failed.");
            _snackbars.Show($"Could not generate the ssh key: {ex.Message}", SnackbarKind.Error);
        }
    }

    [RelayCommand]
    private async Task RegenerateSshKeyAsync()
    {
        var (distro, profile) = CurrentIdentityTarget();
        if (distro is null) return;
        var confirmed = await _confirm.ConfirmAsync(new ConfirmDialogRequest(
            "Regenerate the ssh key?",
            $"The \"{profile.Name}\" profile's current ssh keypair is deleted and a new one is created.",
            "Regenerate",
            "A public key you already added to GitHub keeps working there until you remove it by hand, but it no longer matches the private key on this machine."));
        if (!confirmed) return;

        try
        {
            await _identity.RegenerateSshKeyAsync(distro, profile, CancellationToken.None);
            await LoadIdentityCoreAsync(announce: false);
            _snackbars.Show($"Replaced the ssh key for the \"{profile.Name}\" profile.", SnackbarKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Regenerating the ssh key failed.");
            _snackbars.Show($"Could not regenerate the ssh key: {ex.Message}", SnackbarKind.Error);
        }
    }

    [RelayCommand]
    private async Task DeleteSshKeyAsync()
    {
        var (distro, profile) = CurrentIdentityTarget();
        if (distro is null) return;
        var confirmed = await _confirm.ConfirmAsync(new ConfirmDialogRequest(
            "Delete the ssh key?",
            $"Both halves of the \"{profile.Name}\" profile's ssh keypair are removed from its home directory.",
            "Delete",
            "The private key is gone for good, and any service still trusting the public key keeps trusting it until you remove it there."));
        if (!confirmed) return;

        try
        {
            await _identity.DeleteSshKeyAsync(distro, profile, CancellationToken.None);
            await LoadIdentityCoreAsync(announce: false);
            _snackbars.Show($"Deleted the ssh key for the \"{profile.Name}\" profile.", SnackbarKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deleting the ssh key failed.");
            _snackbars.Show($"Could not delete the ssh key: {ex.Message}", SnackbarKind.Error);
        }
    }

    [RelayCommand]
    private async Task GenerateGpgKeyAsync()
    {
        var (distro, profile) = CurrentIdentityTarget();
        if (distro is null) return;
        try
        {
            await _identity.EnsureGpgKeyAsync(distro, profile, CancellationToken.None);
            await LoadIdentityCoreAsync(announce: false);
            _snackbars.Show($"Created a signing key for the \"{profile.Name}\" profile.", SnackbarKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generating the signing key failed.");
            _snackbars.Show($"Could not generate the signing key: {ex.Message}", SnackbarKind.Error);
        }
    }

    [RelayCommand]
    private async Task RegenerateGpgKeyAsync()
    {
        var (distro, profile) = CurrentIdentityTarget();
        if (distro is null) return;
        var confirmed = await _confirm.ConfirmAsync(new ConfirmDialogRequest(
            "Regenerate the signing key?",
            $"The \"{profile.Name}\" profile's current signing key is deleted from its keyring and a new one is created.",
            "Regenerate",
            "Commits already signed with the old key keep their signature but no longer verify against this profile's new key."));
        if (!confirmed) return;

        try
        {
            await _identity.RegenerateGpgKeyAsync(distro, profile, CancellationToken.None);
            await LoadIdentityCoreAsync(announce: false);
            _snackbars.Show($"Replaced the signing key for the \"{profile.Name}\" profile.", SnackbarKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Regenerating the signing key failed.");
            _snackbars.Show($"Could not regenerate the signing key: {ex.Message}", SnackbarKind.Error);
        }
    }

    [RelayCommand]
    private async Task DeleteGpgKeyAsync()
    {
        var (distro, profile) = CurrentIdentityTarget();
        if (distro is null) return;
        var confirmed = await _confirm.ConfirmAsync(new ConfirmDialogRequest(
            "Delete the signing key?",
            $"The \"{profile.Name}\" profile's signing key is removed from its keyring, secret half included.",
            "Delete",
            "Without the secret half nothing can be signed as this profile again, and the key cannot be recovered."));
        if (!confirmed) return;

        try
        {
            await _identity.DeleteGpgKeyAsync(distro, profile, CancellationToken.None);
            await LoadIdentityCoreAsync(announce: false);
            _snackbars.Show($"Deleted the signing key for the \"{profile.Name}\" profile.", SnackbarKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deleting the signing key failed.");
            _snackbars.Show($"Could not delete the signing key: {ex.Message}", SnackbarKind.Error);
        }
    }

    [RelayCommand]
    private async Task CopySshKeyAsync()
    {
        if (!HasSshPublicKey) return;
        if (await CopyToClipboardAsync(SshPublicKey))
        {
            _snackbars.Show("Public key copied to the clipboard.", SnackbarKind.Success);
        }
    }

    [RelayCommand]
    private async Task CopyGpgKeyAsync()
    {
        if (!HasGpgPublicKey) return;
        if (await CopyToClipboardAsync(GpgPublicKey))
        {
            _snackbars.Show("Public key copied to the clipboard.", SnackbarKind.Success);
        }
    }

    private static async Task<bool> CopyToClipboardAsync(string text)
    {
        var clipboard = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;
        if (clipboard is null) return false;
        await clipboard.SetTextAsync(text);
        return true;
    }

    [RelayCommand]
    private async Task SaveGitHubTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(GitHubToken))
        {
            _snackbars.Show("Paste a token first.", SnackbarKind.Warning);
            return;
        }

        var (distro, profile) = CurrentIdentityTarget();
        if (distro is null) return;
        IsGitHubBusy = true;
        try
        {
            await _identity.SetGitHubTokenAsync(distro, profile, GitHubToken, CancellationToken.None);
            GitHubToken = string.Empty;
            var status = await _identity.GetGitHubAuthStatusAsync(distro, profile, CancellationToken.None);
            GitHubAccountText = status.IsAuthenticated ? $"Signed in as {status.Account}." : "The token was stored but gh did not report a signed-in account.";
            _snackbars.Show($"Stored the token for the \"{profile.Name}\" profile. {GitHubAccountText}", SnackbarKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Storing the GitHub token failed.");
            _snackbars.Show($"Could not store the token: {ex.Message}", SnackbarKind.Error);
        }
        finally
        {
            IsGitHubBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignOutGitHubAsync()
    {
        var (distro, profile) = CurrentIdentityTarget();
        if (distro is null) return;
        IsGitHubBusy = true;
        try
        {
            await _identity.ClearGitHubTokenAsync(distro, profile, CancellationToken.None);
            GitHubAccountText = "Not signed in.";
            _snackbars.Show($"Signed out of GitHub for the \"{profile.Name}\" profile.", SnackbarKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Signing out of GitHub failed.");
            _snackbars.Show($"Could not sign out: {ex.Message}", SnackbarKind.Error);
        }
        finally
        {
            IsGitHubBusy = false;
        }
    }

    [RelayCommand]
    private async Task PushConfigAsync() => await SyncConfigAsync(push: true);

    [RelayCommand]
    private async Task PullConfigAsync() => await SyncConfigAsync(push: false);

    private async Task SyncConfigAsync(bool push)
    {
        var (distro, profile) = CurrentIdentityTarget();
        if (distro is null) return;
        var entries = SplitLines(SyncEntries);
        IsSyncing = true;
        try
        {
            var result = push
                ? await _sync.PushAsync(distro, profile, entries, CancellationToken.None)
                : await _sync.PullAsync(distro, profile, entries, CancellationToken.None);
            var skipped = result.Skipped.Length > 0 ? $" Skipped: {string.Join(", ", result.Skipped)}." : string.Empty;
            _snackbars.Show($"{(push ? "Pushed" : "Pulled")} {result.FilesCopied} file(s) {(push ? "to" : "from")} {profile.Name}.{skipped}", SnackbarKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Syncing the Qwen configuration failed.");
            _snackbars.Show($"Sync failed: {ex.Message}", SnackbarKind.Error);
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private void LoadProfiles()
    {
        Profiles.Clear();
        if (_settings.Profiles.Length == 0) _settings.Profiles = [Settings.CreateDefaultProfile()];
        foreach (var profile in _settings.Profiles) Profiles.Add(profile);
        SelectedProfile = _settings.ResolveActiveProfile();
    }

    private void CommitProfileEdits()
    {
        if (SelectedProfile is null) return;
        var name = ProfileName.Trim();
        var user = ProfileLinuxUser.Trim().ToLowerInvariant();
        var qwen = QwenExecutable.Trim();
        if (name.Length > 0) SelectedProfile.Name = name;
        if (user.Length > 0) SelectedProfile.LinuxUser = user;
        if (ProfileWorkspace.Trim().Length > 0) SelectedProfile.WorkspaceDirectory = ProfileWorkspace.Trim();
        if (qwen.Length > 0) SelectedProfile.QwenExecutable = qwen;
        // Clearing the field restores the default email, so it is assigned even when empty.
        SelectedProfile.Email = ProfileEmail.Trim();
        SelectedProfile.GpgName = ProfileGpgName.Trim();
    }

    partial void OnSelectedProfileChanged(IdentityProfile? value)
    {
        if (value is null) return;
        ProfileName = value.Name;
        ProfileLinuxUser = value.LinuxUser;
        ProfileWorkspace = value.WorkspaceDirectory;
        QwenExecutable = value.ResolvedQwenExecutable;
        ProfileEmail = value.ResolvedEmail;
        ProfileGpgName = value.ResolvedGpgName;

        // The keys belong to the profile, so switching identities has to re-read them. The first switch
        // happens while the page is still loading, before the distro is known, and the refresh started
        // there covers it.
        if (HasCheckedDistro && IsDistroProvisioned)
        {
            _ = LoadIdentityCoreAsync(announce: false);
        }
    }

    private void ScheduleSaveMessageFadeOut()
    {
        _saveMessageFadeOut?.Cancel();
        var fadeOut = _saveMessageFadeOut = new CancellationTokenSource();
        SaveMessageOpacity = 1d;
        _ = FadeOutSaveMessageAsync(fadeOut.Token);
    }

    private async Task FadeOutSaveMessageAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SaveMessageHold, cancellationToken);
            SaveMessageOpacity = 0d;
            await Task.Delay(SaveMessageFade, cancellationToken);
            SaveMessage = null;
        }
        catch (OperationCanceledException)
        {
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

    private void OnLocalControlPairingUrlChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() => UpdateLocalControlPairing(_supervisor.LocalControlPairingUrl));
    }

    private void UpdateLocalControlPairing(string? url)
    {
        ShowLocalControlPairingPending = LocalControl && string.Equals(Host.Trim(), "127.0.0.1", StringComparison.OrdinalIgnoreCase) && ServeWeb && string.IsNullOrWhiteSpace(url);
        LocalControlPairingQr?.Dispose();
        LocalControlPairingQr = null;
        HasLocalControlPairingQr = false;
        if (!LocalControl || !string.Equals(Host.Trim(), "127.0.0.1", StringComparison.OrdinalIgnoreCase) || !ServeWeb || string.IsNullOrWhiteSpace(url)) return;

        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
            var png = new PngByteQRCode(data).GetGraphic(8);
            using var stream = new MemoryStream(png);
            LocalControlPairingQr = new Bitmap(stream);
            HasLocalControlPairingQr = true;
            ShowLocalControlPairingPending = false;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not render the local device pairing QR code.");
            ShowLocalControlPairingPending = false;
        }
    }

    private string[] EffectiveSyncPaths() => _settings.SyncPaths.Length > 0 ? _settings.SyncPaths : Settings.DefaultSyncPaths;

    private static int ParseInt(string? text, int fallback) => int.TryParse((text ?? string.Empty).Trim(), out var value) ? value : fallback;

    private static string[] SplitLines(string? text) =>
        (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Linux user names are lowercase and may not start with a digit or hyphen.</summary>
    internal static bool IsValidLinuxUser(string user) => IdentityProfile.IsValidLinuxUser(user);

    internal static bool IsSafeDistroName(string name) => DistroName.IsMatch(name);

    internal static string SlugifyLinuxUser(string value)
    {
        var lowered = value.Trim().ToLowerInvariant();
        var cleaned = new string([.. lowered.Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_')]);
        if (cleaned.Length == 0 || (!char.IsAsciiLetter(cleaned[0]) && cleaned[0] != '_')) cleaned = "user" + cleaned;
        return cleaned.Length > 32 ? cleaned[..32] : cleaned;
    }
}
