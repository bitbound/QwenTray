using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Bitbound.SystemAbstractions.FileSystem;
using Bitbound.SystemAbstractions.Processes;
using Microsoft.Extensions.Logging;
using QRCoder;
using QwenTray.Services;
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
    private readonly IFileSystem _fileSystem;
    private readonly IProcessManager _processes;
    private readonly ILogger<SettingsViewModel> _logger;

    [ObservableProperty]
    private string _wslDistroName = string.Empty;

    public ObservableCollection<IdentityProfile> Profiles { get; } = [];

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
    private string _distroStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DistroIsReady))]
    [NotifyPropertyChangedFor(nameof(DistroNeedsAttention))]
    [NotifyPropertyChangedFor(nameof(DistroIsChecking))]
    [NotifyPropertyChangedFor(nameof(DistroBannerTitle))]
    [NotifyPropertyChangedFor(nameof(DistroNextStep))]
    [NotifyPropertyChangedFor(nameof(DistroStatusIconKey))]
    private bool _isDistroProvisioned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DistroIsReady))]
    [NotifyPropertyChangedFor(nameof(DistroNeedsAttention))]
    [NotifyPropertyChangedFor(nameof(DistroIsChecking))]
    [NotifyPropertyChangedFor(nameof(DistroBannerTitle))]
    [NotifyPropertyChangedFor(nameof(DistroNextStep))]
    [NotifyPropertyChangedFor(nameof(DistroStatusIconKey))]
    private bool _isDistroBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DistroIsReady))]
    [NotifyPropertyChangedFor(nameof(DistroNeedsAttention))]
    [NotifyPropertyChangedFor(nameof(DistroIsChecking))]
    [NotifyPropertyChangedFor(nameof(DistroBannerTitle))]
    [NotifyPropertyChangedFor(nameof(DistroNextStep))]
    [NotifyPropertyChangedFor(nameof(DistroStatusIconKey))]
    private bool _hasCheckedDistro;

    [ObservableProperty]
    private string _qwenVersion = "Unknown";

    [ObservableProperty]
    private bool _isQwenBusy;

    [ObservableProperty]
    private string? _qwenStatus;

    [ObservableProperty]
    private bool _qwenStatusIsError;

    [ObservableProperty]
    private string _sshPublicKey = string.Empty;

    [ObservableProperty]
    private bool _hasSshPublicKey;

    [ObservableProperty]
    private string? _sshStatus;

    [ObservableProperty]
    private string _gitHubToken = string.Empty;

    [ObservableProperty]
    private string _gitHubStatus = "Unknown";

    [ObservableProperty]
    private bool _isGitHubBusy;

    [ObservableProperty]
    private string _syncEntries = string.Empty;

    [ObservableProperty]
    private string? _syncStatus;

    [ObservableProperty]
    private bool _isSyncing;

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

    [ObservableProperty]
    private string? _publisherTrustMessage;

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
        IFileSystem fileSystem,
        IProcessManager processes,
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
        _fileSystem = fileSystem;
        _processes = processes;
        _logger = logger;
        _supervisor.LocalControlPairingUrlChanged += OnLocalControlPairingUrlChanged;
    }

    public string SettingsFilePath => SettingsStore.FilePath;

    public string LogFilePath => SettingsStore.LogFilePath;

    public bool DistroIsChecking => !HasCheckedDistro || IsDistroBusy;

    public bool DistroIsReady => HasCheckedDistro && IsDistroProvisioned && !IsDistroBusy;

    public bool DistroNeedsAttention => HasCheckedDistro && !IsDistroProvisioned && !IsDistroBusy;

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
        MaxBackoffSeconds = _settings.MaxBackoffSeconds.ToString();
        ExtraArgs = string.Join(Environment.NewLine, _settings.ExtraArgs);
        SyncEntries = string.Join(Environment.NewLine, EffectiveSyncPaths());
        StartOnLogin = _autostart.IsEnabled();
        UpdateLocalControlPairing(_supervisor.LocalControlPairingUrl);
        RefreshPublisherTrustState();

        // Surface the Linux environment state without waiting on wsl.exe so the page opens instantly.
        _ = RefreshDistroAsync();

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
            SaveError = $"The \"{unsafeProfile.Name}\" profile has a Linux user or directory that is not allowed. Use a lowercase user name and a plain absolute path.";
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

    [RelayCommand]
    private async Task RefreshDistroAsync()
    {
        IsDistroBusy = true;
        try
        {
            var distro = WslDistroName.Trim();
            var exists = await _wsl.DistroExistsAsync(distro, CancellationToken.None);
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
            IsDistroProvisioned = false;
        }
        finally
        {
            HasCheckedDistro = true;
            IsDistroBusy = false;
        }
    }

    [RelayCommand]
    private async Task SetupDistroAsync()
    {
        var distro = WslDistroName.Trim();
        if (string.IsNullOrWhiteSpace(distro))
        {
            SaveError = "Set the WSL distro name first.";
            return;
        }

        CommitProfileEdits();
        _settings.Profiles = [.. Profiles];
        IsDistroBusy = true;
        QwenStatusIsError = false;
        QwenStatus = "Creating the distro and installing Qwen and the gh CLI. This takes a few minutes on first run.";
        try
        {
            _provisioner.WriteWslConfig();
            await _provisioner.ProvisionAsync(distro, _settings.Profiles, CancellationToken.None);
            var resolved = await _provisioner.ResolveQwenPathAsync(distro, _settings.ResolveActiveProfile(), CancellationToken.None);
            if (resolved is not null) _settings.ResolveActiveProfile().QwenExecutable = resolved;
            IsDistroProvisioned = true;
            DistroStatus = $"The \"{distro}\" distro is set up.";
            QwenStatus = "Setup complete. Sign in to Windows again or run 'wsl --shutdown' so the mirrored networking change takes effect.";
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Provisioning failed.");
            QwenStatusIsError = true;
            QwenStatus = $"Setup failed: {ex.Message}";
            DistroStatus = "Setup did not complete.";
        }
        finally
        {
            HasCheckedDistro = true;
            IsDistroBusy = false;
        }
    }

    [RelayCommand]
    private void OpenInWindowsTerminal()
    {
        var distro = WslDistroName.Trim();
        var profile = _settings.ResolveActiveProfile();
        if (!IsSafeDistroName(distro) || !IdentityProfile.IsValidLinuxUser(profile.LinuxUser))
        {
            _logger.LogWarning("Cannot open a terminal for distro {Distro} and user {User}: the names are not valid.", distro, profile.LinuxUser);
            DistroStatus = "Fix the distro name or Linux user before opening a terminal.";
            return;
        }

        try
        {
            _processes.Start("wt.exe", $"wsl.exe -d {distro} -u {profile.LinuxUser}", useShellExec: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Windows Terminal could not be started; falling back to wsl.exe.");
            _processes.Start("wsl.exe", $"-d {distro} -u {profile.LinuxUser}", useShellExec: true);
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
        try
        {
            var distro = WslDistroName.Trim();
            CommitProfileEdits();
            var profile = SelectedProfile ?? _settings.ResolveActiveProfile();
            var installed = await _provisioner.ReadQwenVersionAsync(distro, profile, QwenExecutable.Trim(), CancellationToken.None);
            QwenVersion = installed ?? "Unknown";
            var latest = await ReadLatestQwenVersionAsync(distro, profile);
            QwenStatusIsError = false;
            QwenStatus = installed is null
                ? "Qwen is not installed in this profile yet. Choose Update Qwen to install it."
                : latest is null
                    ? $"Installed version {installed}. Latest version could not be determined."
                    : string.Equals(installed, latest, StringComparison.Ordinal) ? $"Installed version {installed} is up to date." : $"Installed {installed}, latest {latest}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checking the qwen version failed.");
            QwenStatusIsError = true;
            QwenStatus = $"Could not check for updates: {ex.Message}";
        }
        finally
        {
            IsQwenBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpdateQwenAsync()
    {
        IsQwenBusy = true;
        QwenStatus = null;
        QwenStatusIsError = false;
        try
        {
            var distro = WslDistroName.Trim();
            CommitProfileEdits();
            var profile = SelectedProfile ?? _settings.ResolveActiveProfile();
            var result = await _provisioner.InstallOrUpdateQwenAsync(distro, profile, CancellationToken.None);
            if (!result.Succeeded)
            {
                QwenStatusIsError = true;
                QwenStatus = $"Updating Qwen failed: {result.Output}";
                return;
            }

            var resolved = await _provisioner.ResolveQwenPathAsync(distro, profile, CancellationToken.None);
            if (resolved is not null) { profile.QwenExecutable = resolved; QwenExecutable = resolved; }
            QwenVersion = await _provisioner.ReadQwenVersionAsync(distro, profile, QwenExecutable.Trim(), CancellationToken.None) ?? "Unknown";
            QwenStatus = $"Qwen is now version {QwenVersion}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Updating qwen failed.");
            QwenStatusIsError = true;
            QwenStatus = $"Updating Qwen failed: {ex.Message}";
        }
        finally
        {
            IsQwenBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadIdentityAsync()
    {
        var distro = WslDistroName.Trim();
        var profile = SelectedProfile ?? _settings.ResolveActiveProfile();
        try
        {
            SshPublicKey = await _identity.ReadPublicKeyAsync(distro, profile, CancellationToken.None) ?? string.Empty;
            HasSshPublicKey = SshPublicKey.Length > 0;
            SshStatus = HasSshPublicKey ? null : "No ssh key yet for this profile. Choose Generate key.";
            var status = await _identity.GetGitHubAuthStatusAsync(distro, profile, CancellationToken.None);
            GitHubStatus = status.IsAuthenticated ? $"Signed in as {status.Account}." : "Not signed in.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading the identity for {Profile} failed.", profile.Name);
            SshStatus = $"Could not read the identity: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task GenerateSshKeyAsync()
    {
        var distro = WslDistroName.Trim();
        var profile = SelectedProfile ?? _settings.ResolveActiveProfile();
        try
        {
            await _identity.EnsureSshKeyAsync(distro, profile, CancellationToken.None);
            await LoadIdentityAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generating the ssh key failed.");
            SshStatus = $"Could not generate the ssh key: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CopySshKeyAsync()
    {
        if (!HasSshPublicKey) return;
        var clipboard = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(SshPublicKey);
        SshStatus = "Public key copied to the clipboard.";
    }

    [RelayCommand]
    private async Task SaveGitHubTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(GitHubToken))
        {
            GitHubStatus = "Paste a token first.";
            return;
        }

        var distro = WslDistroName.Trim();
        var profile = SelectedProfile ?? _settings.ResolveActiveProfile();
        IsGitHubBusy = true;
        try
        {
            await _identity.SetGitHubTokenAsync(distro, profile, GitHubToken, CancellationToken.None);
            GitHubToken = string.Empty;
            var status = await _identity.GetGitHubAuthStatusAsync(distro, profile, CancellationToken.None);
            GitHubStatus = status.IsAuthenticated ? $"Signed in as {status.Account}." : "The token was stored but gh did not report a signed-in account.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Storing the GitHub token failed.");
            GitHubStatus = $"Could not store the token: {ex.Message}";
        }
        finally
        {
            IsGitHubBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignOutGitHubAsync()
    {
        var distro = WslDistroName.Trim();
        var profile = SelectedProfile ?? _settings.ResolveActiveProfile();
        IsGitHubBusy = true;
        try
        {
            await _identity.ClearGitHubTokenAsync(distro, profile, CancellationToken.None);
            GitHubStatus = "Signed out.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Signing out of GitHub failed.");
            GitHubStatus = $"Could not sign out: {ex.Message}";
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
        var distro = WslDistroName.Trim();
        var profile = SelectedProfile ?? _settings.ResolveActiveProfile();
        var entries = SplitLines(SyncEntries);
        IsSyncing = true;
        SyncStatus = null;
        try
        {
            var result = push
                ? await _sync.PushAsync(distro, profile, entries, CancellationToken.None)
                : await _sync.PullAsync(distro, profile, entries, CancellationToken.None);
            var skipped = result.Skipped.Length > 0 ? $" Skipped: {string.Join(", ", result.Skipped)}." : string.Empty;
            SyncStatus = $"{(push ? "Pushed" : "Pulled")} {result.FilesCopied} file(s) {(push ? "to" : "from")} {profile.Name}.{skipped}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Syncing the Qwen configuration failed.");
            SyncStatus = $"Sync failed: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private async Task<string?> ReadLatestQwenVersionAsync(string distro, IdentityProfile profile)
    {
        var result = await _wsl.RunScriptAsync(distro, profile.LinuxUser, "npm view @qwen-code/qwen-code version 2>/dev/null", CancellationToken.None);
        return result.Succeeded ? WslDistro.ParseVersion(result.Output) : null;
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
    }

    partial void OnSelectedProfileChanged(IdentityProfile? value)
    {
        if (value is null) return;
        ProfileName = value.Name;
        ProfileLinuxUser = value.LinuxUser;
        ProfileWorkspace = value.WorkspaceDirectory;
        QwenExecutable = value.ResolvedQwenExecutable;
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
            PublisherTrustMessage = $"Could not read the certificate stores: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task TrustPublisherAsync()
    {
        PublisherTrustMessage = null;
        IsTrustingPublisher = true;

        try
        {
            // Certificate store writes can hit slow group-policy callbacks, so keep the UI free.
            await Task.Run(_certTrust.TrustPublisher);

            IsPublisherTrusted = true;
            PublisherTrustMessage = "Bitbound is now trusted for this user account. Already-downloaded copies of QwenTray show Bitbound as the verified publisher.";
        }
        catch (Exception ex)
        {
            PublisherTrustMessage = $"Could not trust the certificate: {ex.Message}";
        }
        finally
        {
            IsTrustingPublisher = false;
        }
    }

    [RelayCommand]
    private void OpenLogFile()
    {
        var path = _supervisor.LogPath;
        if (string.IsNullOrWhiteSpace(path) || !_fileSystem.FileExists(path))
        {
            SaveWarning = "No daemon log is known yet. The path is captured once the daemon starts.";
            return;
        }

        _processes.Start(path, string.Empty, useShellExec: true);
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
