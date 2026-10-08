using QwenTray;
using Xunit;

namespace QwenTray.Tests;

public sealed class DaemonStateTests
{
    [Fact]
    public void StartAndHealthyCheckMoveToRunning()
    {
        var state = new DaemonStateMachine();
        state.BeginStart();
        Assert.Equal(DaemonState.Starting, state.State);
        state.HealthSucceeded();
        Assert.Equal(DaemonState.Running, state.State);
    }

    [Fact]
    public void UnexpectedExitCrashesAndIncreasesBackoff()
    {
        var state = new DaemonStateMachine();
        state.BeginStart();
        state.UnexpectedExit();
        Assert.Equal(DaemonState.Crashed, state.State);
        Assert.Equal(1, state.BackoffSeconds(60));
        state.UnexpectedExit();
        Assert.Equal(2, state.BackoffSeconds(60));
        state.UnexpectedExit();
        Assert.Equal(4, state.BackoffSeconds(60));
        for (var i = 0; i < 10; i++) state.UnexpectedExit();
        Assert.Equal(60, state.BackoffSeconds(60));
    }

    [Fact]
    public void UserStopClearsCrashStateAndFailureCount()
    {
        var state = new DaemonStateMachine();
        state.BeginStart();
        state.UnexpectedExit();
        state.UserStopped();
        Assert.Equal(DaemonState.Stopped, state.State);
        Assert.Equal(0, state.ConsecutiveFailures);
    }

    [Fact]
    public void AdoptionMarksDaemonRunningWithoutFailureHistory()
    {
        var state = new DaemonStateMachine();
        state.Adopt();
        Assert.Equal(DaemonState.Running, state.State);
        Assert.Equal(0, state.ConsecutiveFailures);
    }
}

public sealed class DaemonCommandAvailabilityTests
{
    [Theory]
    [InlineData(DaemonState.Stopped, true, false, false)]
    [InlineData(DaemonState.Starting, false, true, false)]
    [InlineData(DaemonState.Running, false, true, true)]
    [InlineData(DaemonState.Crashed, false, false, true)]
    public void AvailabilityFollowsState(DaemonState state, bool canStart, bool canStop, bool canRestart)
    {
        var availability = DaemonCommandAvailability.From(state);
        Assert.Equal(canStart, availability.CanStart);
        Assert.Equal(canStop, availability.CanStop);
        Assert.Equal(canRestart, availability.CanRestart);
    }

    [Theory]
    [InlineData(DaemonState.Stopped)]
    [InlineData(DaemonState.Starting)]
    [InlineData(DaemonState.Running)]
    [InlineData(DaemonState.Crashed)]
    public void BusyDisablesEveryCommand(DaemonState state)
    {
        var availability = DaemonCommandAvailability.From(state, busy: true);
        Assert.False(availability.CanStart);
        Assert.False(availability.CanStop);
        Assert.False(availability.CanRestart);
    }
}

public sealed class SettingsTests
{
    [Fact]
    public void DefaultsRoundTripThroughSourceGeneratedJson()
    {
        var settings = new Settings();
        var json = System.Text.Json.JsonSerializer.Serialize(settings, QwenTrayJsonContext.Default.Settings);
        var restored = System.Text.Json.JsonSerializer.Deserialize(json, QwenTrayJsonContext.Default.Settings);
        Assert.NotNull(restored);
        Assert.Equal(settings.Host, restored.Host);
        Assert.Equal(settings.Port, restored.Port);
        Assert.Equal(settings.HealthPollSeconds, restored.HealthPollSeconds);
        Assert.Equal(settings.AutoRestart, restored.AutoRestart);
        Assert.Equal(settings.MaxBackoffSeconds, restored.MaxBackoffSeconds);
        Assert.Equal(settings.WslDistroName, restored.WslDistroName);
        Assert.Empty(restored.ExtraArgs);
    }

    [Fact]
    public void DefaultProfilePathsAreLinuxAndPerUser()
    {
        var profile = Settings.CreateDefaultProfile();
        Assert.Equal("/home/qwen", profile.HomeDirectory);
        Assert.Equal("/home/qwen/.local/bin/qwen", profile.ResolvedQwenExecutable);
        Assert.Equal("/home/qwen/workspace", profile.ResolvedWorkspaceDirectory);
        Assert.StartsWith("/", profile.ResolvedQwenExecutable, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileRoundTripsWithItsIdentities()
    {
        var settings = new Settings
        {
            ActiveProfileId = "work",
            Profiles =
            [
                new IdentityProfile { Id = "home", Name = "Home", LinuxUser = "home", WorkspaceDirectory = "/home/home/w" },
                new IdentityProfile { Id = "work", Name = "Work", LinuxUser = "workuser", QwenExecutable = "/opt/qwen/bin/qwen" },
            ],
        };
        var json = System.Text.Json.JsonSerializer.Serialize(settings, QwenTrayJsonContext.Default.Settings);
        var restored = System.Text.Json.JsonSerializer.Deserialize(json, QwenTrayJsonContext.Default.Settings);
        Assert.NotNull(restored);
        Assert.Equal(2, restored.Profiles.Length);
        Assert.Equal("work", restored.ResolveActiveProfile().Id);
        Assert.Equal("/opt/qwen/bin/qwen", restored.Profiles[1].ResolvedQwenExecutable);
        Assert.Equal("/home/home/.local/bin/qwen", restored.Profiles[0].ResolvedQwenExecutable);
    }

    [Fact]
    public void ResolveActiveProfileFallsBackWhenTheIdIsMissing()
    {
        var settings = new Settings
        {
            ActiveProfileId = "gone",
            Profiles = [new IdentityProfile { Id = "only", Name = "Only", LinuxUser = "only" }],
        };
        Assert.Equal("only", settings.ResolveActiveProfile().Id);

        var empty = new Settings { ActiveProfileId = "gone" };
        Assert.Equal("default", empty.ResolveActiveProfile().Id);
    }

    [Fact]
    public void AllDaemonFieldsRoundTripThroughSourceGeneratedJson()
    {
        var settings = new Settings
        {
            Token = "sekret",
            RequireAuth = true,
            EnableSessionShell = true,
            ServeWeb = false,
            LocalControl = true,
            AllowedOrigins = ["https://a.example", "https://b.example"],
            TlsCertPath = @"C:\certs\a.pem",
            TlsKeyPath = @"C:\certs\a.key",
            MaxSessions = 8,
            MaxConnections = 64,
            EventRingSize = 100,
            RateLimit = true,
            DebugMode = true,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(settings, QwenTrayJsonContext.Default.Settings);
        var restored = System.Text.Json.JsonSerializer.Deserialize(json, QwenTrayJsonContext.Default.Settings);
        Assert.NotNull(restored);
        Assert.Equal(settings.Token, restored.Token);
        Assert.True(restored.RequireAuth);
        Assert.True(restored.EnableSessionShell);
        Assert.False(restored.ServeWeb);
        Assert.True(restored.LocalControl);
        Assert.Equal(settings.AllowedOrigins, restored.AllowedOrigins);
        Assert.Equal(settings.TlsCertPath, restored.TlsCertPath);
        Assert.Equal(settings.MaxSessions, restored.MaxSessions);
        Assert.Equal(settings.MaxConnections, restored.MaxConnections);
        Assert.Equal(settings.EventRingSize, restored.EventRingSize);
        Assert.True(restored.RateLimit);
        Assert.True(restored.DebugMode);
    }
}

public sealed class DaemonArgumentsTests
{
    [Fact]
    public void DefaultsProduceTheWslPrefixThenTheCoreFlags()
    {
        var settings = new Settings();
        var args = DaemonSupervisor.BuildArguments(settings, settings.WslDistroName, settings.ResolveActiveProfile());
        Assert.Equal(["-d", "QwenTray", "-u", "qwen", "-e", "/home/qwen/.local/bin/qwen", "serve", "--hostname", "127.0.0.1", "--port", "4170"], args);
    }

    [Fact]
    public void EveryEnabledOptionBecomesItsFlag()
    {
        var settings = new Settings
        {
            Token = "tok",
            RequireAuth = true,
            EnableSessionShell = true,
            ServeWeb = false,
            LocalControl = true,
            AllowedOrigins = ["https://a.example", "  ", "https://b.example"],
            TlsCertPath = @"C:\c.pem",
            TlsKeyPath = @"C:\c.key",
            MaxSessions = 4,
            MaxConnections = 16,
            EventRingSize = 50,
            RateLimit = true,
            DebugMode = true,
            ExtraArgs = ["--safe-mode"],
        };
        var args = DaemonSupervisor.BuildArguments(settings, settings.WslDistroName, settings.ResolveActiveProfile());
        Assert.Contains("--token", args);
        Assert.Contains("tok", args);
        Assert.Contains("--require-auth", args);
        Assert.Contains("--enable-session-shell", args);
        Assert.Contains("--no-web", args);
        Assert.Contains("--local-control", args);
        Assert.Equal(2, args.FindAll(a => a == "--allow-origin").Count);
        Assert.Contains("https://a.example", args);
        Assert.Contains("https://b.example", args);
        Assert.DoesNotContain("  ", args);
        Assert.Contains("--tls-cert", args);
        Assert.Contains("--tls-key", args);
        Assert.Contains("--max-sessions", args);
        Assert.Contains("4", args);
        Assert.Contains("--max-connections", args);
        Assert.Contains("16", args);
        Assert.Contains("--event-ring-size", args);
        Assert.Contains("50", args);
        Assert.Contains("--rate-limit", args);
        Assert.Contains("--debug", args);
        Assert.Contains("--safe-mode", args);
    }

    [Fact]
    public void TokenlessLoopbackNeedsNoBearerButTokenAndRequireAuthDo()
    {
        Assert.False(new Settings().UsesBearerAuth);
        Assert.True(new Settings { Token = "x" }.UsesBearerAuth);
        Assert.True(new Settings { RequireAuth = true }.UsesBearerAuth);
    }

    [Fact]
    public void TlsSchemeFollowsCertPresence()
    {
        Assert.Equal("http", new Settings().HealthUri.Scheme);
        Assert.Equal("https", new Settings { TlsCertPath = @"C:\c.pem", TlsKeyPath = @"C:\c.key" }.HealthUri.Scheme);
    }

    [Theory]
    [InlineData("0.0.0.0", "127.0.0.1")]
    [InlineData("::", "127.0.0.1")]
    [InlineData("[::]", "127.0.0.1")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("localhost", "localhost")]
    [InlineData("192.168.0.166", "192.168.0.166")]
    public void WebUriMapsWildcardBindToLoopback(string host, string expectedHost)
    {
        var settings = new Settings { Host = host };
        Assert.Equal(expectedHost, settings.WebUri.Host);
        Assert.Equal("127.0.0.1", settings.HealthUri.Host);
        Assert.Equal("127.0.0.1", settings.LoopbackUri.Host);
    }

    [Fact]
    public void ValidationFlagsMismatchedTlsPair()
    {
        Assert.Contains(DaemonSupervisor.ValidateSettings(new Settings { TlsCertPath = "a" }), w => w.Contains("tls", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(DaemonSupervisor.ValidateSettings(new Settings { TlsKeyPath = "b" }), w => w.Contains("tls", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(DaemonSupervisor.ValidateSettings(new Settings()));
        Assert.Empty(DaemonSupervisor.ValidateSettings(new Settings { TlsCertPath = "a", TlsKeyPath = "b" }));
    }

    [Fact]
    public void LocalControlRequiresLoopbackAndWebShell()
    {
        Assert.Empty(DaemonSupervisor.ValidateSettings(new Settings { LocalControl = true }));
        Assert.Contains(DaemonSupervisor.ValidateSettings(new Settings { LocalControl = true, Host = "::1" }), warning => warning.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(DaemonSupervisor.ValidateSettings(new Settings { LocalControl = true, Host = "0.0.0.0" }), warning => warning.Contains("main daemon address", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(DaemonSupervisor.ValidateSettings(new Settings { LocalControl = true, ServeWeb = false }), warning => warning.Contains("Web Shell", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(DaemonSupervisor.ValidateSettings(new Settings { LocalControl = true, ExtraArgs = ["--hostname", "0.0.0.0"] }), warning => warning.Contains("--hostname", StringComparison.Ordinal));
        Assert.Contains(DaemonSupervisor.ValidateSettings(new Settings { LocalControl = true, ExtraArgs = ["--no-web"] }), warning => warning.Contains("--no-web", StringComparison.Ordinal));
    }

    [Fact]
    public void LocalControlStatusAcceptsOnlyActiveAbsolutePairingUrls()
    {
        using var active = System.Text.Json.JsonDocument.Parse("{\"active\":true,\"url\":\"http://192.168.1.5:4170/#token=secret\"}");
        Assert.True(DaemonSupervisor.TryReadLocalControlPairingUrl(active.RootElement, out var url));
        Assert.Equal("http://192.168.1.5:4170/#token=secret", url);

        using var inactive = System.Text.Json.JsonDocument.Parse("{\"active\":false,\"url\":\"http://192.168.1.5:4170/#token=secret\"}");
        Assert.False(DaemonSupervisor.TryReadLocalControlPairingUrl(inactive.RootElement, out _));

        using var redacted = System.Text.Json.JsonDocument.Parse("{\"active\":true,\"urlRedacted\":true}");
        Assert.False(DaemonSupervisor.TryReadLocalControlPairingUrl(redacted.RootElement, out _));
    }

    [Fact]
    public void ValidationRejectsAnEmptyDistroOrUser()
    {
        Assert.Contains(DaemonSupervisor.ValidateSettings(new Settings { WslDistroName = "  " }), w => w.Contains("distro", StringComparison.OrdinalIgnoreCase));
        var noUser = new Settings { Profiles = [new IdentityProfile { Id = "x", Name = "X", LinuxUser = "  " }] };
        Assert.Contains(DaemonSupervisor.ValidateSettings(noUser), w => w.Contains("Linux user", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnixLogPathMapsOntoTheDistroShare()
    {
        Assert.Equal(@"\\wsl.localhost\QwenTray\home\qwen\.qwen\daemon.log", DaemonSupervisor.ToUncPath("QwenTray", "/home/qwen/.qwen/daemon.log"));
        Assert.Equal(string.Empty, DaemonSupervisor.ToUncPath("QwenTray", "relative/path.log"));
        Assert.Equal(string.Empty, DaemonSupervisor.ToUncPath("QwenTray", "   "));
    }
}
