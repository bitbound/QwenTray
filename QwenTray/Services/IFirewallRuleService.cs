namespace QwenTray.Services;

/// <summary>How a firewall rule attempt ended.</summary>
public enum FirewallRuleOutcome
{
    /// <summary>The rule is in place for the requested port.</summary>
    Created,

    /// <summary>The rule was taken back out.</summary>
    Removed,

    /// <summary>Nothing was there to remove, so no prompt was shown.</summary>
    AlreadyAbsent,

    /// <summary>The user dismissed the Windows consent prompt.</summary>
    Cancelled,

    /// <summary>Windows refused the change.</summary>
    Failed,
}

/// <param name="Outcome">How the attempt ended.</param>
/// <param name="Detail">What netsh reported, which is empty on success and on a declined prompt.</param>
public sealed record FirewallRuleResult(FirewallRuleOutcome Outcome, string Detail)
{
    public static FirewallRuleResult Created { get; } = new(FirewallRuleOutcome.Created, string.Empty);

    public static FirewallRuleResult Removed { get; } = new(FirewallRuleOutcome.Removed, string.Empty);

    public static FirewallRuleResult AlreadyAbsent { get; } = new(FirewallRuleOutcome.AlreadyAbsent, string.Empty);

    public static FirewallRuleResult Cancelled { get; } = new(FirewallRuleOutcome.Cancelled, string.Empty);

    public static FirewallRuleResult Failed(string detail) => new(FirewallRuleOutcome.Failed, detail);
}

/// <summary>
/// Opens and closes the one Windows Firewall rule the tray's LAN exposure needs. Only the writes
/// need an elevated process, so each one raises a Windows consent prompt rather than the whole tray
/// running as an administrator.
/// </summary>
public interface IFirewallRuleService
{
    /// <summary>
    /// Replaces any existing rule of the tray's name with one that allows inbound TCP on the port,
    /// so a port change cannot leave the old opening behind.
    /// </summary>
    Task<FirewallRuleResult> AllowInboundPortAsync(int port, CancellationToken cancellationToken);

    /// <summary>Takes the rule away. A rule that is already gone is reported without a prompt.</summary>
    Task<FirewallRuleResult> RemoveAsync(CancellationToken cancellationToken);
}