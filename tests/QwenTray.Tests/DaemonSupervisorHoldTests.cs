using QwenTray.Services;
using Xunit;

namespace QwenTray.Tests;

// Export, import, and distro deletion work on the distro from outside the daemon, so they need it to
// stay down for the whole operation. A plain stop is not enough: the daemon the stop kills reads as a
// crash, and the restart that follows boots the distro again mid-operation.
public sealed class DaemonSupervisorHoldTests
{
    private static (DaemonSupervisor Supervisor, FakeWslDistro Wsl) Create()
    {
        var wsl = new FakeWslDistro();
        return (new DaemonSupervisor(new Settings { AutoRestart = true }, wsl, new SystemdUserService(wsl)), wsl);
    }

    [Fact]
    public async Task AHoldTerminatesTheDistroEvenWhenTheSupervisorOwnsNoDaemon()
    {
        var (supervisor, wsl) = Create();

        var hold = await supervisor.HoldAsync("exporting the bundle");
        Assert.Equal(["QwenTray"], wsl.TerminatedDistros);
        await hold.DisposeAsync();
    }

    [Fact]
    public async Task StartingIsIgnoredWhileTheDistroIsHeld()
    {
        var (supervisor, wsl) = Create();

        var hold = await supervisor.HoldAsync("exporting the bundle");
        // Taking the hold stops the unit, which is the one distro call expected here. Everything counted
        // from here on would be the ignored start booting the distro back up.
        var callsAfterHold = wsl.RunScriptCalls;
        await supervisor.StartAsync();

        Assert.Equal(callsAfterHold, wsl.RunScriptCalls);
        Assert.DoesNotContain(wsl.Scripts, script => script.Contains("systemctl --user restart", StringComparison.Ordinal));
        Assert.Equal(DaemonState.Stopped, supervisor.State);
        await hold.DisposeAsync();
    }

    [Fact]
    public async Task TheDistroIsReleasedOnlyWhenTheLastHoldIsGone()
    {
        var (supervisor, _) = Create();

        var outer = await supervisor.HoldAsync("exporting the bundle");
        var inner = await supervisor.HoldAsync("importing the bundle");
        Assert.True(supervisor.IsHeld);

        await inner.DisposeAsync();
        await inner.DisposeAsync();
        Assert.True(supervisor.IsHeld);

        await outer.DisposeAsync();
        Assert.False(supervisor.IsHeld);
    }

    [Fact]
    public async Task TheHoldChangeIsAnnouncedOnTakeAndOnRelease()
    {
        var (supervisor, _) = Create();
        var announcements = 0;
        supervisor.HeldChanged += (_, _) => announcements++;

        var hold = await supervisor.HoldAsync("exporting the bundle");
        Assert.Equal(1, announcements);

        await hold.DisposeAsync();
        Assert.Equal(2, announcements);
    }
}
