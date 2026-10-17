using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using QRCoder;
using QwenTray.Services;
using QwenTray.Views;

namespace QwenTray.ViewModels;

/// <summary>
/// The daemon's spawn flags: what it runs, where it binds, how a client authenticates, what it is
/// allowed to do, and the caps it runs under. The Qwen executable belongs to the active profile, so this
/// page follows the Identity page's selection.
/// </summary>
public sealed partial class ServerViewModel : ViewModelBase<ServerView>
{
    private readonly Settings _settings;
    private readonly DaemonSupervisor _supervisor;
    private readonly WslDistroProvisioner _provisioner;
    private readonly IFirewallRuleService _firewall;
    private readonly EnvironmentViewModel _environment;
    private readonly IdentityViewModel _identity;
    private readonly SettingsSaveViewModel _save;
    private readonly ISnackbarService _snackbars;
    private readonly ILogger<ServerViewModel> _logger;

    /// <summary>The save footer this page hosts, shared with every other settings page.</summary>
    public SettingsSaveViewModel Save => _save;

    [ObservableProperty]
    private string _qwenExecutable = string.Empty;

    [ObservableProperty]
    private string _qwenVersion = "Unknown";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckQwen))]
    private bool _isQwenBusy;

    [ObservableProperty]
    private string? _qwenProgress;

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
    [NotifyPropertyChangedFor(nameof(CanEditFirewall))]
    private bool _lanProxy;

    [ObservableProperty]
    private string _lanProxyPort = string.Empty;

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
    private string _extraArgs = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditFirewall))]
    private bool _isFirewallBusy;

    /// <summary>
    /// Whether a firewall button can be pressed. The exposure has to be on, because the opening is
    /// only worth having while something is meant to be listening on it.
    /// </summary>
    public bool CanEditFirewall => LanProxy && !IsFirewallBusy;

    public ServerViewModel(
        Settings settings,
        DaemonSupervisor supervisor,
        WslDistroProvisioner provisioner,
        IFirewallRuleService firewall,
        EnvironmentViewModel environment,
        IdentityViewModel identity,
        SettingsSaveViewModel save,
        ISnackbarService snackbars,
        ILogger<ServerViewModel> logger)
    {
        _settings = settings;
        _supervisor = supervisor;
        _provisioner = provisioner;
        _firewall = firewall;
        _environment = environment;
        _identity = identity;
        _save = save;
        _snackbars = snackbars;
        _logger = logger;

        _supervisor.LocalControlPairingUrlChanged += OnLocalControlPairingUrlChanged;

        // A backup holds the distro from outside it, so the Qwen commands have to wait for it.
        _environment.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EnvironmentViewModel.IsBackupBusy)) OnPropertyChanged(nameof(CanCheckQwen));
        };

        // The executable path belongs to the active profile, so following the selection keeps the field
        // honest even when the user never leaves the Identity page.
        _identity.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IdentityViewModel.SelectedProfile)) ReloadQwenExecutable();
        };

        // The shared save reads this page's fields even when the user never opened it, so seed them from
        // the loaded settings at construction rather than waiting for navigation.
        ReloadQwenExecutable();
        SeedFieldsFromSettings();
    }

    public bool CanCheckQwen => !IsQwenBusy && !_environment.IsBackupBusy;

    protected override Task OnInitializeAsync()
    {
        ReloadQwenExecutable();
        SeedFieldsFromSettings();
        RefreshLocalControlPairing();

        return Task.CompletedTask;
    }

    private void SeedFieldsFromSettings()
    {
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
        LanProxy = _settings.LanProxy;
        LanProxyPort = _settings.LanProxyPort.ToString();
        MaxSessions = _settings.MaxSessions.ToString();
        MaxConnections = _settings.MaxConnections.ToString();
        EventRingSize = _settings.EventRingSize.ToString();
        RateLimit = _settings.RateLimit;
        DebugMode = _settings.DebugMode;
        ExtraArgs = string.Join(Environment.NewLine, _settings.ExtraArgs);
    }

    /// <summary>The active profile's executable path, which is where the page's field is stored.</summary>
    private void ReloadQwenExecutable()
    {
        QwenExecutable = (_identity.SelectedProfile ?? _settings.ResolveActiveProfile()).ResolvedQwenExecutable;
    }

    /// <summary>Writes the edited executable path onto the active profile, which is where it lives.</summary>
    internal void CommitProfileEdits()
    {
        var profile = _identity.SelectedProfile;
        if (profile is null) return;
        var qwen = QwenExecutable.Trim();
        if (qwen.Length > 0) profile.QwenExecutable = qwen;
    }

    [RelayCommand]
    private async Task CheckQwenUpdateAsync()
    {
        IsQwenBusy = true;
        QwenProgress = "Reading the installed and latest versions…";
        try
        {
            var distro = _settings.WslDistroName.Trim();
            CommitProfileEdits();
            var profile = _identity.SelectedProfile ?? _settings.ResolveActiveProfile();
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
    private async Task UpdateQwenAsync()
    {
        IsQwenBusy = true;
        QwenProgress = "Installing or updating Qwen in the distro…";
        try
        {
            var distro = _settings.WslDistroName.Trim();
            CommitProfileEdits();
            var profile = _identity.SelectedProfile ?? _settings.ResolveActiveProfile();
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
    /// Opens the Windows Firewall port the forwarder listens on. The port comes from the field rather
    /// than the saved settings, so the opening can be made for an edit the user has not saved yet.
    /// </summary>
    [RelayCommand]
    private async Task AllowFirewallPortAsync()
    {
        if (!TryParseExposurePort(out var port))
        {
            _snackbars.Show("The exposure port must be a number between 1 and 65535.", SnackbarKind.Warning);
            return;
        }

        IsFirewallBusy = true;
        try
        {
            var result = await _firewall.AllowInboundPortAsync(port, CancellationToken.None);
            ReportFirewallResult(result, $"Windows Firewall now allows inbound TCP on port {port}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening the firewall port {Port} failed.", port);
            _snackbars.Show($"Opening the firewall port failed: {ex.Message}", SnackbarKind.Error);
        }
        finally
        {
            IsFirewallBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveFirewallRuleAsync()
    {
        IsFirewallBusy = true;
        try
        {
            var result = await _firewall.RemoveAsync(CancellationToken.None);
            ReportFirewallResult(result, "Removed the QwenTray firewall rule.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Removing the firewall rule failed.");
            _snackbars.Show($"Removing the firewall rule failed: {ex.Message}", SnackbarKind.Error);
        }
        finally
        {
            IsFirewallBusy = false;
        }
    }

    /// <summary>The same range the shared save and the supervisor's own validation use.</summary>
    private bool TryParseExposurePort(out int port)
    {
        var parsed = int.TryParse(LanProxyPort.Trim(), out port) && port is >= 1 and <= 65535;
        return parsed;
    }

    /// <summary>
    /// A dismissed consent prompt is not a failure, so it is reported as a warning that says nothing
    /// changed rather than as an error the user has to act on.
    /// </summary>
    private void ReportFirewallResult(FirewallRuleResult result, string success)
    {
        switch (result.Outcome)
        {
            case FirewallRuleOutcome.Created:
            case FirewallRuleOutcome.Removed:
                _snackbars.Show(success, SnackbarKind.Success);
                break;
            case FirewallRuleOutcome.Cancelled:
                _snackbars.Show("No change was made, because the Windows consent prompt was dismissed.", SnackbarKind.Warning);
                break;
            case FirewallRuleOutcome.AlreadyAbsent:
                _snackbars.Show("There was no QwenTray firewall rule to remove.", SnackbarKind.Info);
                break;
            default:
                _snackbars.Show($"Windows Firewall refused the change. {result.Detail}", SnackbarKind.Error);
                break;
        }
    }

    private void OnLocalControlPairingUrlChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshLocalControlPairing);
    }

    /// <summary>Recomputes the pairing QR from the address the daemon last reported.</summary>
    internal void RefreshLocalControlPairing()
    {
        var url = _supervisor.LocalControlPairingUrl;
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
}
