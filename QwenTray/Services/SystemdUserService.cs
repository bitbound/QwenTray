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

    /// <summary>The forwarder the LAN exposure runs, and the socket that owns its listening port.</summary>
    public const string ProxyUnitName = "qwen-lan-proxy.service";
    public const string ProxySocketUnitName = "qwen-lan-proxy.socket";

    /// <summary>
    /// The forwarder systemd ships. A unit cannot look a path up, so the path is written into the unit and
    /// a distro without it fails the exposure here rather than starting a unit that never comes up.
    /// </summary>
    internal const string SocketProxyPath = "/usr/lib/systemd/systemd-socket-proxyd";

    private const string UnitRelativePath = ".config/systemd/user/" + UnitName;
    private const string ProxyUnitRelativePath = ".config/systemd/user/" + ProxyUnitName;
    private const string ProxySocketRelativePath = ".config/systemd/user/" + ProxySocketUnitName;
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

        /// <summary>
        /// Set when the distro answered nothing at all, which is not the same as systemd reporting no
        /// such unit. A start path that cannot tell those apart reports the wrong thing to the user.
        /// </summary>
        public bool Unreachable { get; init; }
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
    /// Writes the token file from stdin, so the value never reaches a command line. <c>install</c> sets the
    /// mode rather than a umask, because a umask is ignored when a redirection truncates an existing file.
    /// </summary>
    internal static string BuildEnvironmentWriteScript(IdentityProfile profile)
    {
        var quoted = WslDistro.QuotePosix($"{profile.HomeDirectory}/{EnvironmentRelativePath}");
        return $"install -m '0600' -D /dev/null -- {quoted} && cat > {quoted}";
    }

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
        if (!result.Succeeded) return result.TimedOut ? UnitState.Unknown with { Unreachable = true } : UnitState.Unknown;

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
            (Path: $"{profile.HomeDirectory}/{UnitRelativePath}", Content: BuildUnitContent(profile, settings.AutoRestart), Mode: "0600"),
            (Path: $"{profile.HomeDirectory}/{LauncherRelativePath}", Content: BuildLauncherContent(DaemonSupervisor.BuildServeArguments(settings, profile)), Mode: "0700"),
        };

        foreach (var file in files)
        {
            var written = await wsl.RunScriptAsync(distro, profile.LinuxUser, WslDistro.BuildFileWriteScript(file.Path, file.Content, file.Mode), cancellationToken);
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

            // The exposure binds a port of its own, so two profiles' sockets could not both hold it, on the
            // same terms as the daemon's own port.
            await RunUserManagerScriptAsync(distro, other, $"systemctl --user disable --now {ProxySocketUnitName} {ProxyUnitName}", cancellationToken);
        }
    }

    /// <summary>
    /// The listening half of the exposure. systemd owns the port, so it is answered after a boot with no
    /// process of the tray's running, and the forwarder comes up on the first connection.
    /// </summary>
    internal static string BuildProxySocketContent(Settings settings)
    {
        return string.Join('\n', [
            "[Unit]",
            "Description=Qwen Code LAN exposure (QwenTray)",
            "# Written by QwenTray. Change the daemon's settings in the app instead of here.",
            string.Empty,
            "[Socket]",
            $"ListenStream=0.0.0.0:{settings.LanProxyPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            string.Empty,
            "[Install]",
            "WantedBy=sockets.target",
            string.Empty,
        ]);
    }

    /// <summary>
    /// The forwarding half. It dials the daemon's own loopback port, so the daemon keeps its loopback
    /// address, and this unit is what makes that daemon reachable without it binding the network itself.
    /// </summary>
    internal static string BuildProxyUnitContent(Settings settings)
    {
        return string.Join('\n', [
            "[Unit]",
            "Description=Qwen Code LAN forwarder (QwenTray)",
            "# Written by QwenTray. Change the daemon's settings in the app instead of here.",
            $"Requires={ProxySocketUnitName}",
            $"After={ProxySocketUnitName}",
            $"Requires={UnitName}",
            $"After={UnitName}",
            string.Empty,
            "[Service]",
            // No PrivateNetwork, unlike the example in the man page. That example dials a unix socket, while
            // this one dials loopback, and a private network namespace would give the process its own
            // loopback with nothing listening behind it.
            "Type=notify",
            $"ExecStart={SocketProxyPath} 127.0.0.1:{settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            // Two integers are all that reaches this unit, so systemd's specifier expansion has nothing to
            // find. The forwarder also does not exit when idle, because its exit-idle-time defaults to
            // infinity, so a restart here only ever recovers a process that actually failed.
            "Restart=always",
            "RestartSec=1",
            string.Empty,
        ]);
    }

    /// <summary>
    /// Writes the exposure's two units and starts it, or takes it away when the setting is off. Writing and
    /// restarting the socket on every start is what makes a changed port take effect, since an already
    /// running socket keeps the port it bound.
    /// </summary>
    public async Task<bool> ConfigureProxyAsync(Settings settings, string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        if (!settings.LanProxy)
        {
            return await RemoveProxyAsync(distro, profile, cancellationToken);
        }

        var present = await wsl.RunScriptAsync(distro, profile.LinuxUser, $"test -x {WslDistro.QuotePosix(SocketProxyPath)}", cancellationToken);
        if (!present.Succeeded)
        {
            _logger.LogError("{Path} is missing in the \"{Distro}\" distro, so the LAN exposure cannot start.", SocketProxyPath, distro);
            return false;
        }

        var files = new[]
        {
            (Path: $"{profile.HomeDirectory}/{ProxySocketRelativePath}", Content: BuildProxySocketContent(settings), Mode: "0600"),
            (Path: $"{profile.HomeDirectory}/{ProxyUnitRelativePath}", Content: BuildProxyUnitContent(settings), Mode: "0600"),
        };

        foreach (var file in files)
        {
            var written = await wsl.RunScriptAsync(distro, profile.LinuxUser, WslDistro.BuildFileWriteScript(file.Path, file.Content, file.Mode), cancellationToken);
            if (!written.Succeeded)
            {
                _logger.LogError("Could not write {Path} for profile {User}. {Output}", file.Path, profile.LinuxUser, written.Output);
                return false;
            }
        }

        var enabled = await RunUserManagerScriptAsync(distro, profile, $"systemctl --user daemon-reload && systemctl --user enable {ProxySocketUnitName} && systemctl --user restart {ProxySocketUnitName}", cancellationToken);
        if (!enabled.Succeeded)
        {
            _logger.LogError("Could not enable the LAN exposure for profile {User}. {Output}", profile.LinuxUser, enabled.Output);
            return false;
        }

        _logger.LogInformation("Exposed the daemon in distro {Distro} on 0.0.0.0:{Port} for {User}.", distro, settings.LanProxyPort, profile.LinuxUser);
        return true;
    }

    /// <summary>
    /// Stops the exposure and keeps it stopped through the next boot. The unit files stay, because the
    /// setting still asks for the exposure and the next start enables them again.
    /// </summary>
    public async Task<bool> StopProxyAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        var result = await RunUserManagerScriptAsync(distro, profile, $"systemctl --user disable --now {ProxySocketUnitName} {ProxyUnitName}", cancellationToken);
        if (!result.Succeeded)
        {
            _logger.LogDebug("The LAN exposure was not stopped for {User}. {Output}", profile.LinuxUser, result.Output);
        }

        return result.Succeeded;
    }

    /// <summary>
    /// Takes the exposure down and deletes its units, which is what turning the setting off has to mean.
    /// Leaving the files behind would let the next start re-enable a socket the user just asked to remove.
    /// </summary>
    public async Task<bool> RemoveProxyAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        await StopProxyAsync(distro, profile, cancellationToken);

        var removed = true;
        foreach (var file in new[] { $"{profile.HomeDirectory}/{ProxySocketRelativePath}", $"{profile.HomeDirectory}/{ProxyUnitRelativePath}" })
        {
            var deleted = await wsl.RunScriptAsync(distro, profile.LinuxUser, $"rm -f -- {WslDistro.QuotePosix(file)}", cancellationToken);
            removed = removed && deleted.Succeeded;
        }

        await RunUserManagerScriptAsync(distro, profile, "systemctl --user daemon-reload", cancellationToken);
        return removed;
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

    private async Task<WslCommandResult> RunUserManagerScriptAsync(string distro, IdentityProfile profile, string command, CancellationToken cancellationToken)
        => await wsl.RunScriptAsync(distro, profile.LinuxUser, BuildUserManagerScript(command), cancellationToken);
}
