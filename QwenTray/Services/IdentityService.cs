using Microsoft.Extensions.Logging;

namespace QwenTray.Services;

public sealed record GitHubAuthStatus(bool IsAuthenticated, string? Account);

/// <summary>
/// Manages the per-profile credentials that make one Linux user a distinct identity: the ssh keypair,
/// the signing key, and the GitHub token. Everything lives in the profile's home directory inside the
/// distro.
/// </summary>
public sealed class IdentityService(IWslDistro wsl, ILogger<IdentityService> logger)
{
    public const string SshKeyPath = "~/.ssh/id_ed25519";

    /// <summary>
    /// The uid written onto a profile's signing key. The name and the email are both restricted to a
    /// metacharacter-free charset, so the result is safe to place inside the generation script.
    /// </summary>
    public static string GpgUserId(IdentityProfile profile) => $"{profile.ResolvedGpgName} <{profile.ResolvedEmail}>";

    /// <summary>
    /// The pattern that finds a profile's existing key. gpg treats a pattern wrapped in angle brackets
    /// as an exact email match, so the key stays reachable when the display name changes. A full uid
    /// matches only as a substring, which would leave a renamed key impossible to export or delete.
    /// </summary>
    public static string GpgKeySelector(IdentityProfile profile) => $"<{profile.ResolvedEmail}>";

    public async Task EnsureSshKeyAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        var result = await wsl.RunScriptAsync(distro, profile.LinuxUser, BuildEnsureSshKeyScript(), cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException($"Could not create the ssh key for {profile.Name}. {result.Output}");
    }

    /// <summary>
    /// Removes both halves of the keypair. A public key already added to GitHub stays there until the
    /// user removes it, so this only invalidates it.
    /// </summary>
    public async Task DeleteSshKeyAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        var result = await wsl.RunScriptAsync(distro, profile.LinuxUser, BuildDeleteSshKeyScript(), cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException($"Could not delete the ssh key for {profile.Name}. {result.Output}");
    }

    public async Task RegenerateSshKeyAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        await DeleteSshKeyAsync(distro, profile, cancellationToken);
        await EnsureSshKeyAsync(distro, profile, cancellationToken);
    }

    public async Task<string?> ReadPublicKeyAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        var result = await wsl.RunScriptAsync(distro, profile.LinuxUser, $"cat {SshKeyPath}.pub", cancellationToken);
        if (!result.Succeeded) return null;
        var key = result.Output.Trim();
        return key.StartsWith("ssh-", StringComparison.Ordinal) ? key : null;
    }

    public async Task EnsureGpgKeyAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        var result = await wsl.RunScriptAsync(distro, profile.LinuxUser, BuildEnsureGpgKeyScript(profile), cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException($"Could not create the signing key for {profile.Name}. {result.Output}");
    }

    public async Task DeleteGpgKeyAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        var result = await wsl.RunScriptAsync(distro, profile.LinuxUser, BuildDeleteGpgKeyScript(profile), cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException($"Could not delete the signing key for {profile.Name}. {result.Output}");
    }

    public async Task RegenerateGpgKeyAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        await DeleteGpgKeyAsync(distro, profile, cancellationToken);
        await EnsureGpgKeyAsync(distro, profile, cancellationToken);
    }

    public async Task<string?> ReadGpgPublicKeyAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        var result = await wsl.RunScriptAsync(distro, profile.LinuxUser, BuildGpgExportScript(profile), cancellationToken);
        if (!result.Succeeded) return null;
        var key = result.Output.Trim();
        return key.StartsWith("-----BEGIN PGP PUBLIC KEY BLOCK-----", StringComparison.Ordinal) ? key : null;
    }

    public async Task SetGitHubTokenAsync(string distro, IdentityProfile profile, string token, CancellationToken cancellationToken)
    {
        var result = await wsl.RunScriptWithInputAsync(distro, profile.LinuxUser, BuildGitHubTokenScript(), token.Trim(), cancellationToken);
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

    internal static string BuildDeleteSshKeyScript() =>
        $"rm -f {SshKeyPath} {SshKeyPath}.pub";

    // The signing key has no passphrase so the daemon can sign unattended. The user id and the search
    // pattern are always quoted because both hold angle brackets and a space.
    internal static string BuildEnsureGpgKeyScript(IdentityProfile profile)
    {
        var selector = GpgKeySelector(profile);
        var uid = GpgUserId(profile);
        return $"gpg --batch --list-secret-keys \"{selector}\" >/dev/null 2>&1 || gpg --batch --pinentry-mode loopback --passphrase '' --quick-generate-key \"{uid}\" ed25519 default never";
    }

    internal static string BuildGpgExportScript(IdentityProfile profile) => $"gpg --armor --export \"{GpgKeySelector(profile)}\"";

    // gpg will not delete a key by uid in batch mode, so the email only locates the key and its
    // fingerprint is what gets deleted.
    internal static string BuildDeleteGpgKeyScript(IdentityProfile profile)
    {
        var selector = GpgKeySelector(profile);
        return $"fpr=$(gpg --batch --with-colons --list-secret-keys \"{selector}\" 2>/dev/null | grep \"^fpr:\" | cut -d: -f10 | head -1); "
            + "if [ -z \"$fpr\" ]; then exit 0; fi; "
            + "gpg --batch --yes --delete-secret-and-public-key \"$fpr\"";
    }

    /// <summary>Reads the token from stdin so it never appears on a command line or in a process list.</summary>
    internal static string BuildGitHubTokenScript()
        => "gh auth login --hostname github.com --with-token";

    /// <summary>
    /// gh prints "Logged in to github.com account NAME" when a token is valid, and writes an error and
    /// "not logged in" otherwise.
    /// </summary>
    internal static GitHubAuthStatus ParseGitHubAuthStatus(string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // A failed sign-in also names the account ("Failed to log in to github.com account NAME"),
            // and a failing gh folds that onto stderr, which is now part of the output.
            if (line.Contains("Failed", StringComparison.OrdinalIgnoreCase)) continue;
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
