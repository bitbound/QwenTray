using QwenTray;
using Xunit;

namespace QwenTray.Tests;

// The ServiceProvider in Program.Main is the sole disposer of the supervisor singleton. These
// pin that disposal is idempotent and that Stop/Start sequences around it never touch a
// disposed shutdown token source.
public sealed class DaemonSupervisorDisposalTests
{
    private static DaemonSupervisor CreateSupervisor() => new(new Settings { AutoRestart = false });

    [Fact]
    public async Task DisposingTwiceDoesNotThrow()
    {
        var supervisor = CreateSupervisor();
        await supervisor.DisposeAsync();
        await supervisor.DisposeAsync();
    }

    [Fact]
    public async Task DisposingAfterStopDoesNotThrow()
    {
        var supervisor = CreateSupervisor();
        await supervisor.StopAsync();
        await supervisor.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentDisposalDoesNotThrow()
    {
        var supervisor = CreateSupervisor();
        var first = supervisor.DisposeAsync().AsTask();
        var second = supervisor.DisposeAsync().AsTask();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task StartAfterDisposalIsIgnored()
    {
        var supervisor = CreateSupervisor();
        await supervisor.DisposeAsync();
        await supervisor.StartAsync();
        Assert.Equal(DaemonState.Stopped, supervisor.State);
        Assert.Null(supervisor.DaemonProcessId);
    }
}
