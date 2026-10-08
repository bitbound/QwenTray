using System.Text;
using Bitbound.SystemAbstractions;
using Bitbound.SystemAbstractions.FileSystem;
using Microsoft.Extensions.Logging;

namespace QwenTray.Services;

/// <summary>
/// Creates the QwenTray distro and makes it a sealed identity host: no Windows interop, no drive
/// automount, one Linux user per profile, qwen and gh installed.
/// </summary>
public sealed class WslDistroProvisioner(
    IWslDistro wsl,
    IFileSystem fileSystem,
    ISystemEnvironment environment,
    ILogger<WslDistroProvisioner> logger)
{
    public const string SourceDistro = "Ubuntu-26.04";

    private const string QwenInstallCommand = "curl -fsSL https://qwen-code-assets.oss-cn-hangzhou.aliyuncs.com/installation/install-qwen-standalone.sh | bash";

    public string WindowsConfigFilePath => Path.Combine(environment.GetProfileDirectory(), ".wslconfig");

    public async Task<bool> IsProvisionedAsync(string distro, CancellationToken cancellationToken)
    {
        var result = await wsl.RunRootScriptAsync(distro, "test -f /etc/qwentray-provisioned && echo ready", cancellationToken);
        return result.Succeeded && result.Output.Contains("ready", StringComparison.Ordinal);
    }

    public async Task ProvisionAsync(string distro, IReadOnlyList<IdentityProfile> profiles, CancellationToken cancellationToken)
    {
        logger.LogInformation("Provisioning distro {Distro} with {ProfileCount} profile(s).", distro, profiles.Count);

        if (!await wsl.DistroExistsAsync(distro, cancellationToken))
        {
            var install = await wsl.InstallDistroAsync(distro, cancellationToken);
            if (!install.Succeeded) throw new InvalidOperationException($"Could not create the {distro} distro. {install.Output}");
        }

        // Interop and automount are what make a distro able to read C: and run Windows binaries, so
        // both are off before any user is created.
        await RequireAsync(distro, BuildWriteFileScript("/etc/wsl.conf", BuildWslConf(profiles[0].LinuxUser)), cancellationToken);
        await RequireAsync(distro, "apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y git gh sudo openssh-client curl ca-certificates", cancellationToken);

        foreach (var profile in profiles)
        {
            await RequireAsync(distro, BuildCreateUserScript(profile), cancellationToken);
            await RequireAsync(distro, BuildInstallQwenScript(profile), cancellationToken);
        }

        await RequireAsync(distro, "touch /etc/qwentray-provisioned", cancellationToken);
        logger.LogInformation("Provisioned distro {Distro}.", distro);
    }

    public async Task<WslCommandResult> InstallOrUpdateQwenAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
        => await wsl.RunScriptAsync(distro, profile.LinuxUser, QwenInstallCommand, cancellationToken);

    public async Task<string?> ReadQwenVersionAsync(string distro, IdentityProfile profile, string qwenExecutable, CancellationToken cancellationToken)
    {
        var result = await wsl.RunScriptAsync(distro, profile.LinuxUser, $"{qwenExecutable} --version", cancellationToken);
        return result.Succeeded ? WslDistro.ParseVersion(result.Output) : null;
    }

    public async Task<string?> ResolveQwenPathAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        var result = await wsl.RunScriptAsync(distro, profile.LinuxUser, "command -v qwen", cancellationToken);
        return result.Succeeded ? WslDistro.ParseAbsolutePath(result.Output) : null;
    }

    public void WriteWslConfig()
    {
        var path = WindowsConfigFilePath;
        var existing = fileSystem.FileExists(path) ? fileSystem.ReadAllText(path) : string.Empty;
        var merged = MergeWslConfig(existing);
        fileSystem.WriteAllText(path, merged);
        logger.LogInformation("Wrote {Path}. Run 'wsl --shutdown' before the networking change takes effect.", path);
    }

    /// <summary>
    /// Ensures the mirrored networking the loopback health check depends on, without dropping keys the
    /// user already set. Mirrored mode also makes <c>127.0.0.1</c> usable in both directions and
    /// removes the IPv6 loopback gap that NAT mode has.
    /// </summary>
    internal static string MergeWslConfig(string existing)
    {
        // Normalize to bare LF first so a CRLF file does not leave stray carriage returns in lines.
        var lines = existing
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();

        var wsl2Index = lines.FindIndex(line => string.Equals(line.Trim(), "[wsl2]", StringComparison.OrdinalIgnoreCase));
        if (wsl2Index < 0)
        {
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count > 0) lines.Add(string.Empty);
            lines.Add("[wsl2]");
            lines.Add("networkingMode=mirrored");
        }
        else
        {
            var insertAt = wsl2Index + 1;
            while (insertAt < lines.Count && !lines[insertAt].TrimStart().StartsWith('[') && lines[insertAt].Trim().Length != 0)
            {
                if (lines[insertAt].TrimStart().StartsWith("networkingMode", StringComparison.OrdinalIgnoreCase))
                {
                    lines[insertAt] = "networkingMode=mirrored";
                    break;
                }
                insertAt++;
            }

            if (insertAt >= lines.Count || lines[insertAt].Trim().Length == 0 || lines[insertAt].TrimStart().StartsWith('['))
            {
                lines.Insert(insertAt, "networkingMode=mirrored");
            }
        }

        return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
    }

    internal static string BuildWslConf(string defaultUser) =>
        $"""
        [boot]
        systemd=true

        [automount]
        enabled=false

        [interop]
        enabled=false
        appendWindowsPath=false

        [user]
        default={defaultUser}
        """;

    /// <summary>
    /// Writes a file as root by base64-encoding the content, which keeps newlines, quotes, and
    /// pipes out of the shell command line entirely.
    /// </summary>
    internal static string BuildWriteFileScript(string path, string content)
        => $"printf %s {Convert.ToBase64String(Encoding.UTF8.GetBytes(content))} | base64 -d > {path}";

    internal static string BuildCreateUserScript(IdentityProfile profile) =>
        $"id -u {profile.LinuxUser} >/dev/null 2>&1 || useradd -m -s /bin/bash {profile.LinuxUser}";

    internal static string BuildInstallQwenScript(IdentityProfile profile) =>
        $"sudo -u {profile.LinuxUser} -H bash -lc '{QwenInstallCommand} && mkdir -p {profile.ResolvedWorkspaceDirectory}'";

    private async Task RequireAsync(string distro, string script, CancellationToken cancellationToken)
    {
        var result = await wsl.RunRootScriptAsync(distro, script, cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException($"Provisioning step failed. {result.Output}");
    }
}
