using Microsoft.Extensions.Logging;
using QwenTray.Services;

namespace QwenTray;

/// <summary>
/// What the tray decides about the daemon when it starts. The distro is asked whether it is booted before
/// anything else, because that is the only question about it that can be asked without booting it. Every
/// other check here runs a script inside the distro, which would bring up one the user deliberately left
/// down, so a stopped distro stays stopped until something asks for it: the Start command, or the user's
/// own shell. A distro that is already up is fair game, and the daemon is handed to systemd there.
/// </summary>
public sealed class DaemonLauncher(
    IWslDistro wsl,
    WslDistroProvisioner provisioner,
    DaemonSupervisor supervisor,
    Settings settings,
    ILogger<DaemonLauncher> logger)
{
    public async Task LaunchAsync(CancellationToken cancellationToken)
    {
        var distro = settings.WslDistroName;

        if (!await wsl.DistroIsRunningAsync(distro, cancellationToken))
        {
            logger.LogInformation("Daemon not started on launch. The \"{Distro}\" distro is not running.", distro);
            return;
        }

        // Launching into a distro that was never provisioned would spawn a daemon that immediately crashes
        // and back off-restarts, so the tray stays put until the Linux environment exists.
        if (!await provisioner.IsConfiguredAsync(distro, cancellationToken))
        {
            logger.LogInformation("Daemon not started on launch. The \"{Distro}\" Linux environment is not set up.", distro);
            return;
        }

        await supervisor.StartAsync();
    }
}
