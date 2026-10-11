using Microsoft.Extensions.Logging.Abstractions;
using QwenTray.Services;
using Xunit;

namespace QwenTray.Tests;

public sealed class DistroLifecycleTests
{
    // Delete and rebuild never read the Windows environment, which only the wslconfig writer does.
    private static WslDistroProvisioner CreateProvisioner(FakeWslDistro wsl)
        => new(wsl, new Bitbound.SystemAbstractions.TestUtilities.FileSystem.FakeFileSystem(), null!, NullLogger<WslDistroProvisioner>.Instance);

    [Fact]
    public async Task DeletingTheDistroStopsItBeforeUnregisteringIt()
    {
        var wsl = new FakeWslDistro();
        var provisioner = CreateProvisioner(wsl);

        await provisioner.DeleteAsync("QwenTray", CancellationToken.None);

        Assert.Single(wsl.TerminatedDistros, "QwenTray");
        Assert.Single(wsl.UnregisteredDistros, "QwenTray");
    }

    [Fact]
    public async Task RebuildingDeletesTheOldDistroAndProvisionsTheMarker()
    {
        var wsl = new FakeWslDistro();
        var provisioner = CreateProvisioner(wsl);
        var profiles = new[] { Settings.CreateDefaultProfile() };

        await provisioner.RebuildAsync("QwenTray", profiles, CancellationToken.None);

        Assert.Equal(["QwenTray"], wsl.UnregisteredDistros);
        Assert.Contains(wsl.RootScripts, script => script.Contains("touch /etc/qwentray-provisioned", StringComparison.Ordinal));
        Assert.Contains(wsl.RootScripts, script => script.Contains("useradd -m -s /bin/bash qwen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedUnregisterIsReportedInsteadOfSilentlyCarryingOn()
    {
        var wsl = new FakeWslDistro { UnregisterExitCode = 42 };
        var provisioner = CreateProvisioner(wsl);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.DeleteAsync("QwenTray", CancellationToken.None));
        Assert.Contains("could not delete", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheLatestVersionComesFromTheRegistryBecauseTheDistroHasNoNpm()
    {
        // The distro is sealed and the standalone Qwen install carries its own node runtime, so there is
        // no npm to ask. Measured on this box: command -v npm finds nothing.
        var script = WslDistroProvisioner.BuildLatestQwenVersionScript();
        Assert.Contains("curl -fsSL", script, StringComparison.Ordinal);
        Assert.Contains("registry.npmjs.org/@qwen-code/qwen-code/latest", script, StringComparison.Ordinal);
        Assert.DoesNotContain("npm view", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadingTheLatestVersionYieldsTheBareNumber()
    {
        var wsl = new FakeWslDistro { ScriptOutput = "0.25.0" };
        var provisioner = CreateProvisioner(wsl);

        Assert.Equal("0.25.0", await provisioner.ReadLatestQwenVersionAsync("QwenTray", Settings.CreateDefaultProfile(), CancellationToken.None));
        Assert.Contains(wsl.Scripts, script => script.Contains("registry.npmjs.org", StringComparison.Ordinal));
    }
}

public sealed class DaemonLogPathTests
{
    // The exact line qwen serve writes, which is on stderr: process.stderr.write in the shipped bundle.
    private const string BannerLine = "qwen serve: daemon log \u2192 /home/qwen/.qwen/debug/daemon/daemon.log";

    [Fact]
    public void TheStartupBannerYieldsADistroSharePath()
    {
        Assert.True(DaemonSupervisor.TryReadLogPath(BannerLine, "QwenTray", out var path));
        Assert.Equal(@"\\wsl.localhost\QwenTray\home\qwen\.qwen\debug\daemon\daemon.log", path);
    }

    [Fact]
    public void OtherOutputIsNotABanner()
    {
        Assert.False(DaemonSupervisor.TryReadLogPath("qwen serve: listening on 127.0.0.1:4170", "QwenTray", out _));
        Assert.False(DaemonSupervisor.TryReadLogPath("qwen serve: daemon log \u2192 (disabled)", "QwenTray", out _));
    }

    [Fact]
    public async Task AnAdoptedDaemonWithNoBannerIsFoundAtItsConventionalPath()
    {
        var wsl = new FakeWslDistro { ScriptOutput = "/home/qwen/.qwen/debug/daemon/daemon.log" };
        var supervisor = new DaemonSupervisor(new Settings(), wsl);

        var path = await supervisor.ResolveLogPathAsync(CancellationToken.None);

        Assert.Equal(@"\\wsl.localhost\QwenTray\home\qwen\.qwen\debug\daemon\daemon.log", path);
        Assert.Contains(wsl.Scripts, script => script.Contains("~/.qwen/debug/daemon/daemon.log", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProfileWithoutAValidUserNameHasNoLogToFind()
    {
        var wsl = new FakeWslDistro();
        var settings = new Settings { Profiles = [new IdentityProfile { Id = "x", Name = "X", LinuxUser = "not a user" }] };
        var supervisor = new DaemonSupervisor(settings, wsl);

        Assert.Null(await supervisor.ResolveLogPathAsync(CancellationToken.None));
        Assert.Empty(wsl.Scripts);
    }
}

public sealed class WebShellAddressTests
{
    [Fact]
    public void NoTokenLeavesTheAddressAlone()
    {
        var settings = new Settings { Token = "  " };
        Assert.Equal("http://127.0.0.1:4170/", settings.WebShellUri.ToString());
        Assert.Equal(string.Empty, settings.WebShellUri.Fragment);
    }

    [Fact]
    public void ATokenTravelsInTheFragment()
    {
        var settings = new Settings { Token = "abc123" };
        Assert.Equal("http://127.0.0.1:4170/#token=abc123", settings.WebShellUri.ToString());
    }

    [Fact]
    public void TheFragmentSurvivesBeingRoundTrippedThroughSearchParams()
    {
        // The page reads it as new URLSearchParams(location.hash.replace(/^#/, "")).get("token"), so the
        // fragment has to decode back to the exact token the daemon was given.
        foreach (var token in new[] { "abc123", "a+b/c=d", "with space", "ünicode" })
        {
            var fragment = new Settings { Token = token }.WebShellUri.Fragment.TrimStart('#');
            Assert.StartsWith("token=", fragment, StringComparison.Ordinal);
            Assert.Equal(token, Uri.UnescapeDataString(fragment["token=".Length..]));
        }
    }

    [Fact]
    public void AWildcardBindStillOpensOnLoopback()
    {
        var settings = new Settings { Host = "0.0.0.0", Token = "t0ken" };
        Assert.Equal("http://127.0.0.1:4170/#token=t0ken", settings.WebShellUri.ToString());
    }

    [Fact]
    public void TlsMakesTheFragmentAddressHttps()
    {
        var settings = new Settings { TlsCertPath = "/etc/tls/cert.pem", TlsKeyPath = "/etc/tls/key.pem", Token = "t0ken" };
        Assert.Equal("https://127.0.0.1:4170/#token=t0ken", settings.WebShellUri.ToString());
    }

    [Fact]
    public void TheTokenStaysOutOfThePlainWebAddress()
    {
        var settings = new Settings { Token = "secret" };
        Assert.DoesNotContain("secret", settings.WebUri.ToString(), StringComparison.Ordinal);
    }
}
