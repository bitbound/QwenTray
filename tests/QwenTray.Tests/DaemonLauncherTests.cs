using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using QwenTray.Services;
using Xunit;

namespace QwenTray.Tests;

// The tray used to start the daemon on launch whatever the distro was doing. Every question that start
// path asks runs a script inside the distro, which boots one the user had deliberately left stopped.
public sealed class DaemonLauncherTests
{
    private static (DaemonLauncher Launcher, FakeWslDistro Wsl, DaemonSupervisor Supervisor) Create(bool running, string rootScriptOutput = "")
    {
        var wsl = new FakeWslDistro { DistroRunning = running, RootScriptOutput = rootScriptOutput };
        // Port 1 answers nothing, so a start that reaches systemd stays distinguishable from one that
        // adopts a daemon somebody else left serving the default port.
        var settings = new Settings { Port = 1, AutoRestart = false };
        var supervisor = new DaemonSupervisor(settings, wsl, new SystemdUserService(wsl));
        var provisioner = new WslDistroProvisioner(wsl, new Bitbound.SystemAbstractions.TestUtilities.FileSystem.FakeFileSystem(), null!, NullLogger<WslDistroProvisioner>.Instance);

        return (new DaemonLauncher(wsl, provisioner, supervisor, settings, NullLogger<DaemonLauncher>.Instance), wsl, supervisor);
    }

    [Fact]
    public async Task ALaunchLeavesAStoppedDistroStopped()
    {
        var (launcher, wsl, supervisor) = Create(running: false);

        await launcher.LaunchAsync(CancellationToken.None);

        // The running check is a read of the list wsl.exe already has, so a launch that decides to stay
        // put never runs a script inside the distro, which is the only way a launch could boot it.
        Assert.Equal(0, wsl.RunScriptCalls);
        Assert.Empty(wsl.RootScripts);
        Assert.Equal(DaemonState.Stopped, supervisor.State);
    }

    [Fact]
    public async Task ALaunchInADistroThatIsNotProvisionedHandsNothingToSystemd()
    {
        var (launcher, wsl, supervisor) = Create(running: true);

        await launcher.LaunchAsync(CancellationToken.None);

        Assert.DoesNotContain(wsl.RootScripts, script => script.Contains("enable-linger", StringComparison.Ordinal));
        Assert.Equal(DaemonState.Stopped, supervisor.State);
    }

    [Fact]
    public async Task ALaunchInADistroThatIsAlreadyRunningHandsTheDaemonToSystemd()
    {
        var (launcher, wsl, _) = Create(running: true, rootScriptOutput: "ready");

        await launcher.LaunchAsync(CancellationToken.None);

        Assert.Contains(wsl.Scripts, script => script.Contains("systemctl --user restart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheContainerBuildsTheLauncherTheTrayAsksForOnLaunch()
    {
        // The tray resolves this once at startup and treats a failure there as fatal, so an unregistered
        // dependency would only show up as a tray that never comes up.
        var provider = Program.BuildServices();
        try
        {
            Assert.NotNull(provider.GetRequiredService<DaemonLauncher>());
        }
        finally
        {
            // The container owns the supervisor, which is IAsyncDisposable-only, so disposal has to be
            // asynchronous here for the same reason Program.Main disposes it that way.
            await provider.DisposeAsync();
        }
    }
}
