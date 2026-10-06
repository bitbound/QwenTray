using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using QRCoder;
using QwenTray.Services;
using QwenTray.Views;

namespace QwenTray.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase<SettingsView>
{
    private readonly Settings _settings;
    private readonly DaemonSupervisor _supervisor;
    private readonly RegistryAutostartService _autostart;
    private readonly ICertificateTrustService _certTrust;
    private readonly ILogger<SettingsViewModel> _logger;

    [ObservableProperty]
    private string _qwenExecutable = string.Empty;

    [ObservableProperty]
    private string _workspaceDirectory = string.Empty;

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
        DaemonSupervisor supervisor,
        RegistryAutostartService autostart,
        ICertificateTrustService certTrust,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _supervisor = supervisor;
        _autostart = autostart;
        _certTrust = certTrust;
        _logger = logger;
        _supervisor.LocalControlPairingUrlChanged += OnLocalControlPairingUrlChanged;
    }

    public string SettingsFilePath => SettingsStore.FilePath;

    public string LogFilePath => SettingsStore.LogFilePath;

    protected override Task OnInitializeAsync()
    {
        QwenExecutable = _settings.QwenExecutable;
        WorkspaceDirectory = _settings.WorkspaceDirectory;
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
        StartOnLogin = _autostart.IsEnabled();
        UpdateLocalControlPairing(_supervisor.LocalControlPairingUrl);
        RefreshPublisherTrustState();

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

        _settings.QwenExecutable = QwenExecutable.Trim();
        _settings.WorkspaceDirectory = WorkspaceDirectory.Trim();
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

        try
        {
            SettingsStore.Save(_settings);
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

        _logger.LogInformation("Settings saved. Daemon argument count={ArgumentCount} LocalControl={LocalControl}.", DaemonSupervisor.BuildArguments(_settings).Count, _settings.LocalControl);

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
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SaveWarning = "No daemon log is known yet. The path is captured once the daemon starts.";
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
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

    private static int ParseInt(string? text, int fallback) => int.TryParse((text ?? string.Empty).Trim(), out var value) ? value : fallback;

    private static string[] SplitLines(string? text) =>
        (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}