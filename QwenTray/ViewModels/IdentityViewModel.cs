using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Microsoft.Extensions.Logging;
using QwenTray.Services;
using QwenTray.Views;

namespace QwenTray.ViewModels;

/// <summary>
/// The identity profiles, the credentials and keys that make each one distinct, and the backup of the
/// Qwen configuration in the active profile's home directory.
/// </summary>
public sealed partial class IdentityViewModel : ViewModelBase<IdentityView>
{
    private readonly Settings _settings;
    private readonly IdentityService _identity;
    private readonly QwenConfigArchiveService _archive;
    private readonly WslDistroProvisioner _provisioner;
    private readonly EnvironmentViewModel _environment;
    private readonly SettingsSaveViewModel _save;
    private readonly ISnackbarService _snackbars;
    private readonly IConfirmDialogService _confirm;
    private readonly ILogger<IdentityViewModel> _logger;

    private bool _distroProvisioned;

    public ObservableCollection<IdentityProfile> Profiles { get; } = [];

    /// <summary>The save footer this page hosts, shared with every other settings page.</summary>
    public SettingsSaveViewModel Save => _save;

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
    [NotifyPropertyChangedFor(nameof(CanManageGitHub))]
    private bool _isGitHubBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageIdentity))]
    private bool _isIdentityBusy;

    [ObservableProperty]
    private string _configPaths = string.Empty;

    [ObservableProperty]
    private string _configArchivePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanArchiveConfig))]
    private bool _isArchiving;

    public IdentityViewModel(
        Settings settings,
        IdentityService identity,
        QwenConfigArchiveService archive,
        WslDistroProvisioner provisioner,
        EnvironmentViewModel environment,
        SettingsSaveViewModel save,
        ISnackbarService snackbars,
        IConfirmDialogService confirm,
        ILogger<IdentityViewModel> logger)
    {
        _settings = settings;
        _identity = identity;
        _archive = archive;
        _provisioner = provisioner;
        _environment = environment;
        _save = save;
        _snackbars = snackbars;
        _confirm = confirm;
        _logger = logger;

        // A backup holds the distro from outside it, so every command on this page has to wait for it.
        _environment.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(EnvironmentViewModel.IsBackupBusy)) return;
            OnPropertyChanged(nameof(CanManageIdentity));
            OnPropertyChanged(nameof(CanManageGitHub));
            OnPropertyChanged(nameof(CanArchiveConfig));
        };

        // The shared save reads the profile list and the backup paths even when the user never opened
        // this page, so the singleton has to carry them from construction.
        LoadProfiles();
        ConfigPaths = string.Join(Environment.NewLine, EffectiveConfigPaths());
        ConfigArchivePath = QwenConfigArchiveService.DefaultArchivePath(_settings.ConfigArchiveDirectory);
    }

    public bool CanManageIdentity => !IsIdentityBusy && !_environment.IsBackupBusy;

    public bool CanManageGitHub => !IsGitHubBusy && !_environment.IsBackupBusy;

    public bool CanArchiveConfig => !IsArchiving && !_environment.IsBackupBusy;

    public bool HasIdentityKeys => HasSshPublicKey || HasGpgPublicKey;

    private string DistroName => _settings.WslDistroName.Trim();

    protected override async Task OnInitializeAsync()
    {
        LoadProfiles();
        ConfigPaths = string.Join(Environment.NewLine, EffectiveConfigPaths());
        // A typed path survives a revisit, while an emptied field gets a fresh name in the stored folder.
        if (string.IsNullOrWhiteSpace(ConfigArchivePath))
        {
            ConfigArchivePath = QwenConfigArchiveService.DefaultArchivePath(_settings.ConfigArchiveDirectory);
        }

        // Reading the keys boots the distro, which a running backup would be doing something else with.
        if (_environment.IsBackupBusy) return;

        try
        {
            _distroProvisioned = await _provisioner.IsConfiguredAsync(DistroName, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checking the distro failed.");
            _distroProvisioned = false;
        }

        if (_distroProvisioned)
        {
            await LoadIdentityCoreAsync(announce: false);
        }
        else
        {
            ClearIdentity();
        }
    }

    [RelayCommand]
    private void AddProfile()
    {
        var name = string.IsNullOrWhiteSpace(NewProfileName) ? $"Profile {Profiles.Count + 1}" : NewProfileName.Trim();
        var user = string.IsNullOrWhiteSpace(NewProfileUser)
            ? IdentityProfile.SlugifyLinuxUser(name)
            : NewProfileUser.Trim().ToLowerInvariant();
        if (!IdentityProfile.IsValidLinuxUser(user))
        {
            _save.ReportError($"\"{user}\" is not a valid Linux user name. Use lowercase letters, digits, underscore, or hyphen, and start with a letter.");
            return;
        }
        if (Profiles.Any(profile => string.Equals(profile.LinuxUser, user, StringComparison.Ordinal)))
        {
            _save.ReportError($"A profile already uses the Linux user \"{user}\".");
            return;
        }

        var id = IdentityProfile.SlugifyLinuxUser(name);
        if (Profiles.Any(profile => string.Equals(profile.Id, id, StringComparison.Ordinal)))
        {
            id = $"{id}-{Profiles.Count + 1}";
        }
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
        var user = string.IsNullOrWhiteSpace(NewProfileUser)
            ? IdentityProfile.SlugifyLinuxUser(name)
            : NewProfileUser.Trim().ToLowerInvariant();
        if (!IdentityProfile.IsValidLinuxUser(user))
        {
            _save.ReportError($"\"{user}\" is not a valid Linux user name. Use lowercase letters, digits, underscore, or hyphen, and start with a letter.");
            return;
        }
        if (Profiles.Any(profile => string.Equals(profile.LinuxUser, user, StringComparison.Ordinal)))
        {
            _save.ReportError($"A profile already uses the Linux user \"{user}\".");
            return;
        }

        var id = IdentityProfile.SlugifyLinuxUser(name);
        if (Profiles.Any(profile => string.Equals(profile.Id, id, StringComparison.Ordinal)))
        {
            id = $"{id}-{Profiles.Count + 1}";
        }
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
            _save.ReportError("At least one profile is required.");
            return;
        }

        var index = Profiles.IndexOf(SelectedProfile);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles[Math.Clamp(index, 0, Profiles.Count - 1)];
    }

    [RelayCommand]
    private Task LoadIdentityAsync() => LoadIdentityCoreAsync(announce: true);

    private async Task LoadIdentityCoreAsync(bool announce)
    {
        var distro = DistroName;
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

    internal void ClearIdentity()
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
        var distro = DistroName;
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
        var clipboard = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow?.Clipboard;
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
    private async Task ExportConfigAsync()
    {
        var (distro, profile) = CurrentIdentityTarget();
        if (distro is null) return;

        var archivePath = ConfigArchivePath.Trim();
        if (archivePath.Length == 0)
        {
            _snackbars.Show("Set an archive path first.", SnackbarKind.Error);
            return;
        }

        IsArchiving = true;
        try
        {
            var result = await _archive.ExportAsync(distro, profile, SplitLines(ConfigPaths), archivePath, CancellationToken.None);
            // The next export gets a fresh timestamp rather than landing on the one just written.
            ConfigArchivePath = QwenConfigArchiveService.DefaultArchivePath(_settings.ConfigArchiveDirectory);

            var notes = new List<string>();
            if (result.Missing.Length > 0) notes.Add($"Not found: {string.Join(", ", result.Missing)}.");
            if (result.Invalid.Length > 0) notes.Add($"Not absolute Linux paths: {string.Join(", ", result.Invalid)}.");
            var note = string.Join(" ", notes);
            _snackbars.Show(
                $"Wrote {result.FilesWritten} file(s) from the \"{profile.Name}\" profile to {result.ArchivePath}.{(note.Length > 0 ? " " + note : string.Empty)}",
                result.FilesWritten > 0 ? SnackbarKind.Success : SnackbarKind.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exporting the Qwen configuration failed.");
            _snackbars.Show($"Export failed: {ex.Message}", SnackbarKind.Error);
        }
        finally
        {
            IsArchiving = false;
        }
    }

    [RelayCommand]
    private async Task RestoreConfigAsync()
    {
        var (distro, profile) = CurrentIdentityTarget();
        if (distro is null) return;

        var archivePath = ConfigArchivePath.Trim();
        if (archivePath.Length == 0)
        {
            _snackbars.Show("Set an archive path first.", SnackbarKind.Error);
            return;
        }

        IsArchiving = true;
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

            var notes = new List<string>();
            if (result.Missing.Length > 0) notes.Add($"Missing from the archive: {string.Join(", ", result.Missing)}.");
            if (result.Invalid.Length > 0) notes.Add($"Not absolute Linux paths: {string.Join(", ", result.Invalid)}.");
            var note = string.Join(" ", notes);
            _snackbars.Show(
                $"Restored {result.FilesWritten} file(s) into the \"{profile.Name}\" profile.{(note.Length > 0 ? " " + note : string.Empty)}",
                result.FilesWritten > 0 ? SnackbarKind.Success : SnackbarKind.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restoring the Qwen configuration failed.");
            _snackbars.Show($"Restore failed: {ex.Message}", SnackbarKind.Error);
        }
        finally
        {
            IsArchiving = false;
        }
    }

    private void LoadProfiles()
    {
        Profiles.Clear();
        if (_settings.Profiles.Length == 0)
        {
            _settings.Profiles = [Settings.CreateDefaultProfile()];
        }
        foreach (var profile in _settings.Profiles) Profiles.Add(profile);
        SelectedProfile = _settings.ResolveActiveProfile();
    }

    /// <summary>
    /// Writes the profile fields the page shows onto the selected profile. The profile objects in the
    /// collection are the same ones the settings hold, so this updates the shared settings in place.
    /// </summary>
    internal void CommitProfileEdits()
    {
        if (SelectedProfile is null) return;
        var name = ProfileName.Trim();
        var user = ProfileLinuxUser.Trim().ToLowerInvariant();
        if (name.Length > 0) SelectedProfile.Name = name;
        if (user.Length > 0) SelectedProfile.LinuxUser = user;
        if (ProfileWorkspace.Trim().Length > 0)
        {
            SelectedProfile.WorkspaceDirectory = ProfileWorkspace.Trim();
        }
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
        ProfileEmail = value.ResolvedEmail;
        ProfileGpgName = value.ResolvedGpgName;

        // The keys belong to the profile, so switching identities has to re-read them. The first switch
        // happens while the page is still loading, before the distro is known, and the refresh started
        // there covers it.
        if (_distroProvisioned && !_environment.IsBackupBusy)
        {
            _ = LoadIdentityCoreAsync(announce: false);
        }
    }

    private string[] EffectiveConfigPaths()
    {
        if (_settings.ConfigBackupPaths.Length > 0) return _settings.ConfigBackupPaths;
        return QwenConfigArchiveService.DefaultPaths(SelectedProfile ?? _settings.ResolveActiveProfile());
    }

    private static string[] SplitLines(string? text) =>
        (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
