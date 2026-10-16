using Bitbound.SystemAbstractions.Processes;
using QwenTray.Services;
using Xunit;

namespace QwenTray.Tests;

/// <summary>A distro double that records nothing and never shells out.</summary>
internal sealed class FakeWslDistro : IWslDistro
{
    public List<string> Scripts { get; } = [];

    public List<string> RootScripts { get; } = [];

    public List<(string User, string Script)> UserScripts { get; } = [];

    /// <summary>Stdin handed to each script, so a caller can prove a secret travelled that way.</summary>
    public List<(string Script, string Input)> ScriptInputs { get; } = [];

    public List<string> UnregisteredDistros { get; } = [];

    public List<string> TerminatedDistros { get; } = [];

    public List<(string Distro, string TarPath)> Exports { get; } = [];

    public List<(string Distro, string TargetDirectory, string TarPath)> Imports { get; } = [];

    /// <summary>How many times a caller tried to boot the distro through this double.</summary>
    public int RunScriptCalls { get; private set; }

    /// <summary>Holds a script open so a caller can watch the daemon mid-stop.</summary>
    public TaskCompletionSource? ScriptGate { get; set; }

    /// <summary>When set, running a script throws the way a missing wsl.exe would.</summary>
    public Exception? ScriptFailure { get; set; }

    public int ImportExitCode { get; set; }

    public int ExportExitCode { get; set; }

    public string ScriptOutput { get; set; } = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5 test@qwentray";

    public int ScriptExitCode { get; set; }

    /// <summary>
    /// Makes every script answer look like wsl.exe was abandoned rather than answered, which is what a
    /// distro whose Linux side is wedged produces. A real one is a failure with no exit code at all.
    /// </summary>
    public bool ScriptTimedOut { get; set; }

    public int UnregisterExitCode { get; set; }

    public Task<bool> DistroExistsAsync(string distro, CancellationToken cancellationToken) => Task.FromResult(true);

    public async Task<WslCommandResult> RunScriptAsync(string distro, string user, string script, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        RunScriptCalls++;
        Scripts.Add(script);
        UserScripts.Add((user, script));
        if (ScriptFailure is not null) throw ScriptFailure;
        if (ScriptGate is { } gate) await gate.Task;
        return new WslCommandResult(ScriptTimedOut ? -1 : ScriptExitCode, ScriptOutput) { TimedOut = ScriptTimedOut };
    }

    public Task<WslCommandResult> RunScriptWithInputAsync(string distro, string user, string script, string input, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        RunScriptCalls++;
        Scripts.Add(script);
        ScriptInputs.Add((script, input));
        return Task.FromResult(new WslCommandResult(0, string.Empty));
    }

    public Task<WslCommandResult> RunRootScriptAsync(string distro, string script, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        RootScripts.Add(script);
        return Task.FromResult(new WslCommandResult(0, string.Empty));
    }

    public Task<WslCommandResult> InstallDistroAsync(string distro, CancellationToken cancellationToken)
        => Task.FromResult(new WslCommandResult(0, string.Empty));

    public Task<WslCommandResult> TerminateAsync(string distro, CancellationToken cancellationToken)
    {
        TerminatedDistros.Add(distro);
        return Task.FromResult(new WslCommandResult(0, string.Empty));
    }

    public Task<WslCommandResult> UnregisterAsync(string distro, CancellationToken cancellationToken)
    {
        UnregisteredDistros.Add(distro);
        return Task.FromResult(new WslCommandResult(UnregisterExitCode, UnregisterExitCode == 0 ? string.Empty : "The distribution does not exist."));
    }

    public Task<WslCommandResult> ExportAsync(string distro, string tarPath, CancellationToken cancellationToken)
    {
        Exports.Add((distro, tarPath));
        return Task.FromResult(new WslCommandResult(ExportExitCode, ExportExitCode == 0 ? string.Empty : "The distribution is not found."));
    }

    public Task<WslCommandResult> ImportAsync(string distro, string targetDirectory, string tarPath, CancellationToken cancellationToken)
    {
        Imports.Add((distro, targetDirectory, tarPath));
        return Task.FromResult(new WslCommandResult(ImportExitCode, ImportExitCode == 0 ? string.Empty : "The distribution name already exists."));
    }
}

// The ServiceProvider in Program.Main is the sole disposer of the supervisor singleton. These
// pin that disposal is idempotent and that Stop/Start sequences around it never touch a
// disposed shutdown token source.
public sealed class DaemonSupervisorDisposalTests
{
    internal static DaemonSupervisor CreateSupervisor(Settings? settings = null, FakeWslDistro? wsl = null)
    {
        var distro = wsl ?? new FakeWslDistro();
        return new DaemonSupervisor(settings ?? new Settings { AutoRestart = false }, distro, new SystemdUserService(distro, null!));
    }

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
