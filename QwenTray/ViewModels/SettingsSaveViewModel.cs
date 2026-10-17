using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QwenTray.Services;

namespace QwenTray.ViewModels;

/// <summary>
/// The save footer every settings page shares. One Save reads all four pages and writes the whole
/// settings object, exactly as the single settings page used to, so no page has a partial save of its own.
/// </summary>
public sealed partial class SettingsSaveViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly Settings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly DaemonSupervisor _supervisor;
    private readonly RegistryAutostartService _autostart;
    private readonly ILogger<SettingsSaveViewModel> _logger;

    [ObservableProperty]
    private string? _saveError;

    [ObservableProperty]
    private string? _saveMessage;

    [ObservableProperty]
    private double _saveMessageOpacity = 1d;

    [ObservableProperty]
    private string? _saveWarning;

    // The hold and fade lengths have to agree with the OpacityTransition on the save message in
    // SettingsFooter.axaml.
    private static readonly TimeSpan SaveMessageHold = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SaveMessageFade = TimeSpan.FromMilliseconds(300);

    private CancellationTokenSource? _saveMessageFadeOut;

    // The pages are resolved on demand rather than injected. Each of them takes this view model for the
    // footer, so taking them here would leave both sides unresolvable as singletons.
    public SettingsSaveViewModel(
        IServiceProvider services,
        Settings settings,
        SettingsStore settingsStore,
        DaemonSupervisor supervisor,
        RegistryAutostartService autostart,
        ILogger<SettingsSaveViewModel> logger)
    {
        _services = services;
        _settings = settings;
        _settingsStore = settingsStore;
        _supervisor = supervisor;
        _autostart = autostart;
        _logger = logger;
    }

    /// <summary>Shows a problem a page's own command found, in the same place a failed save appears.</summary>
    public void ReportError(string message) => SaveError = message;

    /// <summary>
    /// Writes the Identity page's pending profile edits onto the shared settings object and hands back
    /// the profiles as they now stand. The distro setup runs off this list, so a profile added but not yet
    /// saved still gets its Linux user and its qwen install.
    /// </summary>
    internal IdentityProfile[] CommitProfilesForDistroWork()
    {
        var identity = _services.GetRequiredService<IdentityViewModel>();
        identity.CommitProfileEdits();
        return [.. identity.Profiles];
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        SaveError = null;
        SaveMessage = null;
        SaveWarning = null;

        if (TryApplyToSettings() is { } error)
        {
            SaveError = error;
            return;
        }

        var settingsPage = _services.GetRequiredService<SettingsViewModel>();

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

        if (_autostart.IsEnabled() != settingsPage.StartOnLogin)
        {
            _autostart.SetEnabled(settingsPage.StartOnLogin);
            _logger.LogInformation("Start with Windows {State}.", settingsPage.StartOnLogin ? "enabled" : "disabled");
        }

        _logger.LogInformation("Settings saved. Daemon argument count={ArgumentCount} LocalControl={LocalControl} LanProxy={LanProxy}.", DaemonSupervisor.BuildServeArguments(_settings, _settings.ResolveActiveProfile()).Count, _settings.LocalControl, _settings.LanProxy);

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

    /// <summary>
    /// Reads every page's fields onto the shared settings object and checks them in the order the single
    /// settings page did. Returns the first problem, or nothing once the settings object is up to date.
    /// </summary>
    internal string? TryApplyToSettings()
    {
        var server = _services.GetRequiredService<ServerViewModel>();
        var environment = _services.GetRequiredService<EnvironmentViewModel>();
        var identity = _services.GetRequiredService<IdentityViewModel>();
        var settingsPage = _services.GetRequiredService<SettingsViewModel>();

        if (!int.TryParse(server.Port.Trim(), out var port) || port is < 0 or > 65535)
        {
            return "Port must be a number between 0 and 65535.";
        }

        if (string.IsNullOrWhiteSpace(server.Host))
        {
            return "Hostname cannot be empty.";
        }

        if (server.LocalControl && !string.Equals(server.Host.Trim(), "127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            return "Device pairing requires the main address to be 127.0.0.1. Change the address or turn off device pairing.";
        }

        if (server.LocalControl && !server.ServeWeb)
        {
            return "Device pairing requires the Web Shell to be enabled.";
        }

        if (!int.TryParse(server.LanProxyPort.Trim(), out var lanProxyPort) || lanProxyPort is < 1 or > 65535)
        {
            return "The LAN exposure port must be a number between 1 and 65535.";
        }

        if (server.LanProxy && lanProxyPort == port)
        {
            return "The LAN exposure port matches the daemon port. The forwarder binds a port of its own, so it cannot share the daemon's.";
        }

        if (string.IsNullOrWhiteSpace(environment.WslDistroName))
        {
            return "The WSL distro name cannot be empty.";
        }

        var distroName = environment.WslDistroName.Trim();
        if (!Settings.IsSafeDistroName(distroName))
        {
            return "The WSL distro name may only contain letters, digits, dot, underscore, and hyphen.";
        }

        // The profile edits are written through before they are checked, so the check sees what the page
        // shows. The executable path lives on the Server page but belongs to the active profile, and the
        // same check covers it.
        identity.CommitProfileEdits();
        server.CommitProfileEdits();
        if (identity.Profiles.Count == 0)
        {
            return "Add at least one identity profile.";
        }

        var unsafeProfile = identity.Profiles.FirstOrDefault(profile => !profile.IsShellSafe);
        if (unsafeProfile is not null)
        {
            return $"The \"{unsafeProfile.Name}\" profile has a Linux user, workspace, signing key name or email, or qwen path that is not allowed. Use a lowercase user name, a plain absolute path, a plain email address, and a key name without quotes or shell characters.";
        }

        var duplicate = identity.Profiles.GroupBy(profile => profile.LinuxUser, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            return $"Two profiles both use the Linux user \"{duplicate.Key}\". Each identity needs its own user.";
        }

        _settings.WslDistroName = distroName;
        // The folder is what is kept. The file name carries a timestamp so an export never lands on an
        // earlier bundle, and only the folder survives to the next run.
        var bundlePath = environment.BundlePath.Trim();
        _settings.BundleDirectory = bundlePath.Length == 0
            ? string.Empty
            : Path.GetDirectoryName(bundlePath)
              ?? string.Empty;
        _settings.Profiles = [.. identity.Profiles];
        _settings.ActiveProfileId = identity.SelectedProfile?.Id ?? identity.Profiles[0].Id;
        _settings.Host = server.Host.Trim();
        _settings.Port = port;
        _settings.Token = server.Token.Trim();
        _settings.RequireAuth = server.RequireAuth;
        _settings.AllowedOrigins = SplitLines(server.AllowedOrigins);
        _settings.TlsCertPath = server.TlsCertPath.Trim();
        _settings.TlsKeyPath = server.TlsKeyPath.Trim();
        _settings.EnableSessionShell = server.EnableSessionShell;
        _settings.ServeWeb = server.ServeWeb;
        _settings.LocalControl = server.LocalControl;
        _settings.LanProxy = server.LanProxy;
        _settings.LanProxyPort = lanProxyPort;
        server.RefreshLocalControlPairing();
        _settings.MaxSessions = ParseInt(server.MaxSessions, _settings.MaxSessions);
        _settings.MaxConnections = ParseInt(server.MaxConnections, _settings.MaxConnections);
        _settings.EventRingSize = ParseInt(server.EventRingSize, _settings.EventRingSize);
        _settings.RateLimit = server.RateLimit;
        _settings.DebugMode = server.DebugMode;
        _settings.HealthPollSeconds = Math.Clamp(ParseInt(settingsPage.HealthPollSeconds, _settings.HealthPollSeconds), 1, 60);
        _settings.AutoRestart = settingsPage.AutoRestart;
        _settings.CheckForUpdates = settingsPage.CheckForUpdates;
        _settings.MaxBackoffSeconds = Math.Max(1, ParseInt(settingsPage.MaxBackoffSeconds, _settings.MaxBackoffSeconds));
        _settings.ExtraArgs = SplitLines(server.ExtraArgs);
        _settings.SyncPaths = SplitLines(identity.SyncEntries);

        return null;
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

    private static int ParseInt(string? text, int fallback) => int.TryParse((text ?? string.Empty).Trim(), out var value) ? value : fallback;

    private static string[] SplitLines(string? text) =>
        (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
