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
        foreach (var id in new[] { "dotnet", "node", "azure-cli", "aspire", "cli-utils", "python", "go", "rust" })
        {
            Assert.Contains(id, ids);
        }
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
    public void AspireInstallsToAPathOnEveryProfilesDefaultPathWithoutTouchingProfiles()
    {
        var script = Tool("aspire").Script;
        Assert.Contains("--install-path /usr/local/bin", script, StringComparison.Ordinal);
        Assert.Contains("--skip-path", script, StringComparison.Ordinal);
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

        await installer.InstallAsync("QwenTray", Tool("dotnet"), CancellationToken.None);

        Assert.Contains(Tool("dotnet").Script, wsl.RootScripts);
        Assert.Empty(wsl.Scripts);
    }
}
