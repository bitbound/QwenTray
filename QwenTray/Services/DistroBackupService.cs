using System.Text.Json;
using Bitbound.SystemAbstractions.FileSystem;
using Microsoft.Extensions.Logging;
using QwenTray.Updates;

namespace QwenTray.Services;

public sealed record ExportResult(string BundlePath, long BundleBytes, long DistroUsedBytes);

/// <summary>
/// Moves a whole QwenTray setup between machines. An export stops the distro and packs its filesystem
/// together with the host settings as plaintext, because a DPAPI blob only opens for the account that
/// wrote it. An import registers the filesystem again and re-protects the settings for this account.
/// </summary>
public sealed class DistroBackupService(
    IWslDistro wsl,
    IFileSystem fileSystem,
    BackupBundle bundle,
    SettingsStore settingsStore,
    Settings settings,
    WslDistroProvisioner provisioner,
    RegistryAutostartService autostart,
    AppInfo appInfo,
    ILogger<DistroBackupService> logger)
{
    // An export needs room for the tar and for the finished bundle, an import room for the extracted tar
    // and for the filesystem wsl builds out of it. Each copy is roughly the size of the distro itself,
    // and this is the room left on top of that.
    private const long HeadroomBytes = 2L * 1024 * 1024 * 1024;

    private static string LocalRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "qwentray");

    public static string BackupDirectory => Path.Combine(LocalRoot, "backups");

    private static string TransferDirectory => Path.Combine(LocalRoot, "transfer");

    private static string WslRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wsl");

    /// <summary>
    /// A bundle path under <paramref name="directory"/>, or under the app's own backups folder when
    /// that is empty. The name carries a timestamp so an export never lands on an earlier bundle.
    /// </summary>
    public static string DefaultBundlePath(string? directory = null) =>
        Path.Combine(
          string.IsNullOrWhiteSpace(directory) 
            ? BackupDirectory 
            : directory.Trim(), 
          $"qwentray-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

    /// <summary>
    /// Reads and checks a bundle without changing anything, so the page can say what is about to be
    /// destroyed before it asks for permission.
    /// </summary>
    public async Task<BundleContents> ProbeAsync(string bundlePath, CancellationToken cancellationToken)
    {
        var contents = await bundle.ReadAsync(bundlePath, cancellationToken);

        // The name came off another machine's disk and goes straight into a wsl.exe argument list.
        if (!Settings.IsSafeDistroName(contents.Manifest.WslDistroName))
        {
            throw new InvalidOperationException($"The bundle names the distro \"{contents.Manifest.WslDistroName}\", which QwenTray cannot pass to wsl.exe.");
        }

        ReadSettings(contents.SettingsJson);
        return contents;
    }

    public async Task<ExportResult> ExportAsync(string bundlePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bundlePath)) throw new InvalidOperationException("Set a bundle path first.");
        if (fileSystem.FileExists(bundlePath)) throw new InvalidOperationException($"\"{bundlePath}\" already exists, so it is left alone. Choose a new name.");

        var distro = settings.WslDistroName.Trim();
        if (!Settings.IsSafeDistroName(distro)) throw new InvalidOperationException($"\"{distro}\" is not a name QwenTray can pass to wsl.exe.");
        if (!await wsl.DistroExistsAsync(distro, cancellationToken)) throw new InvalidOperationException($"The \"{distro}\" distro does not exist, so there is nothing to export.");

        var usedBytes = await MeasureDistroAsync(distro, cancellationToken);
        // The tar lands on this machine and only the finished bundle goes to the destination, so the two
        // volumes are checked for one copy each rather than one volume for both.
        var neededBytes = usedBytes > 0 ? usedBytes + HeadroomBytes : 0;
        RequireFreeSpace(BackupDirectory, neededBytes, "hold the exported filesystem while the bundle is written");
        RequireFreeSpace(bundlePath, neededBytes, "write the bundle");

        fileSystem.CreateDirectory(BackupDirectory);
        var tempTar = Path.Combine(BackupDirectory, $".{Path.GetFileNameWithoutExtension(bundlePath)}.{Guid.NewGuid():N}.tar");
        try
        {
            // A distro that is still being written to would land in the tar half-written.
            var stopped = await wsl.TerminateAsync(distro, cancellationToken);
            if (!stopped.Succeeded) throw new InvalidOperationException($"Could not stop the \"{distro}\" distro to export it. {stopped.Output}");

            var exported = await wsl.ExportAsync(distro, tempTar, cancellationToken);
            if (!exported.Succeeded) throw new InvalidOperationException($"Could not export the \"{distro}\" distro. {exported.Output}");

            var manifest = new BackupManifest
            {
                CreatedAtUtc = DateTimeOffset.UtcNow,
                QwenTrayVersion = appInfo.Version.ToString(),
                WslDistroName = distro,
                // The registry value names this machine's executable, so only the intent travels.
                AutostartEnabled = autostart.IsEnabled(),
                DistroTarBytes = fileSystem.GetFileInfo(tempTar).Length,
                SourceUserName = Environment.UserName,
            };

            await bundle.WriteAsync(
                bundlePath,
                manifest,
                JsonSerializer.Serialize(settings, QwenTrayJsonContext.Default.Settings),
                tempTar,
                cancellationToken);

            var bundleBytes = fileSystem.GetFileInfo(bundlePath).Length;
            logger.LogInformation("Exported the {Distro} distro to {Bundle} ({BundleBytes} bytes).", distro, bundlePath, bundleBytes);
            return new ExportResult(bundlePath, bundleBytes, usedBytes);
        }
        finally
        {
            if (fileSystem.FileExists(tempTar)) fileSystem.DeleteFile(tempTar);
        }
    }

    /// <summary>
    /// Registers the bundle's filesystem as its distro and applies its settings. The bundle's own distro
    /// name wins over whatever the settings file says, so the app always points at what was just imported.
    /// </summary>
    public async Task<BackupManifest> ImportAsync(string bundlePath, CancellationToken cancellationToken)
    {
        var contents = await ProbeAsync(bundlePath, cancellationToken);
        var manifest = contents.Manifest;
        var distro = manifest.WslDistroName;
        var imported = ReadSettings(contents.SettingsJson);

        RequireFreeSpace(
            WslRoot,
            manifest.DistroTarBytes > 0 ? manifest.DistroTarBytes * 2 + HeadroomBytes : 0,
            "import");

        var workDirectory = Path.Combine(TransferDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            // Unpacking first means a bundle that turns out to be unreadable or short of space leaves the
            // existing distro where it was.
            var tar = await bundle.ExtractTarAsync(bundlePath, workDirectory, cancellationToken);

            if (await wsl.DistroExistsAsync(distro, cancellationToken))
            {
                await wsl.TerminateAsync(distro, cancellationToken);
                var removed = await wsl.UnregisterAsync(distro, cancellationToken);
                if (!removed.Succeeded) throw new InvalidOperationException($"Could not remove the existing \"{distro}\" distro to make room for the bundle. {removed.Output}");
            }

            var target = ResolveImportTarget(distro);
            var result = await wsl.ImportAsync(distro, target, tar, cancellationToken);
            if (!result.Succeeded) throw new InvalidOperationException($"Could not import the \"{distro}\" distro. {result.Output}");

            provisioner.WriteWslConfig();

            imported.WslDistroName = distro;
            settings.CopyFrom(imported);
            settingsStore.Save(settings);
            autostart.SetEnabled(manifest.AutostartEnabled);

            logger.LogInformation("Imported the {Distro} distro from {Bundle} into {Target}.", distro, bundlePath, target);
            return manifest;
        }
        finally
        {
            if (fileSystem.DirectoryExists(workDirectory))
            {
                fileSystem.DeleteDirectory(workDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Bytes actually stored inside the distro, which is what the tar will hold. The VHDX on disk is
    /// sparse, so its size overstates the job. Nothing guarantees this answers, and a run that really is
    /// out of space says so itself, so a failed measurement only drops the preflight.
    /// </summary>
    internal static string BuildMeasureScript() => "df -B1 --output=used / | tail -1 | tr -d ' \t'";

    private async Task<long> MeasureDistroAsync(string distro, CancellationToken cancellationToken)
    {
        var result = await wsl.RunRootScriptAsync(distro, BuildMeasureScript(), cancellationToken);
        if (!result.Succeeded || !long.TryParse(WslDistro.ParseVersion(result.Output), out var usedBytes))
        {
            logger.LogWarning("Could not measure the space used inside {Distro}, so the free space check is skipped.", distro);
            return 0;
        }

        return usedBytes;
    }

    private static Settings ReadSettings(string json)
    {
        var imported = JsonSerializer.Deserialize(json, QwenTrayJsonContext.Default.Settings)
            ?? throw new InvalidOperationException("The bundle's settings could not be read.");

        if (imported.Profiles.Length == 0) imported.Profiles = [Settings.CreateDefaultProfile()];

        // These values are interpolated into scripts that run as root the next time the distro is set up.
        var unsafeProfile = imported.Profiles.FirstOrDefault(profile => !profile.IsShellSafe);
        if (unsafeProfile is not null)
        {
            throw new InvalidOperationException($"The \"{unsafeProfile.Name}\" profile in the bundle holds a user name or path that QwenTray will not run as a command.");
        }

        return imported;
    }

    /// <summary>
    /// wsl refuses a location that already holds files, and the folder left behind by an unregistered
    /// distro is not ours to delete. The plain name is where WSL puts imported distros, so a second
    /// choice is taken only when something still occupies it.
    /// </summary>
    private string ResolveImportTarget(string distro)
    {
        var preferred = Path.Combine(WslRoot, distro);
        var target = !fileSystem.DirectoryExists(preferred) || IsEmpty(preferred)
            ? preferred
            : Path.Combine(WslRoot, $"{distro}-{DateTime.Now:yyyyMMdd-HHmmss}");

        fileSystem.CreateDirectory(target);
        return target;
    }

    private bool IsEmpty(string directory) =>
        fileSystem.GetDirectories(directory).Length == 0
        && fileSystem.GetFiles(directory).Length == 0;

    /// <summary>
    /// Stops an operation that clearly cannot finish before it has destroyed anything. A zero requirement
    /// means nothing could be measured, which is not a reason to refuse.
    /// </summary>
    private void RequireFreeSpace(string path, long requiredBytes, string operation)
    {
        if (requiredBytes <= 0) return;

        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root)) return;

        var drive = fileSystem.GetDrives()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Name.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (drive is null || !drive.IsReady || drive.TotalFreeSpace >= requiredBytes) return;

        throw new InvalidOperationException($"Not enough room on {drive.Name} to {operation} this bundle. It needs about {FormatBytes(requiredBytes)} and {FormatBytes(drive.TotalFreeSpace)} is free.");
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.#} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        _ => $"{bytes / 1024d:0.#} KB",
    };
}
