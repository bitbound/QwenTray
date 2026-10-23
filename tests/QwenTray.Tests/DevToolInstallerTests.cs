using Microsoft.Extensions.Logging.Abstractions;
using QwenTray.Services;
using Xunit;

namespace QwenTray.Tests;

public sealed class DevToolInstallerTests
{
    private static DevTool Tool(string id) => DevToolInstaller.Catalog.Single(tool => string.Equals(tool.Id, id, StringComparison.Ordinal));

    [Fact]
    public void EveryToolHasAnIdNameSummaryAndScriptAndTheIdsAreUnique()
    {
        Assert.NotEmpty(DevToolInstaller.Catalog);
        Assert.All(DevToolInstaller.Catalog, tool =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Id));
            Assert.False(string.IsNullOrWhiteSpace(tool.Name));
            Assert.False(string.IsNullOrWhiteSpace(tool.Summary));
            Assert.False(string.IsNullOrWhiteSpace(tool.Script));
        });
        Assert.Equal(
            DevToolInstaller.Catalog.Count,
            DevToolInstaller.Catalog.Select(tool => tool.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheRequestedToolsAreAllPresent()
    {
        var ids = DevToolInstaller.Catalog.Select(tool => tool.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in new[] { "dotnet", "node", "azure-cli", "aspire", "cli-utils", "python", "go", "rust", "browser", "docker", "open-code-review", "zvec-grep" })
        {
            Assert.Contains(id, ids);
        }
    }

    [Fact]
    public void TheCatalogLeadsWithTheToolsAnAgentSessionNeedsFirst()
    {
        Assert.Equal(
            ["docker", "cli-utils", "open-code-review", "zvec-grep", "browser", "dotnet", "node", "azure-cli", "aspire", "python", "go", "rust"],
            DevToolInstaller.Catalog.Select(tool => tool.Id));
    }

    [Fact]
    public void DotNetInstallsTheLtsSdkFromTheDistrosOwnFeed()
    {
        Assert.Contains("apt-get install -y dotnet-sdk-10.0", Tool("dotnet").Script, StringComparison.Ordinal);
    }

    [Fact]
    public void NodeComesFromNodeSourceAndTheBundleCarriesNpm()
    {
        var script = Tool("node").Script;
        Assert.Contains("https://deb.nodesource.com/setup_lts.x", script, StringComparison.Ordinal);
        Assert.Contains("apt-get install -y nodejs", script, StringComparison.Ordinal);
        // The nodejs package already provides npm, so no separate npm package is named.
        Assert.DoesNotContain("install -y npm", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AzureCliAddsMicrosoftsRepoAndOverwritesItsKeyWithoutPrompting()
    {
        var script = Tool("azure-cli").Script;
        Assert.Contains("packages.microsoft.com/repos/azure-cli/", script, StringComparison.Ordinal);
        Assert.Contains("apt-get install -y azure-cli", script, StringComparison.Ordinal);
        // gpg refuses to overwrite the keyring without --yes, which would break a second press.
        Assert.Contains("gpg --dearmor --yes", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AspireInstallsIntoTheHomeOfWhoeverRunsTheScript()
    {
        var script = Tool("aspire").Script;
        Assert.Contains("--install-path \"$HOME/.local/bin\"", script, StringComparison.Ordinal);
        Assert.Contains("--skip-path", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/usr/local", script, StringComparison.Ordinal);
    }

    [Fact]
    public void CliUtilitiesAlsoExposeFdUnderTheNamePeopleType()
    {
        var script = Tool("cli-utils").Script;
        Assert.Contains("build-essential", script, StringComparison.Ordinal);
        Assert.Contains("jq", script, StringComparison.Ordinal);
        Assert.Contains("ripgrep", script, StringComparison.Ordinal);
        Assert.Contains("fd-find", script, StringComparison.Ordinal);
        // Ubuntu ships the binary as fdfind, so the name people type is linked to it.
        Assert.Contains("/usr/local/bin/fd", script, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenCodeReviewSharesOneBinaryAndGivesEveryProfileItsOwnSkills()
    {
        var script = Tool("open-code-review").Script;
        // The vendor script verifies the release checksum, and the install dir keeps the binary off
        // root's home so every profile resolves the same ocr.
        Assert.Contains("https://open-codereview.ai/install.sh", script, StringComparison.Ordinal);
        Assert.Contains("OCR_INSTALL_DIR=/usr/local/bin", script, StringComparison.Ordinal);
        Assert.Contains("for skill in open-code-review open-code-review-delegate", script, StringComparison.Ordinal);
        Assert.Contains("$home/.qwen/skills/$skill/SKILL.md", script, StringComparison.Ordinal);
        // install -d would also re-mode ~/.qwen, which holds the profile's settings and keys.
        Assert.DoesNotContain("install -d", script, StringComparison.Ordinal);
        Assert.Contains("chown -R \"$account:$account\" \"$home/.qwen/skills\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ZvecGrepIsOneGlobalPackageButRegistersItselfPerProfile()
    {
        var script = Tool("zvec-grep").Script;
        Assert.Contains("npm install -g @zvec/zvec-grep", script, StringComparison.Ordinal);
        Assert.Contains("zg install --target qwen --yes", script, StringComparison.Ordinal);
        // The integrator writes the MCP registration into the account's own Qwen Code files, so it
        // cannot run as root.
        Assert.Contains("runuser -l \"$account\"", script, StringComparison.Ordinal);
        // The package needs a current Node, so a distro with no npm gets the same LTS line as the
        // Node.js entry rather than Ubuntu's older package.
        Assert.Contains("command -v npm", script, StringComparison.Ordinal);
        Assert.Contains("https://deb.nodesource.com/setup_lts.x", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PythonInstallsUvIntoAPathOnEveryProfilesDefaultPath()
    {
        var script = Tool("python").Script;
        Assert.Contains("python3-venv", script, StringComparison.Ordinal);
        Assert.Contains("UV_INSTALL_DIR=/usr/local/bin", script, StringComparison.Ordinal);
        Assert.Contains("UV_NO_MODIFY_PATH=1", script, StringComparison.Ordinal);
    }

    [Fact]
    public void GoReadsTheCurrentVersionInsteadOfPinningOneAndReplacesTheOldTree()
    {
        var script = Tool("go").Script;
        Assert.Contains("https://go.dev/VERSION?m=text", script, StringComparison.Ordinal);
        Assert.Contains("/usr/local/go", script, StringComparison.Ordinal);
        // Removing the old tree first stops a reinstall from leaving two toolchains side by side.
        Assert.Contains("rm -rf /usr/local/go", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RustUsesASharedRustupHomeAndPutsItOnTheLoginPath()
    {
        var script = Tool("rust").Script;
        Assert.Contains("RUSTUP_HOME=/usr/local/rustup", script, StringComparison.Ordinal);
        Assert.Contains("CARGO_HOME=/usr/local/cargo", script, StringComparison.Ordinal);
        Assert.Contains("/etc/profile.d/rust.sh", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallRunsTheToolsScriptAsRoot()
    {
        var wsl = new FakeWslDistro();
        var installer = new DevToolInstaller(wsl, NullLogger<DevToolInstaller>.Instance);

        await installer.InstallAsync("QwenTray", Tool("dotnet"), [new IdentityProfile { LinuxUser = "qwen" }], CancellationToken.None);

        Assert.Contains(Tool("dotnet").Script, wsl.RootScripts);
        Assert.Empty(wsl.Scripts);
    }

    [Fact]
    public void BrowserInstallsEdgeFromMicrosoftsFeedAndMakesItTheDefaultHandlerForEveryone()
    {
        var script = Tool("browser").Script;
        Assert.Contains("https://packages.microsoft.com/repos/edge", script, StringComparison.Ordinal);
        Assert.Contains("apt-get install -y microsoft-edge-stable", script, StringComparison.Ordinal);
        // xdg-open reads the shared list, so a new profile has nothing of its own to set up.
        Assert.Contains("/etc/xdg/mimeapps.list", script, StringComparison.Ordinal);
        Assert.Contains("x-scheme-handler/https=microsoft-edge.desktop;", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DockerComesFromItsOwnSignedRepoAndIsEnabledForEveryHumanAccount()
    {
        var script = Tool("docker").Script;
        Assert.Contains("https://download.docker.com/linux/ubuntu", script, StringComparison.Ordinal);
        Assert.Contains("apt-get install -y docker-ce", script, StringComparison.Ordinal);
        Assert.Contains("systemctl enable --now docker", script, StringComparison.Ordinal);
        // A distro provisioned before this entry existed has no membership, so the install grants it.
        Assert.Contains("usermod -aG docker", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AspireIsScopedToTheProfileHomeBecauseItsLogsGoBesideTheInstallRoot()
    {
        // A root-owned install root leaves the CLI unable to start its own log directory for a profile
        // user, so this tool is the one that cannot be shared.
        Assert.Equal(InstallScope.ProfileHome, Tool("aspire").Scope);
        Assert.All(DevToolInstaller.Catalog.Where(tool => tool.Id != "aspire"), tool => Assert.Equal(InstallScope.Distro, tool.Scope));
    }

    [Fact]
    public async Task AProfileHomeToolInstallsOncePerProfileAsThatProfileUser()
    {
        var wsl = new FakeWslDistro();
        var installer = new DevToolInstaller(wsl, NullLogger<DevToolInstaller>.Instance);
        var profiles = new[]
        {
            new IdentityProfile { Id = "work", Name = "Work", LinuxUser = "workuser" },
            new IdentityProfile { Id = "home", Name = "Home", LinuxUser = "home" },
        };

        await installer.InstallAsync("QwenTray", Tool("aspire"), profiles, CancellationToken.None);

        Assert.Equal(["workuser", "home"], wsl.UserScripts.Select(entry => entry.User));
        Assert.All(wsl.UserScripts, entry => Assert.Equal(Tool("aspire").Script, entry.Script));
        Assert.Empty(wsl.RootScripts);
    }

    [Fact]
    public async Task AProfileHomeToolStopsAtTheFirstProfileWhoseInstallFails()
    {
        var wsl = new FakeWslDistro { ScriptExitCode = 1 };
        var installer = new DevToolInstaller(wsl, NullLogger<DevToolInstaller>.Instance);
        var profiles = new[]
        {
            new IdentityProfile { Id = "work", Name = "Work", LinuxUser = "workuser" },
            new IdentityProfile { Id = "home", Name = "Home", LinuxUser = "home" },
        };

        var result = await installer.InstallAsync("QwenTray", Tool("aspire"), profiles, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(["workuser"], wsl.UserScripts.Select(entry => entry.User));
    }
}
