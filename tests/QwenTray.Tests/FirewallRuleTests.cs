using QwenTray.Services;
using Xunit;

namespace QwenTray.Tests;

/// <summary>
/// The Server page's two firewall buttons. The page has to ask for the port the user typed rather
/// than the saved one, because the field is editable without a save. The netsh arguments are pinned
/// here because they are the only place the rule's name and shape are decided.
/// </summary>
public sealed class FirewallRuleTests
{
    [Fact]
    public async Task AllowUsesThePortInTheFieldRatherThanTheSavedSetting()
    {
        // The page is a draft until the shared save runs, so a button that read the settings would
        // open the port from the last save rather than the one on screen.
        var pages = new SettingsPages(Exposure());
        await pages.InitializeAsync();

        pages.Server.LanProxyPort = "4999";
        var reported = await Report(pages, () => pages.Server.AllowFirewallPortCommand.ExecuteAsync(null));

        Assert.Equal([4999], pages.Firewall.AllowedPorts);
        Assert.Contains(reported, message => message.Kind == SnackbarKind.Success && message.Text.Contains("4999", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AllowRefusesAPortTheFieldCannotHold()
    {
        // Raising a consent prompt for a port netsh will reject spends the user's one attention on it.
        var pages = new SettingsPages(Exposure());
        await pages.InitializeAsync();
        pages.Server.LanProxyPort = "70000";

        var reported = await Report(pages, () => pages.Server.AllowFirewallPortCommand.ExecuteAsync(null));

        Assert.Empty(pages.Firewall.AllowedPorts);
        Assert.Contains(reported, message => message.Kind == SnackbarKind.Warning && message.Text.Contains("between 1 and 65535", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADismissedConsentPromptIsAWarningRatherThanAnError()
    {
        // The user chose not to approve, so nothing is broken and there is nothing to retry.
        var pages = new SettingsPages(Exposure());
        await pages.InitializeAsync();
        pages.Firewall.NextResult = FirewallRuleResult.Cancelled;

        var reported = await Report(pages, () => pages.Server.AllowFirewallPortCommand.ExecuteAsync(null));

        Assert.Contains(reported, message => message.Kind == SnackbarKind.Warning && message.Text.Contains("dismissed", StringComparison.Ordinal));
        Assert.DoesNotContain(reported, message => message.Kind == SnackbarKind.Error);
    }

    [Fact]
    public async Task ARefusedChangeCarriesNetshsExitCode()
    {
        // netsh's own wording is not captured, because an elevated process cannot redirect it. The exit
        // code is what is left to say which way it failed.
        var pages = new SettingsPages(Exposure());
        await pages.InitializeAsync();
        pages.Firewall.NextResult = FirewallRuleResult.Failed("netsh exited with code 1.");

        await pages.Server.AllowFirewallPortCommand.ExecuteAsync(null);

        Assert.Contains(pages.Snackbars.Messages, message => message.Kind == SnackbarKind.Error && message.Text.Contains("code 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemovingAnAbsentRuleIsNotAnError()
    {
        var pages = new SettingsPages(Exposure());
        await pages.InitializeAsync();
        pages.Firewall.NextResult = FirewallRuleResult.AlreadyAbsent;

        var reported = await Report(pages, () => pages.Server.RemoveFirewallRuleCommand.ExecuteAsync(null));

        Assert.Equal(1, pages.Firewall.RemoveCalls);
        Assert.DoesNotContain(reported, message => message.Kind == SnackbarKind.Error);
    }

    [Fact]
    public async Task RemovingTheRulePassesNoPortBecauseItTakesTheNamedRuleWhole()
    {
        // The delete names the rule rather than a port, so a rule left over from an older port is the
        // one that gets taken away.
        var pages = new SettingsPages(Exposure());
        await pages.InitializeAsync();

        await pages.Server.RemoveFirewallRuleCommand.ExecuteAsync(null);

        Assert.Empty(pages.Firewall.AllowedPorts);
    }

    [Fact]
    public async Task TheFirewallButtonsAreOpenWhileTheExposureIsOnAndIdle()
    {
        var pages = new SettingsPages(Exposure());
        await pages.InitializeAsync();

        Assert.True(pages.Server.CanEditFirewall);
    }

    [Fact]
    public async Task TheFirewallButtonsAreClosedWhileTheExposureIsOff()
    {
        // With nothing meant to be listening, the opening is not worth a consent prompt.
        var pages = new SettingsPages(Exposure());
        await pages.InitializeAsync();

        pages.Server.LanProxy = false;

        Assert.False(pages.Server.CanEditFirewall);
    }

    [Fact]
    public async Task TheFirewallButtonsAreClosedWhileOneIsAlreadyRunning()
    {
        // Two prompts at once would leave the user guessing which rule the second one replaced.
        var pages = new SettingsPages(Exposure());
        await pages.InitializeAsync();

        pages.Server.IsFirewallBusy = true;

        Assert.False(pages.Server.CanEditFirewall);
    }

    [Fact]
    public void TheRuleIsNamedQwenTrayAndOpensThePortForInboundTcp()
    {
        var command = FirewallRuleService.BuildAddCommand(4171);

        Assert.Contains("name=QwenTray", command, StringComparison.Ordinal);
        Assert.Contains("dir=in", command, StringComparison.Ordinal);
        Assert.Contains("action=allow", command, StringComparison.Ordinal);
        Assert.Contains("protocol=TCP", command, StringComparison.Ordinal);
        Assert.Contains("localport=4171", command, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeleteTakesOutTheSameNameTheAddWrites()
    {
        // A delete that named something else would leave the opening behind and still report success.
        Assert.Contains("name=QwenTray", FirewallRuleService.BuildDeleteCommand(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllowingReplacesTheRuleRatherThanStackingASecondOne()
    {
        // Two rules under one name would leave the previous port open after a port change, which is
        // exactly the opening the user meant to close.
        var pages = new SettingsPages(Exposure());
        await pages.InitializeAsync();

        await pages.Server.AllowFirewallPortCommand.ExecuteAsync(null);
        pages.Server.LanProxyPort = "4999";
        await pages.Server.AllowFirewallPortCommand.ExecuteAsync(null);

        Assert.Equal([4171, 4999], pages.Firewall.AllowedPorts);
    }

    private static Settings Exposure() => new()
    {
        Profiles = [Settings.CreateDefaultProfile()],
        LanProxy = true,
        LanProxyPort = 4171,
    };

    /// <summary>
    /// Runs one button and returns only the messages it raised. Initialization runs every page, and the
    /// Settings page reports its own unrelated business, so an assertion over the whole list would be
    /// answering for messages the button never sent.
    /// </summary>
    private static async Task<List<(string Text, SnackbarKind Kind)>> Report(SettingsPages pages, Func<Task> command)
    {
        var from = pages.Snackbars.Messages.Count;
        await command();
        return pages.Snackbars.Messages.Skip(from).ToList();
    }
}