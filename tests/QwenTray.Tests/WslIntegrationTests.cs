using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using QwenTray;
using QwenTray.Services;
using QwenTray.ViewModels;
using Xunit;

namespace QwenTray.Tests;

public sealed class WslCommandTests
{
    [Fact]
    public void ScriptRunsThroughBashWithoutTheDefaultShell()
    {
        Assert.Equal(["-d", "QwenTray", "-u", "qwen", "-e", "/bin/bash", "-lc", "echo hi"], WslDistro.BuildScriptCommand("QwenTray", "qwen", "echo hi"));
    }

    [Fact]
    public void OutputEncodingFollowsTheInvocation()
    {
        // wsl.exe writes its own output as UTF-16LE, but passes a child command's stdout through as UTF-8.
        Assert.Equal(Encoding.Unicode.CodePage, WslDistro.ListDistrosInvocation().Encoding.CodePage);
        Assert.Equal(Encoding.Unicode.CodePage, WslDistro.InstallDistroInvocation("D").Encoding.CodePage);
        Assert.Equal(Encoding.Unicode.CodePage, WslDistro.TerminateInvocation("D").Encoding.CodePage);
        Assert.Equal(Encoding.UTF8.CodePage, WslDistro.ScriptInvocation("D", "u", "s").Encoding.CodePage);
        Assert.Equal(Encoding.UTF8.CodePage, WslDistro.DaemonInvocation(new Settings(), "D", Settings.CreateDefaultProfile()).Encoding.CodePage);
    }

    [Fact]
    public void CommandOutputDecodedAsUtf16WouldLoseTheText()
    {
        // The exact bytes measured from `wsl -d QwenTray -e /bin/bash -lc "echo ready"`.
        var bytes = "ready\n"u8.ToArray();
        Assert.DoesNotContain("ready", WslDistro.WslOutputEncoding.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal("ready\n", WslDistro.CommandOutputEncoding.GetString(bytes));
    }

    [Fact]
    public void ACommandThatSucceedsKeepsOnlyItsStdout()
    {
        Assert.Equal("ready\n", WslDistro.CombineCommandOutput(0, "ready\n", "warning: something\n"));
    }

    [Fact]
    public void ACommandThatFailsCarriesItsStderrReason()
    {
        Assert.Equal("error: Invalid token.", WslDistro.CombineCommandOutput(1, string.Empty, "error: Invalid token.\n"));
    }

    [Fact]
    public void AFailureWithBothStreamsKeepsStdoutBeforeTheReason()
    {
        var combined = WslDistro.CombineCommandOutput(1, "partial output\n", "boom\n");
        Assert.StartsWith("partial output", combined, StringComparison.Ordinal);
        Assert.EndsWith("boom", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailureWithNothingOnStderrLeavesStdoutAlone()
    {
        Assert.Equal("partial", WslDistro.CombineCommandOutput(1, "partial", "   "));
        Assert.Equal(string.Empty, WslDistro.CombineCommandOutput(1, string.Empty, string.Empty));
    }

    [Fact]
    public void WslOwnUtf16ErrorLosesItsNulsWhenItIsReadAsUtf8()
    {
        // wsl.exe writes its own errors as UTF-16LE, so reading them as the child's UTF-8 leaves a NUL
        // between every character. Those NULs are dropped to keep the message readable.
        Assert.Equal("There", WslDistro.CombineCommandOutput(1, string.Empty, "T\0h\0e\0r\0e\0"));
    }

    [Fact]
    public void DistroNamesSurviveNulAndBomAndSplitOnEither()
    {
        Assert.Equal(["Ubuntu-26.04", "QwenTray", "docker-desktop"], WslDistro.ParseDistroNames("Ubuntu-26.04\r\n\0QwenTray\0\r\n\uFEFFdocker-desktop\r\n"));
        Assert.Equal(["A", "B", "C"], WslDistro.ParseDistroNames("A\0B\0C"));
        Assert.Equal([], WslDistro.ParseDistroNames("   \r\n"));
    }

    [Fact]
    public void ArgumentsQuoteOnlyWhenWhitespaceIsPresent()
    {
        Assert.Equal("--terminate QwenTray", WslDistro.BuildArguments(["--terminate", "QwenTray"]));
        Assert.Equal("-d \"a b\"", WslDistro.BuildArguments(["-d", "a b"]));
        Assert.Equal("-- \"a b\"", WslDistro.BuildArguments(["--", "a b"]));
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

    [Fact]
    public void UnregisterIsItsOwnCommandAndCarriesOnlyTheDistroName()
    {
        Assert.Equal("--unregister QwenTray", WslDistro.UnregisterInvocation("QwenTray").Arguments);
        Assert.Equal(Encoding.Unicode.CodePage, WslDistro.UnregisterInvocation("QwenTray").Encoding.CodePage);
    }

    [Fact]
    public void SplittingTheServeArgumentsKeepsTheSpawnCommandLineIdentical()
    {
        var settings = new Settings { Port = 4170 };
        var profile = Settings.CreateDefaultProfile();
        var composed = DaemonSupervisor.BuildArguments(settings, "QwenTray", profile);
        var expected = new List<string> { "-d", "QwenTray", "-u", "qwen", "-e" };
        expected.AddRange(DaemonSupervisor.BuildServeArguments(settings, profile));
        Assert.Equal(expected, composed);
    }

    [Fact]
    public void TheDaemonRunsThroughBashWithStderrFoldedIntoStdout()
    {
        // The startup banner that names the log file is written to stderr, so the daemon's stderr has to
        // land in the one stream the supervisor reads.
        var command = WslDistro.BuildDaemonCommand("QwenTray", "qwen", DaemonSupervisor.BuildServeArguments(new Settings { Port = 4170 }, Settings.CreateDefaultProfile()));
        Assert.Equal(["-d", "QwenTray", "-u", "qwen", "-e", "/bin/bash", "-lc"], command.Take(7));
        var script = command[7];
        Assert.StartsWith("exec '/home/qwen/.local/bin/qwen' 'serve' '--hostname' '127.0.0.1' '--port' '4170' '--workspace' '/home/qwen/workspace'", script, StringComparison.Ordinal);
        Assert.EndsWith("' 2>&1", script, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotingForTheShellKeepsEveryMetacharacterLiteral()
    {
        Assert.Equal("'abc'", WslDistro.QuotePosix("abc"));
        Assert.Equal("'a; rm -rf /'", WslDistro.QuotePosix("a; rm -rf /"));
        Assert.Equal("'$(echo pwned)'", WslDistro.QuotePosix("$(echo pwned)"));
        // An embedded single quote closes the literal, emits one escaped quote, and reopens it.
        Assert.Equal("'it'\\''s'", WslDistro.QuotePosix("it's"));
        Assert.Equal("''", WslDistro.QuotePosix(string.Empty));
    }

    [Fact]
    public void ATokenCarryingShellSyntaxStaysInsideItsOwnArgument()
    {
        var script = WslDistro.BuildDaemonScript(["--token", "a' ; touch /pwned ; echo '"]);
        Assert.Equal("exec '--token' 'a'\\'' ; touch /pwned ; echo '\\''' 2>&1", script);
    }

    [Fact]
    public void TheBearerTokenGoesIntoTheEnvironmentNotTheCommandLine()
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(WslDistro.Executable);
        WslDistro.ApplyServeToken(startInfo, "  sekret  ");

        Assert.Equal("sekret", startInfo.Environment[WslDistro.ServerTokenVariable]);
        Assert.Contains(WslDistro.ServerTokenVariable, startInfo.Environment["WSLENV"]!);
        Assert.DoesNotContain("sekret", startInfo.Arguments);
    }

    [Fact]
    public void ABlankTokenTouchesNothingInTheEnvironment()
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(WslDistro.Executable);
        var before = startInfo.Environment.Count;

        WslDistro.ApplyServeToken(startInfo, "   ");

        Assert.Equal(before, startInfo.Environment.Count);
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
    public void WslConfSealsInteropAndAutomountAndNamesNoDefaultUser()
    {
        var conf = WslDistroProvisioner.BuildWslConf();
        Assert.Contains("[automount]\nenabled=false", conf, StringComparison.Ordinal);
        Assert.Contains("[interop]\nenabled=false", conf, StringComparison.Ordinal);
        Assert.Contains("appendWindowsPath=false", conf, StringComparison.Ordinal);
        // Naming a user that does not exist yet can stop the distro from booting, so there is none.
        Assert.DoesNotContain("[user]", conf, StringComparison.Ordinal);
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
    public void AFailedSignInLineIsNotReadAsAnAccount()
    {
        // gh names the account even when it refuses the token, and a failing gh folds that line onto
        // stderr, which is now part of the command output.
        var status = IdentityService.ParseGitHubAuthStatus(
            "github.com\n  X Failed to log in to github.com account octocat (/home/qwen/.config/gh/hosts.yml)\n  - The token in /home/qwen/.config/gh/hosts.yml is invalid.\n");
        Assert.False(status.IsAuthenticated);
        Assert.Null(status.Account);
    }

    [Fact]
    public async Task ReadingThePublicKeyCatsTheKeyFileInsteadOfExecutingIt()
    {
        var wsl = new FakeWslDistro();
        var service = new IdentityService(wsl, NullLogger<IdentityService>.Instance);
        var key = await service.ReadPublicKeyAsync("QwenTray", new IdentityProfile { LinuxUser = "qwen" }, CancellationToken.None);
        Assert.Contains("cat ~/.ssh/id_ed25519.pub", wsl.Scripts);
        Assert.Equal("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5 test@qwentray", key);
    }

    [Fact]
    public void GpgKeyScriptIsIdempotentAndQuotesTheUid()
    {
        var script = IdentityService.BuildEnsureGpgKeyScript(new IdentityProfile { LinuxUser = "qwen" });
        Assert.Contains("--list-secret-keys \"<qwen@qwentray.local>\"", script, StringComparison.Ordinal);
        Assert.Contains("||", script, StringComparison.Ordinal);
        Assert.Contains("--quick-generate-key \"QwenTray qwen <qwen@qwentray.local>\" ed25519 default never", script, StringComparison.Ordinal);
        Assert.Contains("--pinentry-mode loopback", script, StringComparison.Ordinal);
    }

    [Fact]
    public void GpgExportScriptArmorsThePublicKey()
    {
        Assert.Equal("gpg --armor --export \"<qwen@qwentray.local>\"", IdentityService.BuildGpgExportScript(new IdentityProfile { LinuxUser = "qwen" }));
    }

    [Fact]
    public void GpgUidNeverUsesTheFreeTextDisplayName()
    {
        // The display name is unrestricted, so it must never reach a shell script.
        var profile = new IdentityProfile { LinuxUser = "qwen", Name = "qwen\"; touch /pwned; \"" };
        Assert.Equal("QwenTray qwen <qwen@qwentray.local>", IdentityService.GpgUserId(profile));
        Assert.DoesNotContain("pwned", IdentityService.BuildEnsureGpgKeyScript(profile), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSigningKeyEmailDefaultsToTheLinuxUserAndCanBeOverridden()
    {
        var profile = new IdentityProfile { LinuxUser = "qwen" };
        Assert.Equal("qwen@qwentray.local", profile.ResolvedEmail);
        Assert.Equal("QwenTray qwen <qwen@qwentray.local>", IdentityService.GpgUserId(profile));

        profile.Email = "me@example.com";
        Assert.Equal("QwenTray qwen <me@example.com>", IdentityService.GpgUserId(profile));
        Assert.Contains("<me@example.com>", IdentityService.BuildEnsureGpgKeyScript(profile), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSigningKeyNameDefaultsToTheProductAndUserAndCanBeOverridden()
    {
        var profile = new IdentityProfile { LinuxUser = "qwen" };
        Assert.Equal("QwenTray qwen", profile.ResolvedGpgName);

        profile.GpgName = "Ada Lovelace";
        Assert.Equal("Ada Lovelace <qwen@qwentray.local>", IdentityService.GpgUserId(profile));
        Assert.Contains("--quick-generate-key \"Ada Lovelace <qwen@qwentray.local>\"", IdentityService.BuildEnsureGpgKeyScript(profile), StringComparison.Ordinal);
    }

    [Fact]
    public void ARenamedSigningKeyIsStillFoundByItsEmail()
    {
        // gpg matches a user id as a substring, so a script that searches by the full uid strands the key
        // the moment its name changes: it cannot be exported, and deleting it matches nothing and succeeds
        // while leaving the key behind. Every lookup uses the email, which is what the key keeps.
        var profile = new IdentityProfile { LinuxUser = "qwen", GpgName = "Ada Lovelace" };
        Assert.DoesNotContain("Ada", IdentityService.BuildGpgExportScript(profile), StringComparison.Ordinal);

        var delete = IdentityService.BuildDeleteGpgKeyScript(profile);
        Assert.Contains("--list-secret-keys \"<qwen@qwentray.local>\"", delete, StringComparison.Ordinal);
        Assert.DoesNotContain("Ada", delete, StringComparison.Ordinal);

        // Generation still names the key; only the search ignores the name, so a rename cannot make the
        // profile grow a second key with the same email.
        Assert.DoesNotContain("--list-secret-keys \"Ada", IdentityService.BuildEnsureGpgKeyScript(profile), StringComparison.Ordinal);
    }

    [Fact]
    public void ASigningKeyEmailWithShellMetacharactersIsRejected()
    {
        Assert.True(new IdentityProfile { LinuxUser = "qwen" }.IsShellSafe);
        Assert.True(new IdentityProfile { LinuxUser = "qwen", Email = "me+sign@ex.co.uk" }.IsShellSafe);

        Assert.False(new IdentityProfile { LinuxUser = "qwen", Email = "me@example.com\"; touch /pwned; \"" }.IsShellSafe);
        Assert.False(new IdentityProfile { LinuxUser = "qwen", Email = "not an email" }.IsShellSafe);
        Assert.False(new IdentityProfile { LinuxUser = "qwen", Email = "a@b" }.IsShellSafe);
    }

    [Fact]
    public void ASigningKeyNameWithShellMetacharactersIsRejected()
    {
        Assert.True(new IdentityProfile { LinuxUser = "qwen", GpgName = "Ada Lovelace" }.IsShellSafe);
        Assert.True(new IdentityProfile { LinuxUser = "qwen", GpgName = "O'Brien-Dev_2.0, x" }.IsShellSafe);
        Assert.True(new IdentityProfile { LinuxUser = "qwen", GpgName = new string('a', 64) }.IsShellSafe);

        // Each of these would either expand inside the quoted user id or end the name where gpg expects
        // the email to start.
        Assert.False(new IdentityProfile { LinuxUser = "qwen", GpgName = "Ada\"; touch /pwned; \"" }.IsShellSafe);
        Assert.False(new IdentityProfile { LinuxUser = "qwen", GpgName = "$(touch /pwned)" }.IsShellSafe);
        Assert.False(new IdentityProfile { LinuxUser = "qwen", GpgName = "Ada `touch /pwned`" }.IsShellSafe);
        Assert.False(new IdentityProfile { LinuxUser = "qwen", GpgName = "Ada <me@example.com>" }.IsShellSafe);
        Assert.False(new IdentityProfile { LinuxUser = "qwen", GpgName = new string('a', 65) }.IsShellSafe);
    }

    [Fact]
    public async Task ReadingTheGpgKeyAcceptsOnlyAnArmoredBlock()
    {
        const string armored = "-----BEGIN PGP PUBLIC KEY BLOCK-----\n\nmDMEaskSgBYJKwYBBAHaRw8BAQdAtWIb\n-----END PGP PUBLIC KEY BLOCK-----";
        var wsl = new FakeWslDistro { ScriptOutput = armored };
        var service = new IdentityService(wsl, NullLogger<IdentityService>.Instance);
        var profile = new IdentityProfile { LinuxUser = "qwen" };

        Assert.Equal(armored, await service.ReadGpgPublicKeyAsync("QwenTray", profile, CancellationToken.None));
        Assert.Contains(wsl.Scripts, script => script.Contains("--armor --export", StringComparison.Ordinal));

        wsl.ScriptOutput = "gpg: WARNING: unsafe permissions on homedir";
        Assert.Null(await service.ReadGpgPublicKeyAsync("QwenTray", profile, CancellationToken.None));
    }

    [Fact]
    public void TokenScriptNeverCarriesTheSecretAndReadsStdin()
    {
        var script = IdentityService.BuildGitHubTokenScript();
        Assert.Equal("gh auth login --hostname github.com --with-token", script);
        Assert.DoesNotContain("base64", script, StringComparison.Ordinal);
        Assert.DoesNotContain("printf", script, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxUserNameAndPathsMustBeShellSafe()
    {
        var good = new IdentityProfile { LinuxUser = "qwen" };
        Assert.True(good.IsShellSafe);
        Assert.Equal("/home/qwen/.local/bin/qwen", good.ResolvedQwenExecutable);

        Assert.False(new IdentityProfile { LinuxUser = "qwen;evil" }.IsShellSafe);
        Assert.False(new IdentityProfile { LinuxUser = "qwen", WorkspaceDirectory = "/tmp/x'; evil; '" }.IsShellSafe);
        Assert.False(new IdentityProfile { LinuxUser = "qwen", QwenExecutable = "/opt/q $x" }.IsShellSafe);
        Assert.False(new IdentityProfile { LinuxUser = "qwen", WorkspaceDirectory = "relative/path" }.IsShellSafe);
        Assert.False(new IdentityProfile { LinuxUser = "qwen", WorkspaceDirectory = "/home/qwen/my work" }.IsShellSafe);
    }

    [Fact]
    public void DistroNamesAreRestrictedToASafeCharset()
    {
        Assert.True(SettingsViewModel.IsSafeDistroName("QwenTray"));
        Assert.True(SettingsViewModel.IsSafeDistroName("Qwen-Tray_2.0"));
        Assert.False(SettingsViewModel.IsSafeDistroName("Distro & notepad.exe"));
        Assert.False(SettingsViewModel.IsSafeDistroName("bad;name"));
        // A leading dash or dot would be read as a wsl.exe option.
        Assert.False(SettingsViewModel.IsSafeDistroName("-u"));
        Assert.False(SettingsViewModel.IsSafeDistroName(".hidden"));
        Assert.False(SettingsViewModel.IsSafeDistroName(string.Empty));
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

    [Fact]
    public void DeletingTheSshKeyRemovesBothHalvesAndNothingElse()
    {
        var script = IdentityService.BuildDeleteSshKeyScript();
        Assert.Equal("rm -f ~/.ssh/id_ed25519 ~/.ssh/id_ed25519.pub", script);
        // known_hosts belongs to the profile too, so a key delete must not wipe it.
        Assert.DoesNotContain("known_hosts", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-r", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DeletingTheSigningKeyResolvesTheFingerprintFirst()
    {
        var script = IdentityService.BuildDeleteGpgKeyScript(new IdentityProfile { LinuxUser = "qwen" });

        // gpg refuses to delete by uid in batch mode, so the email only finds the key and the fingerprint
        // is the delete target.
        Assert.Contains("--list-secret-keys \"<qwen@qwentray.local>\"", script, StringComparison.Ordinal);
        Assert.Contains("grep \"^fpr:\"", script, StringComparison.Ordinal);
        Assert.Contains("cut -d: -f10", script, StringComparison.Ordinal);
        Assert.Contains("--delete-secret-and-public-key \"$fpr\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--delete-secret-and-public-key \"QwenTray", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegeneratingTheSshKeyDeletesBeforeItCreates()
    {
        var wsl = new FakeWslDistro();
        var service = new IdentityService(wsl, NullLogger<IdentityService>.Instance);
        await service.RegenerateSshKeyAsync("QwenTray", new IdentityProfile { LinuxUser = "qwen" }, CancellationToken.None);

        var delete = wsl.Scripts.FindIndex(script => script.StartsWith("rm -f", StringComparison.Ordinal));
        var create = wsl.Scripts.FindIndex(script => script.Contains("ssh-keygen", StringComparison.Ordinal));
        Assert.InRange(delete, 0, wsl.Scripts.Count - 1);
        Assert.True(delete < create, "the old key has to be gone before a new one is written");
    }

    [Fact]
    public async Task RegeneratingTheSigningKeyDeletesBeforeItCreates()
    {
        var wsl = new FakeWslDistro();
        var service = new IdentityService(wsl, NullLogger<IdentityService>.Instance);
        await service.RegenerateGpgKeyAsync("QwenTray", new IdentityProfile { LinuxUser = "qwen" }, CancellationToken.None);

        var delete = wsl.Scripts.FindIndex(script => script.Contains("--delete-secret-and-public-key", StringComparison.Ordinal));
        var create = wsl.Scripts.FindIndex(script => script.Contains("--quick-generate-key", StringComparison.Ordinal));
        Assert.InRange(delete, 0, wsl.Scripts.Count - 1);
        Assert.True(delete < create, "the old key has to be gone before a new one is written");
    }
}
