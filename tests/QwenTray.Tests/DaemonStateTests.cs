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

public sealed class SettingsTests
{
    [Fact]
    public void DefaultsRoundTripThroughSourceGeneratedJson()
    {
        var settings = new Settings();
        var json = System.Text.Json.JsonSerializer.Serialize(settings, QwenTrayJsonContext.Default.Settings);
        var restored = System.Text.Json.JsonSerializer.Deserialize(json, QwenTrayJsonContext.Default.Settings);
        Assert.NotNull(restored);
        Assert.Equal(settings.QwenExecutable, restored.QwenExecutable);
        Assert.Equal(settings.Host, restored.Host);
        Assert.Equal(settings.Port, restored.Port);
        Assert.Equal(settings.HealthPollSeconds, restored.HealthPollSeconds);
        Assert.Equal(settings.AutoRestart, restored.AutoRestart);
        Assert.Equal(settings.MaxBackoffSeconds, restored.MaxBackoffSeconds);
        Assert.Equal(settings.WorkspaceDirectory, restored.WorkspaceDirectory);
        Assert.Empty(restored.ExtraArgs);
    }

    [Fact]
    public void WorkspaceDefaultsToUserProfileDocumentsQwen()
    {
        var settings = new Settings();
        Assert.EndsWith(@"Documents\Qwen", settings.WorkspaceDirectory, StringComparison.Ordinal);
        Assert.Contains("USERPROFILE", settings.WorkspaceDirectory, StringComparison.Ordinal);
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
    public void DefaultsProduceOnlyTheCoreFlags()
    {
        var args = DaemonSupervisor.BuildArguments(new Settings());
        Assert.Equal(["serve", "--hostname", "127.0.0.1", "--port", "4170"], args);
    }

    [Fact]
    public void EveryEnabledOptionBecomesItsFlag()
    {
        var args = DaemonSupervisor.BuildArguments(new Settings
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
        });
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

    [Fact]
    public void ValidationFlagsMismatchedTlsPair()
    {
        Assert.Contains(DaemonSupervisor.ValidateSettings(new Settings { TlsCertPath = "a" }), w => w.Contains("tls", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(DaemonSupervisor.ValidateSettings(new Settings { TlsKeyPath = "b" }), w => w.Contains("tls", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(DaemonSupervisor.ValidateSettings(new Settings()));
        Assert.Empty(DaemonSupervisor.ValidateSettings(new Settings { TlsCertPath = "a", TlsKeyPath = "b" }));
    }
}
