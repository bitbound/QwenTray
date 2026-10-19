using Bitbound.SystemAbstractions.FileSystem;
using Microsoft.Extensions.Logging;
using QwenTray.Updates;

namespace QwenTray.Services;

/// <summary>
/// Backs up and restores the active profile's Qwen configuration from inside the distro. The Windows
/// <c>%USERPROFILE%\.qwen</c> folder is never read or written.
/// </summary>
public interface IQwenConfigArchiveService
{
    Task<ConfigExportResult> ExportAsync(
        string distro,
        IdentityProfile profile,
        IReadOnlyCollection<string> paths,
        string archivePath,
        CancellationToken cancellationToken);

    Task<ConfigArchiveManifest> ProbeAsync(string archivePath, CancellationToken cancellationToken);

    Task<ConfigRestoreResult> RestoreAsync(
        string distro,
        IdentityProfile profile,
        string archivePath,
        CancellationToken cancellationToken);
}

/// <summary>
/// Drives the archive over the distro share, so each command wakes the distro first.
/// </summary>
public sealed class QwenConfigArchiveService(
    IQwenConfigArchive archive,
    IWslDistro wsl,
    AppInfo appInfo,
    ILogger<QwenConfigArchiveService> logger) : IQwenConfigArchiveService
{
    private static readonly string[] ConfigEntries =
        ["QWEN.md", "agents", "rules", "memories", "skills", "workflows", "settings.json"];

    /// <summary>The absolute Linux paths a profile backs up until the list is edited.</summary>
    public static string[] DefaultPaths(IdentityProfile profile) =>
        [.. ConfigEntries.Select(entry => $"{profile.HomeDirectory}/.qwen/{entry}")];

    /// <summary>
    /// An archive path under <paramref name="directory"/>, or under the app's own backups folder when
    /// that is empty. The name carries a timestamp so an export never lands on an earlier archive.
    /// </summary>
    public static string DefaultArchivePath(string? directory = null) =>
        Path.Combine(
            string.IsNullOrWhiteSpace(directory) ? DistroBackupService.BackupDirectory : directory.Trim(),
            $"qwen-config-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

    public async Task<ConfigExportResult> ExportAsync(
        string distro,
        IdentityProfile profile,
        IReadOnlyCollection<string> paths,
        string archivePath,
        CancellationToken cancellationToken)
    {
        await WakeDistroAsync(distro, profile, cancellationToken);

        var manifest = new ConfigArchiveManifest
        {
            CreatedAtUtc = DateTimeOffset.UtcNow,
            QwenTrayVersion = appInfo.Version.ToString(),
            WslDistroName = distro,
            LinuxUser = profile.LinuxUser,
        };

        var result = await archive.ExportAsync(HostRoot(distro), archivePath, manifest, paths, cancellationToken);
        logger.LogInformation(
            "Wrote {Count} configuration file(s) from {Distro} to {Archive}.",
            result.FilesWritten,
            distro,
            result.ArchivePath);
        return result;
    }

    /// <summary>Reads the manifest without the distro, so the page can ask before anything is written.</summary>
    public async Task<ConfigArchiveManifest> ProbeAsync(string archivePath, CancellationToken cancellationToken) =>
        await archive.ReadManifestAsync(archivePath, cancellationToken);

    public async Task<ConfigRestoreResult> RestoreAsync(
        string distro,
        IdentityProfile profile,
        string archivePath,
        CancellationToken cancellationToken)
    {
        await WakeDistroAsync(distro, profile, cancellationToken);

        var result = await archive.RestoreAsync(HostRoot(distro), archivePath, cancellationToken);
        logger.LogInformation(
            "Restored {Count} configuration file(s) into {Distro} from {Archive}.",
            result.FilesWritten,
            distro,
            archivePath);
        return result;
    }

    private async Task WakeDistroAsync(string distro, IdentityProfile profile, CancellationToken cancellationToken)
    {
        // The \\wsl.localhost share is only reachable while the distro is up.
        await wsl.RunScriptAsync(distro, profile.LinuxUser, "true", cancellationToken);
    }

    private static string HostRoot(string distro) => $@"\\wsl.localhost\{distro}";
}
