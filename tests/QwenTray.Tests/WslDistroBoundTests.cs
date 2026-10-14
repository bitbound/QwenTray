using System.Diagnostics;
using Bitbound.SystemAbstractions;
using Bitbound.SystemAbstractions.Processes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QwenTray.Services;
using Xunit;

namespace QwenTray.Tests;

/// <summary>
/// The bound on a wsl.exe call. These use a real process rather than a double, because the thing that
/// has to be proven is that a command which never answers is abandoned, and a double answers at once.
/// </summary>
public sealed class WslDistroBoundTests
{
    private static IProcessManager Processes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSystemAbstractions();
        return services.BuildServiceProvider().GetRequiredService<IProcessManager>();
    }

    private static WslDistro CmdDistro() => new(Processes(), "cmd.exe");

    [WindowsFact]
    public async Task ACommandThatOutlivesTheBoundIsAbandonedAndReportedAsATimeout()
    {
        // Measured on this box: a distro whose Linux side is wedged accepts the process and then answers
        // nothing at all. A sleeping ping is a real process that outlives the bound, so the call has to
        // come back anyway and say why rather than wait for it.
        var started = Stopwatch.GetTimestamp();
        var invocation = new WslInvocation("/c ping -n 8 127.0.0.1", WslDistro.CommandOutputEncoding, TimeSpan.FromSeconds(1));

        var result = await CmdDistro().ExecuteAsync(invocation, input: null, CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.Contains("did not answer", result.Output, StringComparison.Ordinal);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(6), "the call waited for the process instead of stopping at the bound");
    }

    [WindowsFact]
    public async Task ACommandThatAnswersInsideTheBoundIsReturnedNormally()
    {
        var invocation = new WslInvocation("/c echo ready", WslDistro.CommandOutputEncoding, TimeSpan.FromSeconds(30));

        var result = await CmdDistro().ExecuteAsync(invocation, input: null, CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.True(result.Succeeded);
        Assert.Contains("ready", result.Output, StringComparison.Ordinal);
    }
}
