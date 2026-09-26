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
}
