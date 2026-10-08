using Bitbound.SystemAbstractions.FileSystem;
using Microsoft.Extensions.Logging;

namespace QwenTray.Services;

public sealed record SyncResult(int FilesCopied, string[] Skipped);

/// <summary>
/// Copies the chosen configuration entries between the Windows Qwen Code folder and the active
/// profile's <c>~/.qwen</c>. Files move over the distro share, so the distro has to be running.
/// </summary>
public sealed class WslConfigSyncService(
    IFileSystem fileSystem,
    IWslDistro wsl,
    ILogger<WslConfigSyncService> logger)
{
    public static string WindowsConfigRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".qwen");

    public static string DistroConfigRoot(string distro, IdentityProfile profile) =>
        $@"\\wsl.localhost\{distro}\home\{profile.LinuxUser}\.qwen";

    public async Task<SyncResult> PushAsync(string distro, IdentityProfile profile, IEnumerable<string> entries, CancellationToken cancellationToken)
        => await SyncAsync(WindowsConfigRoot, DistroConfigRoot(distro, profile), distro, profile, entries, cancellationToken);

    public async Task<SyncResult> PullAsync(string distro, IdentityProfile profile, IEnumerable<string> entries, CancellationToken cancellationToken)
        => await SyncAsync(DistroConfigRoot(distro, profile), WindowsConfigRoot, distro, profile, entries, cancellationToken);

    private async Task<SyncResult> SyncAsync(string sourceRoot, string destinationRoot, string distro, IdentityProfile profile, IEnumerable<string> entries, CancellationToken cancellationToken)
    {
        // The \\wsl.localhost share is only reachable while the distro is up.
        await wsl.RunScriptAsync(distro, profile.LinuxUser, "true", cancellationToken);

        var skipped = new List<string>();
        if (!fileSystem.DirectoryExists(sourceRoot))
        {
            logger.LogWarning("The sync source {SourceRoot} does not exist.", sourceRoot);
            return new SyncResult(0, []);
        }

        var copied = 0;
        foreach (var entry in entries)
        {
            if (!IsSyncableEntry(entry))
            {
                skipped.Add(entry);
                continue;
            }

            var source = Path.Combine(sourceRoot, entry);
            var destination = Path.Combine(destinationRoot, entry);
            if (fileSystem.DirectoryExists(source))
            {
                copied += CopyDirectory(source, destination);
            }
            else if (fileSystem.FileExists(source))
            {
                fileSystem.CreateDirectory(Path.GetDirectoryName(destination)!);
                fileSystem.CopyFile(source, destination, overwrite: true);
                copied++;
            }
        }

        logger.LogInformation("Synced {Count} file(s) from {Source} to {Destination}.", copied, sourceRoot, destinationRoot);
        return new SyncResult(copied, [.. skipped]);
    }

    private int CopyDirectory(string source, string destination)
    {
        fileSystem.CreateDirectory(destination);
        var copied = 0;
        foreach (var file in fileSystem.GetFiles(source))
        {
            fileSystem.CopyFile(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            copied++;
        }

        foreach (var directory in fileSystem.GetDirectories(source))
        {
            copied += CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        return copied;
    }

    /// <summary>
    /// Rejects entries that would escape the config folder, plus the two roots that must never sync:
    /// <c>settings.json</c> carries per-machine secrets, and <c>projects</c> is keyed by absolute path.
    /// </summary>
    internal static bool IsSyncableEntry(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return false;
        var trimmed = entry.Trim().Replace('\\', '/');
        if (trimmed.StartsWith('/')) return false;

        var normalized = trimmed.Trim('/');
        if (normalized.Length == 0) return false;
        if (normalized.Contains("..", StringComparison.Ordinal)) return false;
        if (normalized.Contains(':')) return false;

        var top = normalized.Split('/')[0];
        return !string.Equals(top, "settings.json", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(top, "projects", StringComparison.OrdinalIgnoreCase);
    }
}
