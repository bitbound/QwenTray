using System.Text.Json;
using Bitbound.SystemAbstractions.FileSystem;
using Microsoft.Extensions.Logging;
using QwenTray.Updates;

namespace QwenTray.Services;

public sealed record ExportResult(string BundlePath, long BundleBytes);

/// <summary>
/// Moves a whole QwenTray setup between machines. An export stops the distro and copies the virtual disk
/// it boots from together with the host settings as plaintext, because a DPAPI blob only opens for the
/// account that wrote it. An import copies that disk into place and registers it where it lies, so neither
/// direction unpacks a filesystem.
/// </summary>
public sealed class DistroBackupService(
    IWslDistro wsl,
    IFileSystem fileSystem,
    BackupBundle bundle,
    WslDistroLocation location,
    SettingsStore settingsStore,
    Settings settings,
    WslDistroProvisioner provisioner,
    RegistryAutostartService autostart,
    AppInfo appInfo,
    ILogger<DistroBackupService> logger)
{
    // An export copies the distro's disk to the bundle, and an import copies it from the bundle into the
    // folder wsl will boot it from. One copy of the disk is what a volume has to have room for, and this is
    // what is left on top of that.
    private const long HeadroomBytes = 2L * 1024 * 1024 * 1024;

    private static string LocalRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "qwentray");

    public static string BackupDirectory => Path.Combine(LocalRoot, "backups");

    private static string TransferDirectory => Path.Combine(LocalRoot, "transfer");

    private static string WslRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wsl");

    /// <summary>
    /// A bundle folder under <paramref name="directory"/>, or under the app's own backups folder when that
    /// is empty. The name carries a timestamp so an export never lands on an earlier bundle.
    /// </summary>
    public static string DefaultBundleDirectory(string? directory = null) =>
        Path.Combine(
          string.IsNullOrWhiteSpace(directory)
            ? BackupDirectory
            : directory.Trim(),
          $"qwentray-{DateTime.Now:yyyyMMdd-HHmmss}");

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

    /// <summary>
    /// Copies the distro's disk and this machine's settings into a bundle folder. Nothing is destroyed, but
    /// the distro is stopped so its disk is not copied while it is mounted, and it stays stopped.
    /// </summary>
    public async Task<ExportResult> ExportAsync(string bundlePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bundlePath)) throw new InvalidOperationException("Set a bundle path first.");
        if (fileSystem.DirectoryExists(bundlePath)) throw new InvalidOperationException($"\"{bundlePath}\" already exists, so it is left alone. Choose a new name.");

        var distro = settings.WslDistroName.Trim();
        if (!Settings.IsSafeDistroName(distro)) throw new InvalidOperationException($"\"{distro}\" is not a name QwenTray can pass to wsl.exe.");
        if (!await wsl.DistroExistsAsync(distro, cancellationToken)) throw new InvalidOperationException($"The \"{distro}\" distro does not exist, so there is nothing to export.");

        var sourceVhd = location.TryFindVhdPath(distro);
        if (sourceVhd is null || !fileSystem.FileExists(sourceVhd))
        {
            throw new InvalidOperationException($"QwenTray cannot find the virtual disk the \"{distro}\" distro boots from, so there is nothing to export.");
        }

        var vhdBytes = fileSystem.GetFileInfo(sourceVhd).Length;
        RequireFreeSpace(bundlePath, vhdBytes + HeadroomBytes, "hold a copy of the distro disk");

        // A disk that is still mounted holds whatever its writer had in flight, and wsl will not hand it
        // over while it holds it.
        var stopped = await wsl.TerminateAsync(distro, cancellationToken);
        if (!stopped.Succeeded) throw new InvalidOperationException($"Could not stop the \"{distro}\" distro to export it. {stopped.Output}");

        var manifest = new BackupManifest
        {
            CreatedAtUtc = DateTimeOffset.UtcNow,
            QwenTrayVersion = appInfo.Version.ToString(),
            WslDistroName = distro,
            // The registry value names this machine's executable, so only the intent travels.
            AutostartEnabled = autostart.IsEnabled(),
            DistroVhdBytes = vhdBytes,
            SourceUserName = Environment.UserName,
        };

        await bundle.WriteAsync(
            bundlePath,
            manifest,
            JsonSerializer.Serialize(settings, QwenTrayJsonContext.Default.Settings),
            sourceVhd,
            cancellationToken);

        var bundleBytes = fileSystem.GetFiles(bundlePath).Sum(path => fileSystem.GetFileInfo(path).Length);
        logger.LogInformation("Exported the {Distro} distro to {Bundle} ({BundleBytes} bytes).", distro, bundlePath, bundleBytes);
        return new ExportResult(bundlePath, bundleBytes);
    }

    /// <summary>
    /// Registers the bundle's disk as its distro and applies its settings. The bundle's own distro name
    /// wins over whatever the settings file says, so the app always points at what was just imported.
    /// </summary>
    public async Task<BackupManifest> ImportAsync(string bundlePath, CancellationToken cancellationToken)
    {
        var contents = await ProbeAsync(bundlePath, cancellationToken);
        var manifest = contents.Manifest;
        var distro = manifest.WslDistroName;
        var imported = ReadSettings(contents.SettingsJson);

        RequireFreeSpace(
            WslRoot,
            manifest.DistroVhdBytes > 0 ? manifest.DistroVhdBytes + HeadroomBytes : 0,
            "import");

        // The disk is copied to a staging folder first because it is the step that can fail after minutes
        // of work, and until it finishes the distro already on this machine is still the one that boots.
        var staging = Path.Combine(TransferDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            var stagedVhd = BackupBundle.VhdPathIn(staging);
            await bundle.CopyVhdOutAsync(bundlePath, stagedVhd, cancellationToken);

            if (await wsl.DistroExistsAsync(distro, cancellationToken))
            {
                await wsl.TerminateAsync(distro, cancellationToken);
                var removed = await wsl.UnregisterAsync(distro, cancellationToken);
                if (!removed.Succeeded) throw new InvalidOperationException($"Could not remove the existing \"{distro}\" distro to make room for the bundle. {removed.Output}");
            }

            // Resolved only now, because a distro can be registered straight out of the folder this one is
            // about to be copied into, and the copy would land inside a disk that is about to be deleted.
            var target = ResolveImportTarget(distro);
            var targetVhd = BackupBundle.VhdPathIn(target);
            fileSystem.MoveFile(stagedVhd, targetVhd, overwrite: false);

            var result = await wsl.ImportInPlaceAsync(distro, targetVhd, cancellationToken);
            if (!result.Succeeded) throw new InvalidOperationException($"Could not register the distro from the bundle. {result.Output}");

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
            if (fileSystem.DirectoryExists(staging))
            {
                fileSystem.DeleteDirectory(staging, recursive: true);
            }
        }
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
