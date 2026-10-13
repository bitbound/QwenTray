using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace QwenTray.Services;

/// <summary>
/// Runs <c>qwen serve</c> as a systemd user service inside the distro. The unit lives in each profile's
/// own home, so the daemon belongs to that Linux user and outlives the tray: quitting QwenTray, a tray
/// crash, and a Windows reboot all leave it serving, and the next tray start finds it there.
/// </summary>
public sealed class SystemdUserService(IWslDistro wsl, ILogger<SystemdUserService>? logger = null)
{
    private readonly ILogger<SystemdUserService> _logger = logger ?? NullLogger<SystemdUserService>.Instance;

    public const string UnitName = "qwen-serve.service";

    private const string UnitRelativePath = ".config/systemd/user/" + UnitName;
    private const string LauncherRelativePath = ".config/qwen/serve.sh";
    private const string EnvironmentRelativePath = ".config/qwen/serve.env";

    /// <summary>
    /// What systemd itself has to say about the unit, which is a different question from whether the
    /// port answers. <see cref="Installed"/> is false only when systemd knows of no such unit at all, so
    /// a profile that was never set up is distinguishable from one whose daemon is merely stopped.
    /// </summary>
    public sealed record UnitState(bool Installed, bool Enabled, bool Active)
    {
        /// <summary>Nothing could be asked. Never treated as a confirmed answer.</summary>
        public static UnitState Unknown => new(false, false, false);
    }

    /// <summary>
    /// wsl.exe runs a command without a login session, so the command never receives the runtime directory
    /// that names the user manager's socket. Measured in the distro: a bare <c>bash -lc</c> answers
    /// "Failed to connect to user scope bus", and naming the directory makes the same call succeed. Every
    /// call here goes through <see cref="RunUserManagerScriptAsync"/>, which applies this prefix.
    /// </summary>
    internal static string BuildUserManagerScript(string command)
        => $"export XDG_RUNTIME_DIR=\"/run/user/$(id -u)\" && {command}";

    /// <summary>
    /// The unit body. Paths are absolute and derived from a validated user name, so nothing
    /// user-controlled is ever handed to systemd's parser. Measured in the distro: systemd expands a
    /// specifier such as <c>%s</c> even inside single quotes, which is why the settings-derived command
    /// line lives in the launcher script rather than in <c>ExecStart</c>.
    /// </summary>
    internal static string BuildUnitContent(IdentityProfile profile, bool autoRestart)
    {
        var home = profile.HomeDirectory;
        return string.Join('\n', [
            "[Unit]",
            "Description=Qwen Code daemon (QwenTray)",
            "# Written by QwenTray. Change the daemon's settings in the app instead of here.",
            string.Empty,
            "[Service]",
            "Type=simple",
            // A service starts with systemd's own path rather than a login shell's, and the qwen launcher
            // lives in the profile's bin directory.
            $"Environment=\"PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:{home}/.local/bin\"",
            // The leading dash means a profile with no token configured simply has no token file.
            $"EnvironmentFile=-{home}/{EnvironmentRelativePath}",
            $"ExecStart=/bin/bash {home}/{LauncherRelativePath}",
            // systemd owns the restart. It notices a crashed process sooner than the tray's health poll
            // can, and it keeps the daemon up when the tray is not running at all.
            $"Restart={(autoRestart ? "always" : "no")}",
            "RestartSec=5",
            "TimeoutStopSec=30",
            string.Empty,
            "[Install]",
            "WantedBy=default.target",
            string.Empty,
        ]);
    }

    /// <summary>
    /// The launcher holds the command line built from settings. Reusing
    /// <see cref="WslDistro.BuildDaemonScript"/> keeps one quoting rule for the daemon's arguments.
    /// </summary>
    internal static string BuildLauncherContent(IReadOnlyList<string> serveArguments)
        => "#!/bin/bash\n" + WslDistro.BuildDaemonScript(serveArguments) + "\n";

    /// <summary>
    /// Writes the token file from the process's own stdin, so the value never appears on any command line.
    /// The umask is what makes the file unreadable by anything but the profile user.
    /// </summary>
    internal static string BuildEnvironmentWriteScript(IdentityProfile profile)
        => $"umask 077 && mkdir -p -- {WslDistro.QuotePosix($"{profile.HomeDirectory}/.config/qwen")}"
         + $" && cat > {WslDistro.QuotePosix($"{profile.HomeDirectory}/{EnvironmentRelativePath}")}";

    /// <summary>
    /// Removes the token file. Turning auth off has to delete the old secret rather than leave it where
    /// the daemon would still read it.
    /// </summary>
    internal static string BuildEnvironmentRemoveScript(IdentityProfile profile)
        => $"rm -f -- {WslDistro.QuotePosix($"{profile.HomeDirectory}/{EnvironmentRelativePath}")}";

    /// <summary>
    /// Asks systemd what it has. This is the tray's first question about the daemon, before the port,
    /// because something answering the port is not proof that the process it answers for is the one this
    /// profile's unit started. Measured in the distro: <c>show --value</c> prints ActiveState then
    /// UnitFileState on separate lines and exits 0 even for a unit that does not exist, leaving
    /// UnitFileState empty. The lines carry the answer; the exit code carries nothing.
    /// </summary>
    public async Task<UnitState> ReadStateAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        var result = await RunUserManagerScriptAsync(distro, profile, $"systemctl --user show -p ActiveState,UnitFileState --value {UnitName}", cancellationToken);
        if (!result.Succeeded) return UnitState.Unknown;

        // The empties are kept on purpose. A unit that does not exist has an empty second line, and
        // dropping it would make "never installed" and "installed but stopped" look identical.
        var lines = result.Output.Replace("\0", string.Empty).Split('\n');
        if (lines.Length < 2) return UnitState.Unknown;

        var activeState = lines[0].Trim().TrimStart('\uFEFF');
        var fileState = lines[1].Trim();
        return new UnitState(
            Installed: fileState.Length > 0 && !string.Equals(fileState, "not-found", StringComparison.Ordinal),
            Enabled: string.Equals(fileState, "enabled", StringComparison.Ordinal),
            Active: string.Equals(activeState, "active", StringComparison.Ordinal) || string.Equals(activeState, "activating", StringComparison.Ordinal));
    }

    /// <summary>
    /// Writes the unit, the launcher, and the token file, then makes systemd aware of it and starts it
    /// with the user manager. Nothing is run here, so this is safe while some other process holds the
    /// port: it makes the next boot and the next start belong to the tray without disturbing what is
    /// serving now. Content travels base64-encoded, so newlines and quotes never reach a command line.
    /// </summary>
    public async Task<bool> ConfigureAsync(Settings settings, string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        var files = new[]
        {
            (Path: $"{profile.HomeDirectory}/{UnitRelativePath}", Content: BuildUnitContent(profile, settings.AutoRestart), Executable: false),
            (Path: $"{profile.HomeDirectory}/{LauncherRelativePath}", Content: BuildLauncherContent(DaemonSupervisor.BuildServeArguments(settings, profile)), Executable: true),
        };

        foreach (var file in files)
        {
            var written = await wsl.RunScriptAsync(distro, profile.LinuxUser, BuildWriteFileScript(file.Path, file.Content, file.Executable), cancellationToken);
            if (!written.Succeeded)
            {
                _logger.LogError("Could not write {Path} for profile {User}. {Output}", file.Path, profile.LinuxUser, written.Output);
                return false;
            }
        }

        if (!await WriteTokenAsync(settings, distro, profile, cancellationToken)) return false;

        var enabled = await RunUserManagerScriptAsync(distro, profile, $"systemctl --user daemon-reload && systemctl --user enable {UnitName}", cancellationToken);
        if (!enabled.Succeeded)
        {
            _logger.LogError("Could not enable the daemon unit for profile {User}. {Output}", profile.LinuxUser, enabled.Output);
            return false;
        }

        _logger.LogInformation("Configured the daemon unit in distro {Distro} for {User}.", distro, profile.LinuxUser);
        return true;
    }

    /// <summary>
    /// Restarts rather than starts. A start on a unit systemd already considers running is a no-op, which
    /// would leave the previous settings in force and could never recover a daemon that is running but no
    /// longer answering. Measured in the distro: restart replaces a live process and also brings up one
    /// that was never started, while start leaves a running unit exactly as it was.
    /// </summary>
    public async Task<bool> RestartAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        var restarted = await RunUserManagerScriptAsync(distro, profile, $"systemctl --user restart {UnitName}", cancellationToken);
        if (!restarted.Succeeded)
        {
            _logger.LogError("Could not restart the daemon unit for profile {User}. {Output}", profile.LinuxUser, restarted.Output);
        }

        return restarted.Succeeded;
    }

    /// <summary>
    /// Stops the daemon and keeps it stopped through the next distro boot. Disabling is part of stopping:
    /// a unit that is merely stopped would come back the moment anything booted the distro again.
    /// </summary>
    public async Task<bool> StopAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        var result = await RunUserManagerScriptAsync(distro, profile, $"systemctl --user disable --now {UnitName}", cancellationToken);
        if (!result.Succeeded)
        {
            _logger.LogWarning("Could not stop the daemon unit for profile {User}. {Output}", profile.LinuxUser, result.Output);
        }

        return result.Succeeded;
    }

    /// <summary>
    /// Enables lingering for the profile user when the distro has not already recorded it. This needs root,
    /// and the marker file is asked for rather than <c>loginctl</c> output parsed, so the check is
    /// idempotent. Without lingering an enabled unit never starts on a boot nobody has logged into.
    /// </summary>
    public async Task<bool> EnsureLingerAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        if (!IdentityProfile.IsValidLinuxUser(profile.LinuxUser)) return false;

        var script = $"test -f /var/lib/systemd/linger/{profile.LinuxUser} || loginctl enable-linger {profile.LinuxUser}";
        var result = await wsl.RunRootScriptAsync(distro, script, cancellationToken);
        if (!result.Succeeded)
        {
            _logger.LogWarning("Could not enable lingering for {User}, so the daemon will not start on a boot with no login. {Output}", profile.LinuxUser, result.Output);
        }

        return result.Succeeded;
    }

    /// <summary>
    /// Stops every other profile's unit. Each profile keeps its own enabled unit in its own home, so
    /// without this a reboot would start them all and only one of them could bind the port.
    /// </summary>
    public async Task StandAsideOtherProfilesAsync(string distro, IdentityProfile active, IReadOnlyList<IdentityProfile> profiles, CancellationToken cancellationToken)
    {
        foreach (var other in profiles.Where(profile => !string.Equals(profile.LinuxUser, active.LinuxUser, StringComparison.Ordinal)))
        {
            if (!IdentityProfile.IsValidLinuxUser(other.LinuxUser)) continue;

            // A profile that was never set up has no unit to stop, so a failure here says nothing.
            await RunUserManagerScriptAsync(distro, other, $"systemctl --user disable --now {UnitName}", cancellationToken);
        }
    }

    /// <summary>The daemon's own process id, or nothing while systemd reports no main process.</summary>
    public async Task<int?> ReadMainPidAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        var result = await RunUserManagerScriptAsync(distro, profile, $"systemctl --user show -p MainPID --value {UnitName}", cancellationToken);
        if (!result.Succeeded || !int.TryParse(WslDistro.ParseVersion(result.Output), out var pid) || pid <= 0)
        {
            return null;
        }

        return pid;
    }

    private async Task<bool> WriteTokenAsync(Settings settings, string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Token))
        {
            var removed = await wsl.RunScriptAsync(distro, profile.LinuxUser, BuildEnvironmentRemoveScript(profile), cancellationToken);
            if (!removed.Succeeded)
            {
                _logger.LogError("Could not remove the daemon's token file for profile {User}. {Output}", profile.LinuxUser, removed.Output);
                return false;
            }

            return true;
        }

        var written = await wsl.RunScriptWithInputAsync(distro, profile.LinuxUser, BuildEnvironmentWriteScript(profile), $"{WslDistro.ServerTokenVariable}={settings.Token.Trim()}\n", cancellationToken);
        if (!written.Succeeded)
        {
            _logger.LogError("Could not write the daemon's token file for profile {User}. {Output}", profile.LinuxUser, written.Output);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Base64 keeps the content off the command line, and the mode is named rather than left to whatever
    /// umask the distro's user happens to have. <c>-D</c> creates the parent directories.
    /// </summary>
    internal static string BuildWriteFileScript(string path, string content, bool makeExecutable)
    {
        var quoted = WslDistro.QuotePosix(path);
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        return $"install -m {WslDistro.QuotePosix(makeExecutable ? "0700" : "0600")} -D /dev/null -- {quoted}"
             + $" && printf %s {WslDistro.QuotePosix(encoded)} | base64 -d > {quoted}";
    }

    private async Task<WslCommandResult> RunUserManagerScriptAsync(string distro, IdentityProfile profile, string command, CancellationToken cancellationToken)
        => await wsl.RunScriptAsync(distro, profile.LinuxUser, BuildUserManagerScript(command), cancellationToken);
}
