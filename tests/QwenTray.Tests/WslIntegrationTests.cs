using System.Text;
using QwenTray;
using QwenTray.Services;
using QwenTray.ViewModels;
using Xunit;

namespace QwenTray.Tests;

public sealed class WslCommandTests
{
    [Fact]
    public void ScriptRunsThroughBashAsTheRequestedUser()
    {
        Assert.Equal(["-d", "QwenTray", "-u", "qwen", "--", "/bin/bash", "-lc", "echo hi"], WslDistro.BuildScriptCommand("QwenTray", "qwen", "echo hi"));
    }

    [Fact]
    public void ArgumentsQuoteOnlyWhenWhitespaceIsPresent()
    {
        Assert.Equal("--terminate QwenTray", WslDistro.BuildArguments(["--terminate", "QwenTray"]));
        Assert.Equal("-d \"a b\"", WslDistro.BuildArguments(["-d", "a b"]));
        Assert.Equal("-- \"a b\"", WslDistro.BuildArguments(["--", "a b"]));
    }

    [Fact]
    public void DistroNamesComeBackCleanOfNulAndBom()
    {
        var names = WslDistro.ParseDistroNames("Ubuntu-26.04\r\n\0QwenTray\0\r\n\uFEFFdocker-desktop\r\n");
        Assert.Equal(["Ubuntu-26.04", "QwenTray", "docker-desktop"], names);
    }

    [Fact]
    public void AbsolutePathParsingSkipsNonPathLines()
    {
        Assert.Equal("/home/qwen/.local/bin/qwen", WslDistro.ParseAbsolutePath("searching...\n/home/qwen/.local/bin/qwen\n"));
        Assert.Null(WslDistro.ParseAbsolutePath("command not found"));
    }

    [Fact]
    public void VersionParsingTakesTheFirstNonEmptyLine()
    {
        Assert.Equal("1.2.3", WslDistro.ParseVersion("\r\n1.2.3\r\n"));
        Assert.Null(WslDistro.ParseVersion("\r\n\r\n"));
    }
}

public sealed class WslConfigTests
{
    [Fact]
    public void MissingWsl2SectionIsAppended()
    {
        var merged = WslDistroProvisioner.MergeWslConfig("[experimental]\nautoMemoryReclaim=gradual\n");
        Assert.Contains("networkingMode=mirrored", merged, StringComparison.Ordinal);
        Assert.Contains("[wsl2]", merged, StringComparison.Ordinal);
        Assert.Contains("autoMemoryReclaim=gradual", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingNetworkingModeIsReplacedNotDuplicated()
    {
        var merged = WslDistroProvisioner.MergeWslConfig("[wsl2]\nnetworkingMode=bridged\nmemory=8GB\n");
        Assert.Equal(1, merged.Split("networkingMode", StringSplitOptions.RemoveEmptyEntries).Length - 1);
        Assert.Contains("networkingMode=mirrored", merged, StringComparison.Ordinal);
        Assert.Contains("memory=8GB", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void MergingIsIdempotent()
    {
        var once = WslDistroProvisioner.MergeWslConfig(string.Empty);
        var twice = WslDistroProvisioner.MergeWslConfig(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void WslConfSealsInteropAndAutomountAndSetsTheDefaultUser()
    {
        var conf = WslDistroProvisioner.BuildWslConf("qwen");
        Assert.Contains("[automount]\nenabled=false", conf, StringComparison.Ordinal);
        Assert.Contains("[interop]\nenabled=false", conf, StringComparison.Ordinal);
        Assert.Contains("appendWindowsPath=false", conf, StringComparison.Ordinal);
        Assert.Contains("default=qwen", conf, StringComparison.Ordinal);
    }

    [Fact]
    public void FileWriteScriptCarriesTheContentAsBase64()
    {
        const string content = "[interop]\nenabled=false\n# a 'quote' and a | pipe\n";
        var script = WslDistroProvisioner.BuildWriteFileScript("/etc/wsl.conf", content);
        const string prefix = "printf %s ";
        var start = script.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var end = script.IndexOf(" | base64", StringComparison.Ordinal);
        var encoded = script[start..end];
        Assert.Equal(content, Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        Assert.EndsWith("> /etc/wsl.conf", script, StringComparison.Ordinal);
    }

    [Fact]
    public void UserCreationScriptIsIdempotent()
    {
        var script = WslDistroProvisioner.BuildCreateUserScript(new IdentityProfile { LinuxUser = "qwen" });
        Assert.Contains("id -u qwen", script, StringComparison.Ordinal);
        Assert.Contains("useradd -m -s /bin/bash qwen", script, StringComparison.Ordinal);
    }
}

public sealed class SyncEntryTests
{
    [Theory]
    [InlineData("QWEN.md")]
    [InlineData("agents")]
    [InlineData("rules")]
    [InlineData("memories")]
    [InlineData("skills")]
    [InlineData("workflows")]
    [InlineData("agents/reviewer.md")]
    public void TheAgreedEntriesAreSyncable(string entry)
    {
        Assert.True(WslConfigSyncService.IsSyncableEntry(entry));
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData("projects")]
    [InlineData("../secret")]
    [InlineData("agents/../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData("")]
    [InlineData("   ")]
    public void DangerousOrExcludedEntriesAreRejected(string entry)
    {
        Assert.False(WslConfigSyncService.IsSyncableEntry(entry));
    }

    [Fact]
    public void SettingsJsonIsRejectedEvenWhenNestedOrCased()
    {
        Assert.False(WslConfigSyncService.IsSyncableEntry("SETTINGS.JSON"));
        Assert.False(WslConfigSyncService.IsSyncableEntry("Projects"));
    }
}

public sealed class IdentityTests
{
    [Fact]
    public void AuthStatusReadsTheAccountFromGhOutput()
    {
        var status = IdentityService.ParseGitHubAuthStatus("github.com\n  Logged in to github.com account octocat (keyring)\n");
        Assert.True(status.IsAuthenticated);
        Assert.Equal("octocat", status.Account);
    }

    [Fact]
    public void AuthStatusIsFalseWithoutAnAccountLine()
    {
        var status = IdentityService.ParseGitHubAuthStatus("github.com\n  X Not logged in to github.com\n");
        Assert.False(status.IsAuthenticated);
        Assert.Null(status.Account);
    }

    [Fact]
    public void TokenScriptNeverEmbedsTheRawToken()
    {
        var script = IdentityService.BuildGitHubTokenScript("ghp_secretvalue");
        Assert.DoesNotContain("ghp_secretvalue", script, StringComparison.Ordinal);
        const string prefix = "printf %s ";
        var start = script.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var end = script.IndexOf(" | base64", StringComparison.Ordinal);
        Assert.Equal("ghp_secretvalue", Encoding.UTF8.GetString(Convert.FromBase64String(script[start..end])));
        Assert.Contains("gh auth login", script, StringComparison.Ordinal);
        Assert.Contains("--with-token", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("qwen", true)]
    [InlineData("work_user", true)]
    [InlineData("a", true)]
    [InlineData("Work", false)]
    [InlineData("1bad", false)]
    [InlineData("-bad", false)]
    [InlineData("has space", false)]
    [InlineData("", false)]
    public void LinuxUserNamesAreValidated(string user, bool expected)
    {
        Assert.Equal(expected, SettingsViewModel.IsValidLinuxUser(user));
    }

    [Fact]
    public void SlugificationProducesAValidUserName()
    {
        Assert.Equal("work_account", SettingsViewModel.SlugifyLinuxUser("Work Account"));
        Assert.Equal("user1profile", SettingsViewModel.SlugifyLinuxUser("1profile"));
        Assert.True(SettingsViewModel.IsValidLinuxUser(SettingsViewModel.SlugifyLinuxUser("Weird! Name?")));
    }
}
