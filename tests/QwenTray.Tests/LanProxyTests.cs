using System.Net;
using System.Net.Sockets;
using QwenTray.Services;
using Xunit;

namespace QwenTray.Tests;

/// <summary>
/// The LAN exposure is a second systemd unit pair the tray applies on every start. These pin the promises
/// around it: a start makes the distro match the setting, and a stop takes the listening port down with the
/// daemon it forwards to.
/// </summary>
public sealed class LanProxyTests
{
    [Fact]
    public async Task StartingWritesAndEnablesTheExposureWhenItIsOn()
    {
        var wsl = new FakeWslDistro();
        var settings = new Settings { Port = UnusedPort(), LanProxy = true, LanProxyPort = 4171 };
        var supervisor = new DaemonSupervisor(settings, wsl, new SystemdUserService(wsl));

        await supervisor.StartAsync();

        Assert.Contains(wsl.UserScripts, entry => entry.Script.Contains("enable qwen-lan-proxy.socket", StringComparison.Ordinal));
        Assert.Null(supervisor.LastProblem);
    }

    [Fact]
    public async Task StartingRemovesTheExposureWhenItIsOff()
    {
        // The default state. Nothing is exposed, and a unit pair left behind by an earlier run is taken away
        // rather than left to be re-enabled by the next start.
        var wsl = new FakeWslDistro();
        var settings = new Settings { Port = UnusedPort() };
        var supervisor = new DaemonSupervisor(settings, wsl, new SystemdUserService(wsl));

        await supervisor.StartAsync();

        Assert.Contains(wsl.UserScripts, entry => entry.Script.Contains("disable --now qwen-lan-proxy.socket qwen-lan-proxy.service", StringComparison.Ordinal));
        Assert.DoesNotContain(wsl.UserScripts, entry => entry.Script.Contains("enable qwen-lan-proxy.socket", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StoppingTakesTheExposureDownWithTheDaemon()
    {
        // A stopped tray has to mean nothing is listening, on the same terms as the daemon's own port.
        var wsl = new FakeWslDistro();
        var supervisor = new DaemonSupervisor(new Settings { LanProxy = true }, wsl, new SystemdUserService(wsl));

        await supervisor.StopAsync();

        Assert.Contains(wsl.UserScripts, entry => entry.User == "qwen" && entry.Script.Contains("disable --now qwen-lan-proxy.socket qwen-lan-proxy.service", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SwitchingProfilesStandsTheOtherProfilesExposureAside()
    {
        // Both profiles' sockets would bind the same port, so only one exposure can be enabled at a time.
        var wsl = new FakeWslDistro();
        var settings = new Settings
        {
            LanProxy = true,
            Profiles = [Profile("qwen"), Profile("dev")],
            ActiveProfileId = "qwen",
        };
        var supervisor = new DaemonSupervisor(settings, wsl, new SystemdUserService(wsl));

        await supervisor.StopAsync();

        Assert.Contains(wsl.UserScripts, entry => entry.User == "dev" && entry.Script.Contains("disable --now qwen-lan-proxy.socket", StringComparison.Ordinal));
    }

    [Fact]
    public void TheExposurePortCannotBeTheDaemonPort()
    {
        var warnings = DaemonSupervisor.ValidateSettings(new Settings { LanProxy = true, Port = 4171, LanProxyPort = 4171 }).ToList();

        Assert.Contains(warnings, warning => warning.Contains("forwarder could never bind it", StringComparison.Ordinal));
    }

    [Fact]
    public void AWildcardBindAlreadyExposesTheDaemonAndTheExposureSaysSo()
    {
        // A warning rather than an error, because the combination is not invalid. It just adds nothing, and
        // the wildcard bind is the setting that breaks Live Voice.
        var warnings = DaemonSupervisor.ValidateSettings(new Settings { LanProxy = true, Host = "0.0.0.0", Port = 4170, LanProxyPort = 4171 }).ToList();

        Assert.Contains(warnings, warning => warning.Contains("127.0.0.1", StringComparison.Ordinal));
    }

    [Fact]
    public void CopyingSettingsCarriesTheExposure()
    {
        // Import and reset write through CopyFrom, so a setting that is left out of it silently reverts.
        var target = new Settings();

        target.CopyFrom(new Settings { LanProxy = true, LanProxyPort = 4999 });

        Assert.True(target.LanProxy);
        Assert.Equal(4999, target.LanProxyPort);
    }

    private static IdentityProfile Profile(string linuxUser) => new() { Id = linuxUser, Name = linuxUser, LinuxUser = linuxUser };

    /// <summary>A port nothing can be listening on, so a start never mistakes a real daemon for its own.</summary>
    private static int UnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.Server.LocalEndPoint!).Port;
        listener.Stop();
        return port;
    }
}
