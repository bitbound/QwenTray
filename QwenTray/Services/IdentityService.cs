using System.Text;
using Microsoft.Extensions.Logging;

namespace QwenTray.Services;

public sealed record GitHubAuthStatus(bool IsAuthenticated, string? Account);

/// <summary>
/// Manages the per-profile credentials that make one Linux user a distinct identity: the ssh keypair
/// and the GitHub token. Everything lives in the profile's home directory inside the distro.
/// </summary>
public sealed class IdentityService(IWslDistro wsl, ILogger<IdentityService> logger)
{
    public const string SshKeyPath = "~/.ssh/id_ed25519";

    public async Task EnsureSshKeyAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        var result = await wsl.RunScriptAsync(distro, profile.LinuxUser, BuildEnsureSshKeyScript(), cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException($"Could not create the ssh key for {profile.Name}. {result.Output}");
    }

    public async Task<string?> ReadPublicKeyAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        var result = await wsl.RunScriptAsync(distro, profile.LinuxUser, $"{SshKeyPath}.pub", cancellationToken);
        if (!result.Succeeded) return null;
        var key = result.Output.Trim();
        return key.StartsWith("ssh-", StringComparison.Ordinal) ? key : null;
    }

    public async Task SetGitHubTokenAsync(string distro, IdentityProfile profile, string token, CancellationToken cancellationToken)
    {
        var result = await wsl.RunScriptAsync(distro, profile.LinuxUser, BuildGitHubTokenScript(token), cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException($"Could not store the GitHub token for {profile.Name}. {result.Output}");
        logger.LogInformation("Stored a GitHub token for profile {Profile}.", profile.Name);
    }

    public async Task ClearGitHubTokenAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        var result = await wsl.RunScriptAsync(distro, profile.LinuxUser, "gh auth logout --hostname github.com", cancellationToken);
        if (!result.Succeeded) logger.LogDebug("GitHub logout for {Profile} reported: {Output}", profile.Name, result.Output);
    }

    public async Task<GitHubAuthStatus> GetGitHubAuthStatusAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        var result = await wsl.RunScriptAsync(distro, profile.LinuxUser, "gh auth status --hostname github.com", cancellationToken);
        return ParseGitHubAuthStatus(result.Output);
    }

    internal static string BuildEnsureSshKeyScript() =>
        $"test -f {SshKeyPath} || (mkdir -p ~/.ssh && chmod 700 ~/.ssh && ssh-keygen -t ed25519 -N '' -C qwentray -f {SshKeyPath} >/dev/null && chmod 600 {SshKeyPath})";

    /// <summary>Feeds the token over stdin so it never lands in a command line or the process list.</summary>
    internal static string BuildGitHubTokenScript(string token)
        => $"printf %s {Convert.ToBase64String(Encoding.UTF8.GetBytes(token.Trim()))} | base64 -d | gh auth login --hostname github.com --with-token";

    /// <summary>
    /// gh prints "Logged in to github.com account NAME" when a token is valid, and writes an error and
    /// "not logged in" otherwise.
    /// </summary>
    internal static GitHubAuthStatus ParseGitHubAuthStatus(string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var marker = line.IndexOf("account", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) continue;
            var rest = line[(marker + "account".Length)..].Trim();
            if (rest.Length == 0) continue;
            var account = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(account)) return new GitHubAuthStatus(true, account);
        }

        return new GitHubAuthStatus(false, null);
    }
}
