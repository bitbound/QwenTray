using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Bitbound.SystemAbstractions.Processes;
using Microsoft.Extensions.Logging;

namespace QwenTray.Services;

/// <inheritdoc cref="IFirewallRuleService"/>
/// <remarks>
/// The rule is written through netsh, whose arguments are read back by tests. An elevated call cannot
/// redirect its output, because Windows only honors the elevation verb when the shell executes the
/// process rather than creating it directly. The exit code is therefore the whole of the answer.
/// </remarks>
public sealed class FirewallRuleService(IProcessManager processes, ILogger<FirewallRuleService> logger) : IFirewallRuleService
{
    /// <summary>
    /// The name the rule carries. The tray owns exactly one, so a second press replaces the first
    /// rather than adding to it.
    /// </summary>
    internal const string RuleName = "QwenTray";

    /// <summary>ShellExecute error code for a consent prompt the user dismissed.</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>netsh exits with this when a rule name matches nothing.</summary>
    private const int NoSuchRuleExitCode = 1;

    private const string Netsh = "netsh.exe";

    public async Task<FirewallRuleResult> AllowInboundPortAsync(int port, CancellationToken cancellationToken)
    {
        logger.LogInformation("Opening a Windows Firewall rule named {Rule} for inbound TCP on port {Port}.", RuleName, port);

        // Both halves run under one consent prompt. A port change has to take the old opening with it,
        // and a second rule under the same name would leave the previous port listening.
        var command = string.Join('&', [
            BuildDeleteCommand(),
            BuildAddCommand(port),
        ]);

        return await RunElevatedAsync($"/c {command}", FirewallRuleResult.Created, cancellationToken);
    }

    public async Task<FirewallRuleResult> RemoveAsync(CancellationToken cancellationToken)
    {
        // Reading a rule needs no elevation, so a rule that is already gone is settled here rather than
        // behind a consent prompt the user would answer for no reason.
        if (!await RuleExistsAsync(cancellationToken))
        {
            logger.LogInformation("No Windows Firewall rule named {Rule} was present.", RuleName);
            return FirewallRuleResult.AlreadyAbsent;
        }

        logger.LogInformation("Removing the Windows Firewall rule named {Rule}.", RuleName);

        return await RunElevatedAsync($"/c {BuildDeleteCommand()}", FirewallRuleResult.Removed, cancellationToken);
    }

    /// <summary>
    /// The add half. The port is formatted invariantly so a locale that writes digits differently
    /// cannot hand netsh a port it will not parse.
    /// </summary>
    internal static string BuildAddCommand(int port)
        => $"netsh advfirewall firewall add rule name={RuleName} dir=in action=allow protocol=TCP localport={port.ToString(CultureInfo.InvariantCulture)}";

    internal static string BuildDeleteCommand()
        => $"netsh advfirewall firewall delete rule name={RuleName}";

    private async Task<bool> RuleExistsAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(Netsh)
        {
            Arguments = $"advfirewall firewall show rule name={RuleName}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = processes.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        // Both pipes are drained before the wait, because netsh writes its verdict to standard output
        // and a full buffer on either one would leave the process unable to exit.
        var output = process.StandardOutput?.ReadToEndAsync(cancellationToken) ?? Task.FromResult(string.Empty);
        var error = process.StandardError?.ReadToEndAsync(cancellationToken) ?? Task.FromResult(string.Empty);
        await Task.WhenAll(output, error);
        await process.WaitForExitAsync(cancellationToken);

        return process.ExitCode != NoSuchRuleExitCode;
    }

    /// <summary>
    /// Runs one elevated command and reads its exit code. A dismissed consent prompt throws rather
    /// than returning a code, so it is caught here and reported as its own outcome. The caller names
    /// the success result, because a clean exit means whatever the command was for.
    /// </summary>
    private async Task<FirewallRuleResult> RunElevatedAsync(string arguments, FirewallRuleResult onSuccess, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            Arguments = arguments,
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        IProcess? process;
        try
        {
            process = processes.Start(startInfo);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            logger.LogInformation("The Windows consent prompt for the firewall rule was dismissed.");
            return FirewallRuleResult.Cancelled;
        }

        if (process is null)
        {
            return FirewallRuleResult.Failed("Windows could not start the elevated command.");
        }

        await process.WaitForExitAsync(cancellationToken);

        return process.ExitCode == 0
            ? onSuccess
            : FirewallRuleResult.Failed($"netsh exited with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}.");
    }
}