using Bitbound.SystemAbstractions.Processes;
using QwenTray;
using QwenTray.Services;
using Xunit;

namespace QwenTray.Tests;

/// <summary>A distro double that records nothing and never shells out.</summary>
internal sealed class FakeWslDistro : IWslDistro
{
    public Task<bool> DistroExistsAsync(string distro, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<WslCommandResult> RunScriptAsync(string distro, string user, string script, CancellationToken cancellationToken)
        => Task.FromResult(new WslCommandResult(0, string.Empty));

    public Task<WslCommandResult> RunRootScriptAsync(string distro, string script, CancellationToken cancellationToken)
        => Task.FromResult(new WslCommandResult(0, string.Empty));

    public Task<WslCommandResult> InstallDistroAsync(string distro, CancellationToken cancellationToken)
        => Task.FromResult(new WslCommandResult(0, string.Empty));

    public Task<WslCommandResult> TerminateAsync(string distro, CancellationToken cancellationToken)
        => Task.FromResult(new WslCommandResult(0, string.Empty));

    public IProcess StartDaemon(Settings settings, string distro, IdentityProfile profile)
        => throw new NotSupportedException("These tests never start a daemon.");
}

// The ServiceProvider in Program.Main is the sole disposer of the supervisor singleton. These
// pin that disposal is idempotent and that Stop/Start sequences around it never touch a
// disposed shutdown token source.
public sealed class DaemonSupervisorDisposalTests
{
    private static DaemonSupervisor CreateSupervisor() => new(new Settings { AutoRestart = false }, new FakeWslDistro());

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
