using System.Net;
using System.Net.Sockets;
using System.Text;
using QwenTray.Services;
using Xunit;

namespace QwenTray.Tests;

/// <summary>
/// The daemon runs as a systemd user unit so it outlives the tray. These pin the shape of what is written
/// into the distro and the calls that drive it.
/// </summary>
public sealed class SystemdUnitTests
{
    private static IdentityProfile Profile() => Settings.CreateDefaultProfile();

    [Fact]
    public void EveryUserManagerCallNamesTheRuntimeDirectory()
    {
        // Measured in the distro: wsl.exe runs a command without a login session, and a bare bash -lc
        // answers "Failed to connect to user scope bus", naming neither the bus nor the runtime
        // directory. Naming the directory is what makes systemctl --user reachable at all.
        var script = SystemdUserService.BuildUserManagerScript("systemctl --user is-active qwen-serve.service");

        Assert.Contains("export XDG_RUNTIME_DIR=\"/run/user/$(id -u)\"", script, StringComparison.Ordinal);
        Assert.StartsWith("export", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUnitIsPulledUpWithTheUserManagerAndRestartsOnItsOwn()
    {
        var unit = SystemdUserService.BuildUnitContent(Profile(), autoRestart: true);

        // WantedBy default.target plus enabling is what makes the daemon come back on a boot.
        Assert.Contains("WantedBy=default.target", unit, StringComparison.Ordinal);
        Assert.Contains("Restart=always", unit, StringComparison.Ordinal);
        Assert.Contains("ExecStart=/bin/bash /home/qwen/.config/qwen/serve.sh", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void TurningAutoRestartOffIsWrittenIntoTheUnit()
    {
        // The tray is not always running, so the restart preference has to live in the unit or it means
        // nothing once the tray is gone.
        Assert.Contains("Restart=no", SystemdUserService.BuildUnitContent(Profile(), autoRestart: false), StringComparison.Ordinal);
    }

    [Fact]
    public void TheUnitKeepsEverySettingOutOfSystemdOwnParser()
    {
        // Measured in the distro: systemd expands a specifier such as %s even inside single quotes, so a
        // settings value placed in ExecStart could be rewritten by it. The unit carries derived paths only.
        var settings = new Settings
        {
            Token = "tok%%(pwned)",
            AllowedOrigins = ["https://example.test/%h"],
            ExtraArgs = ["--note=%i"],
        };
        var unit = SystemdUserService.BuildUnitContent(Profile(), settings.AutoRestart);

        Assert.DoesNotContain("tok", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("--note", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTokenIsReadFromAFileAndNeverFromTheUnitArguments()
    {
        var unit = SystemdUserService.BuildUnitContent(Profile(), autoRestart: true);

        // The leading dash means a profile with no token configured has no token file and still starts.
        Assert.Contains("EnvironmentFile=-/home/qwen/.config/qwen/serve.env", unit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfiguringTheUnitWritesItAndEnablesItWithoutStartingAnything()
    {
        var wsl = new FakeWslDistro();
        var unit = new SystemdUserService(wsl);

        Assert.True(await unit.ConfigureAsync(new Settings { Token = "sekret" }, "QwenTray", Profile(), CancellationToken.None));

        // Both files are written as the profile user, which needs no root.
        Assert.Contains(wsl.UserScripts, entry => entry.User == "qwen" && Decode(entry.Script).Contains("WantedBy=default.target", StringComparison.Ordinal));
        Assert.Contains(wsl.UserScripts, entry => entry.User == "qwen" && Decode(entry.Script).Contains("exec '/home/qwen/.local/bin/qwen' 'serve'", StringComparison.Ordinal));
        Assert.Contains(wsl.Scripts, script => script.Contains("install -m '0700' -D", StringComparison.Ordinal));

        // Enabling is the part that survives a boot. Configuring never restarts, because it has to be safe
        // to run while a stray process holds the port, where a start would fail to bind.
        Assert.Contains(wsl.Scripts, script => script.Contains("daemon-reload && systemctl --user enable qwen-serve.service", StringComparison.Ordinal));
        Assert.DoesNotContain(wsl.Scripts, script => script.Contains("restart", StringComparison.Ordinal));

        // The token appears only as stdin, never inside a script body.
        Assert.DoesNotContain("sekret", string.Join('\n', wsl.Scripts), StringComparison.Ordinal);
        Assert.Contains(wsl.ScriptInputs, entry => entry.Input == "QWEN_SERVER_TOKEN=sekret\n");
    }

    [Fact]
    public async Task RestartingIsItsOwnStepAndUsesRestartRatherThanStart()
    {
        // Measured in the distro: start on a live unit keeps the same process, so it cannot apply new
        // settings or recover a wedged one. Restart does both.
        var wsl = new FakeWslDistro();
        var unit = new SystemdUserService(wsl);

        Assert.True(await unit.RestartAsync("QwenTray", Profile(), CancellationToken.None));
        Assert.Contains(wsl.Scripts, script => script.EndsWith("systemctl --user restart qwen-serve.service", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProfileWithNoTokenHasItsTokenFileRemoved()
    {
        // Leaving the file behind would keep an old secret live for the daemon to read.
        var wsl = new FakeWslDistro();
        var unit = new SystemdUserService(wsl);

        Assert.True(await unit.ConfigureAsync(new Settings { Token = "  " }, "QwenTray", Profile(), CancellationToken.None));

        Assert.Contains(wsl.Scripts, script => script.Contains("rm -f -- '/home/qwen/.config/qwen/serve.env'", StringComparison.Ordinal));
        Assert.Empty(wsl.ScriptInputs);
    }

    [Fact]
    public async Task AFailedWriteStopsBeforeTheUnitIsEnabled()
    {
        var wsl = new FakeWslDistro { ScriptExitCode = 1 };
        var unit = new SystemdUserService(wsl);

        Assert.False(await unit.ConfigureAsync(new Settings(), "QwenTray", Profile(), CancellationToken.None));
        Assert.DoesNotContain(wsl.Scripts, script => script.Contains("systemctl --user enable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheUnitStateIsReadFromSystemdOwnAnswerAndNotFromThePort()
    {
        // Measured in the distro: show --value prints ActiveState then UnitFileState, exits 0 even for a
        // unit that does not exist, and leaves the second line empty when it does not.
        var wsl = new FakeWslDistro();
        var unit = new SystemdUserService(wsl);

        wsl.ScriptOutput = "active\nenabled\n";
        var managed = await unit.ReadStateAsync("QwenTray", Profile(), CancellationToken.None);
        Assert.True(managed.Active);
        Assert.True(managed.Enabled);
        Assert.True(managed.Installed);

        // Enabled but stopped. This is what Stop leaves behind, and it must not read as "no unit".
        wsl.ScriptOutput = "inactive\nenabled\n";
        var stopped = await unit.ReadStateAsync("QwenTray", Profile(), CancellationToken.None);
        Assert.False(stopped.Active);
        Assert.True(stopped.Enabled);
        Assert.True(stopped.Installed);

        // Never installed at all, which is the stray-daemon case the start path has to notice.
        wsl.ScriptOutput = "inactive\n\n";
        var absent = await unit.ReadStateAsync("QwenTray", Profile(), CancellationToken.None);
        Assert.False(absent.Active);
        Assert.False(absent.Installed);
    }

    [Fact]
    public async Task ADistroThatAnswersNothingIsReportedAsUnreachableRatherThanAsNoUnit()
    {
        // A distro whose Linux side is wedged and a profile that was never set up both leave systemd
        // saying nothing, and only the first one is worth naming to the user.
        var wsl = new FakeWslDistro { ScriptTimedOut = true };
        var unit = new SystemdUserService(wsl);

        var state = await unit.ReadStateAsync("QwenTray", Profile(), CancellationToken.None);

        Assert.True(state.Unreachable);
        Assert.False(state.Active);
        Assert.False(state.Installed);
    }

    [Fact]
    public async Task StoppingTheUnitDisablesItSoItDoesNotComeBackOnTheNextBoot()
    {
        // A unit that is merely stopped would return the moment anything booted the distro again.
        var wsl = new FakeWslDistro();
        var unit = new SystemdUserService(wsl);

        await unit.StopAsync("QwenTray", Profile(), CancellationToken.None);

        Assert.Contains(wsl.Scripts, script => script.Contains("systemctl --user disable --now qwen-serve.service", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LingeringIsEnabledByRootOnlyWhenTheMarkerIsMissing()
    {
        // The marker file is the distro's own record, so asking for it beats parsing loginctl output.
        var wsl = new FakeWslDistro();
        var unit = new SystemdUserService(wsl);

        Assert.True(await unit.EnsureLingerAsync("QwenTray", Profile(), CancellationToken.None));
        Assert.Contains(wsl.RootScripts, script => script.Contains("test -f /var/lib/systemd/linger/qwen || loginctl enable-linger qwen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AUserNameThatIsNotASingleWordNeverReachesARootScript()
    {
        var wsl = new FakeWslDistro();
        var unit = new SystemdUserService(wsl);
        var profile = new IdentityProfile { Id = "x", Name = "X", LinuxUser = "qwen; rm -rf /" };

        Assert.False(await unit.EnsureLingerAsync("QwenTray", profile, CancellationToken.None));
        Assert.Empty(wsl.RootScripts);
    }

    [Fact]
    public async Task StartingOneProfileStandsTheOthersAside()
    {
        // Each profile keeps its own enabled unit in its own home. Without this, a reboot would start them
        // all and only one of them could bind the port.
        var wsl = new FakeWslDistro();
        var unit = new SystemdUserService(wsl);
        var active = new IdentityProfile { Id = "a", Name = "A", LinuxUser = "qwen" };
        var profiles = new[] { active, new IdentityProfile { Id = "b", Name = "B", LinuxUser = "dev" } };

        await unit.StandAsideOtherProfilesAsync("QwenTray", active, profiles, CancellationToken.None);

        Assert.Contains(wsl.UserScripts, entry => entry.User == "dev" && entry.Script.Contains("disable --now", StringComparison.Ordinal));
        Assert.DoesNotContain(wsl.UserScripts, entry => entry.User == "qwen");
    }

    [Fact]
    public void SettingUpADistroEnablesLingeringForTheProfileItCreates()
    {
        var script = WslDistroProvisioner.BuildCreateUserScript(Settings.CreateDefaultProfile());

        Assert.Contains("useradd -m -s /bin/bash qwen", script, StringComparison.Ordinal);
        Assert.Contains("loginctl enable-linger qwen", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMainPidOfZeroIsReportedAsNoProcess()
    {
        // systemd answers 0 rather than leaving the value empty while the unit has no main process.
        var wsl = new FakeWslDistro { ScriptOutput = "0" };
        var unit = new SystemdUserService(wsl);

        Assert.Null(await unit.ReadMainPidAsync("QwenTray", Profile(), CancellationToken.None));

        wsl.ScriptOutput = "4321";
        Assert.Equal(4321, await unit.ReadMainPidAsync("QwenTray", Profile(), CancellationToken.None));
    }

    [Fact]
    public void TheExposureListensOnTheNetworkAndForwardsToTheLoopbackDaemon()
    {
        var settings = new Settings { Port = 4170, LanProxy = true, LanProxyPort = 4171 };

        var socket = SystemdUserService.BuildProxySocketContent(settings);
        var unit = SystemdUserService.BuildProxyUnitContent(settings);

        // systemd owns the listening port, so the exposure is answered after a boot with no tray running.
        Assert.Contains("ListenStream=0.0.0.0:4171", socket, StringComparison.Ordinal);
        Assert.Contains("WantedBy=sockets.target", socket, StringComparison.Ordinal);

        // The forward target is the daemon's own loopback port, which is what keeps the daemon bound to
        // loopback and its address stable for whatever points at it.
        Assert.Contains($"ExecStart={SystemdUserService.SocketProxyPath} 127.0.0.1:4170", unit, StringComparison.Ordinal);
        Assert.Contains($"Requires={SystemdUserService.ProxySocketUnitName}", unit, StringComparison.Ordinal);
        Assert.Contains($"Requires={SystemdUserService.UnitName}", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExposureDoesNotPutTheForwarderInItsOwnNetworkNamespace()
    {
        // The man page's example sets PrivateNetwork=yes, and its target is a unix socket. This one dials
        // loopback, where a private namespace would give the process its own loopback and nothing behind it,
        // so the exposure would accept connections and forward every one of them into nothing.
        var unit = SystemdUserService.BuildProxyUnitContent(new Settings { LanProxy = true });

        Assert.DoesNotContain("PrivateNetwork", unit, StringComparison.Ordinal);
        Assert.Contains("Type=notify", unit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TurningTheExposureOnWritesBothUnitsAndRestartsTheSocket()
    {
        var wsl = new FakeWslDistro();
        var unit = new SystemdUserService(wsl);

        Assert.True(await unit.ConfigureProxyAsync(new Settings { LanProxy = true, LanProxyPort = 4171 }, "QwenTray", Profile(), CancellationToken.None));

        Assert.Contains(wsl.UserScripts, entry => Decode(entry.Script).Contains("ListenStream=0.0.0.0:4171", StringComparison.Ordinal));
        Assert.Contains(wsl.UserScripts, entry => Decode(entry.Script).Contains("systemd-socket-proxyd 127.0.0.1:4170", StringComparison.Ordinal));

        // Restarting the socket is what re-binds a port an already running socket still holds, which is how
        // a changed exposure port takes effect.
        Assert.Contains(wsl.Scripts, script => script.Contains("daemon-reload && systemctl --user enable qwen-lan-proxy.socket && systemctl --user restart qwen-lan-proxy.socket", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TurningTheExposureOffRemovesBothOfItsUnits()
    {
        // Leaving the files behind would let the next start enable a socket the user just turned off.
        var wsl = new FakeWslDistro();
        var unit = new SystemdUserService(wsl);

        Assert.True(await unit.ConfigureProxyAsync(new Settings { LanProxy = false }, "QwenTray", Profile(), CancellationToken.None));

        Assert.Contains(wsl.UserScripts, entry => entry.Script.Contains("disable --now qwen-lan-proxy.socket qwen-lan-proxy.service", StringComparison.Ordinal));
        Assert.Contains(wsl.Scripts, script => script.Contains("rm -f -- '/home/qwen/.config/systemd/user/qwen-lan-proxy.socket'", StringComparison.Ordinal));
        Assert.Contains(wsl.Scripts, script => script.Contains("rm -f -- '/home/qwen/.config/systemd/user/qwen-lan-proxy.service'", StringComparison.Ordinal));
        Assert.DoesNotContain(wsl.UserScripts, entry => Decode(entry.Script).Contains("[Socket]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnExposureWithoutTheForwarderBinaryFailsBeforeWritingAUnit()
    {
        // The forwarder ships with systemd, so its absence is a distro problem worth naming here rather
        // than a unit that never comes up and says nothing.
        var wsl = new FakeWslDistro { ScriptExitCode = 1 };
        var unit = new SystemdUserService(wsl);

        Assert.False(await unit.ConfigureProxyAsync(new Settings { LanProxy = true }, "QwenTray", Profile(), CancellationToken.None));
        Assert.DoesNotContain(wsl.UserScripts, entry => Decode(entry.Script).Contains("[Socket]", StringComparison.Ordinal));
        Assert.DoesNotContain(wsl.Scripts, script => script.Contains("enable qwen-lan-proxy.socket", StringComparison.Ordinal));
    }

    /// <summary>Reads file content back out of the base64 that a write script carries.</summary>
    private static string Decode(string script)
    {
        var marker = "printf %s '";
        var start = script.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        var from = start + marker.Length;
        var end = script.IndexOf('\'', from);
        return Encoding.UTF8.GetString(Convert.FromBase64String(script[from..end]));
    }
}

/// <summary>
/// The tray hands the daemon over and then only watches it. These pin the two lifecycle promises the
/// requirement is about: an explicit Stop still stops, and exiting the tray does not.
/// </summary>
public sealed class DaemonOutlivesTrayTests
{
    [Fact]
    public async Task QuittingTheTrayLeavesTheDaemonServing()
    {
        var wsl = new FakeWslDistro();
        var supervisor = new DaemonSupervisor(new Settings { AutoRestart = false }, wsl, new SystemdUserService(wsl));

        await supervisor.DisposeAsync();

        // Disposal used to stop the daemon and terminate the distro, which killed the daemon the phone is
        // talking to along with every agent session running in the distro.
        Assert.Empty(wsl.TerminatedDistros);
        Assert.DoesNotContain(wsl.Scripts, script => script.Contains("disable --now", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopStopsTheUnitWithoutTerminatingTheDistro()
    {
        var wsl = new FakeWslDistro();
        var supervisor = new DaemonSupervisor(new Settings(), wsl, new SystemdUserService(wsl));

        await supervisor.StopAsync();

        Assert.Contains(wsl.Scripts, script => script.Contains("systemctl --user disable --now qwen-serve.service", StringComparison.Ordinal));
        // The distro stays up, so anything else running inside it, an agent session included, is untouched.
        Assert.Empty(wsl.TerminatedDistros);
    }

    [Fact]
    public async Task StartingHandsTheDaemonToSystemdAsTheProfileUser()
    {
        var wsl = new FakeWslDistro();
        var settings = new Settings { Profiles = [new IdentityProfile { Id = "d", Name = "D", LinuxUser = "dev" }], ActiveProfileId = "d" };
        var supervisor = new DaemonSupervisor(settings, wsl, new SystemdUserService(wsl));

        await supervisor.StartAsync();

        Assert.Equal(DaemonState.Starting, supervisor.State);
        Assert.Contains(wsl.UserScripts, entry => entry.User == "dev" && entry.Script.Contains("systemctl --user restart qwen-serve.service", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AUnitThatIsActiveAndServingIsAdoptedAndLeftCompletelyAlone()
    {
        // The ordinary case now. The unit outlived the tray, so a fresh tray must leave that daemon, and
        // its settings, exactly as systemd has them.
        var wsl = new FakeWslDistro { ScriptOutput = "active\nenabled\n" };
        await WithServingPortAsync(async port =>
        {
            var supervisor = new DaemonSupervisor(new Settings { Port = port }, wsl, new SystemdUserService(wsl));
            await supervisor.StartAsync();

            Assert.Equal(DaemonState.Running, supervisor.State);
            Assert.True(supervisor.IsDaemonManaged);
            Assert.DoesNotContain(wsl.Scripts, script => script.Contains("systemctl --user restart", StringComparison.Ordinal));
            Assert.DoesNotContain(wsl.Scripts, script => script.Contains("systemctl --user enable", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task AHealthyDaemonThatIsNotOurUnitIsAdoptedButReportedUnmanaged()
    {
        // A stray from an older build answers the port while systemd knows no unit. Adopting it silently
        // would claim a daemon that Stop cannot reach and that dies with the distro, so the tray takes the
        // next start over without restarting the one that is working.
        var wsl = new FakeWslDistro { ScriptOutput = "inactive\n\n" };
        await WithServingPortAsync(async port =>
        {
            var supervisor = new DaemonSupervisor(new Settings { Port = port }, wsl, new SystemdUserService(wsl));
            await supervisor.StartAsync();

            Assert.Equal(DaemonState.Running, supervisor.State);
            Assert.False(supervisor.IsDaemonManaged);
            Assert.Contains(wsl.Scripts, script => script.Contains("systemctl --user enable", StringComparison.Ordinal));
            Assert.DoesNotContain(wsl.Scripts, script => script.Contains("systemctl --user restart", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task AnActiveUnitThatStoppedAnsweringIsRestarted()
    {
        // systemd calls the unit active because the process never exited, so this is the wedged case only
        // the tray can notice. Returning early on "active" here would strand it forever.
        var wsl = new FakeWslDistro { ScriptOutput = "active\nenabled\n" };
        var supervisor = new DaemonSupervisor(new Settings { Port = UnusedPort() }, wsl, new SystemdUserService(wsl));

        await supervisor.StartAsync();

        Assert.Contains(wsl.Scripts, script => script.Contains("systemctl --user restart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SwitchingProfilesStandsThePreviousProfileAsideBeforeChoosingAPath()
    {
        // The profile being switched away from keeps its unit enabled and keeps answering the port, so
        // asking systemd or the port first makes the switch look like this profile is already serving.
        // Two enabled units then compete for one port at the next boot, and the switch never completes.
        var wsl = new FakeWslDistro { ScriptOutput = "inactive\n\n" };
        var settings = new Settings { Profiles = [Profile("qwen"), Profile("dev")], ActiveProfileId = "qwen" };

        await WithServingPortAsync(async port =>
        {
            settings.Port = port;
            var supervisor = new DaemonSupervisor(settings, wsl, new SystemdUserService(wsl));

            await supervisor.StartAsync();

            var stoodAside = wsl.UserScripts.FindIndex(entry => entry.User == "dev" && entry.Script.Contains("disable --now", StringComparison.Ordinal));
            var askedState = wsl.UserScripts.FindIndex(entry => entry.Script.Contains("ActiveState", StringComparison.Ordinal));
            Assert.True(stoodAside >= 0, "the profile being switched away from was never stopped");
            Assert.True(stoodAside < askedState, "the previous profile was still serving while this profile's state was read");
        });
    }

    [Fact]
    public async Task StopStandsTheOtherProfilesUnitsAsideToo()
    {
        // A stopped tray has to mean nothing is serving. Disabling only the active profile's unit would
        // leave the previous profile answering the port while the tray reports Stopped, and the poll
        // returns early on Stopped so it would never be noticed.
        var wsl = new FakeWslDistro { ScriptOutput = "inactive\n\n" };
        var settings = new Settings { Profiles = [Profile("qwen"), Profile("dev")], ActiveProfileId = "qwen" };
        var supervisor = new DaemonSupervisor(settings, wsl, new SystemdUserService(wsl));

        await supervisor.StopAsync();

        Assert.Contains(wsl.UserScripts, entry => entry.User == "dev" && entry.Script.Contains("disable --now", StringComparison.Ordinal));
    }

    private static IdentityProfile Profile(string linuxUser) => new() { Id = linuxUser, Name = linuxUser, LinuxUser = linuxUser };

    [Fact]
    public async Task AWedgedDistroEndsTheStartWithAProblemInsteadOfNothingHappening()
    {
        // Windows keeps calling the distro Running while its Linux side answers nothing at all, so every
        // call stalls. The start has to end somewhere the user can see, and it must not go on to write a
        // unit into a distro it cannot reach.
        var wsl = new FakeWslDistro { ScriptTimedOut = true };
        var supervisor = new DaemonSupervisor(new Settings(), wsl, new SystemdUserService(wsl));

        await supervisor.StartAsync();

        Assert.Equal(DaemonState.Crashed, supervisor.State);
        Assert.NotNull(supervisor.LastProblem);
        Assert.Contains("QwenTray", supervisor.LastProblem, StringComparison.Ordinal);
        Assert.DoesNotContain(wsl.Scripts, script => script.Contains("systemctl --user restart", StringComparison.Ordinal));
        Assert.DoesNotContain(wsl.Scripts, script => script.Contains("systemctl --user enable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStartThatGetsSomewhereClearsTheLastProblem()
    {
        var wsl = new FakeWslDistro { ScriptTimedOut = true };
        var supervisor = new DaemonSupervisor(new Settings(), wsl, new SystemdUserService(wsl));
        await supervisor.StartAsync();
        Assert.NotNull(supervisor.LastProblem);

        // The next attempt finds a distro that answers, so the complaint from the last one is stale.
        wsl.ScriptTimedOut = false;
        wsl.ScriptOutput = "active\nenabled\n";
        await supervisor.StartAsync();

        Assert.Null(supervisor.LastProblem);
    }

    /// <summary>A port nothing can be listening on, so health always fails.</summary>
    private static int UnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.Server.LocalEndPoint!).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Runs a body against a port that answers like a healthy daemon does.</summary>
    private static async Task WithServingPortAsync(Func<int, Task> body)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource();
        var serving = ServeHealthAsync(listener, cancellation.Token);
        try
        {
            await body(((IPEndPoint)listener.Server.LocalEndPoint!).Port);
        }
        finally
        {
            cancellation.Cancel();
            listener.Stop();
            try { await serving; } catch (Exception) { }
        }
    }

    /// <summary>Answers every request with a health response, which is all the tray looks at.</summary>
    private static async Task ServeHealthAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        var payload = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                var stream = client.GetStream();
                var buffer = new byte[1024];
                // What was asked is irrelevant, but a negative count means the peer hung up first.
                if (await stream.ReadAsync(buffer, cancellationToken) < 0) return;
                await stream.WriteAsync(payload, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
        }
    }
}
