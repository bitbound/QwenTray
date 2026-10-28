using System.Text;
using System.Text.Json;
using Bitbound.SystemAbstractions.FileSystem;

namespace QwenTray.Services;

/// <summary>
/// What a bundle holds and where it came from. The name inside it is input from another machine, so it
/// is validated before it reaches wsl.exe.
/// </summary>
public sealed record BackupManifest
{
    /// <summary>Two, because a bundle carries the distro's virtual disk. Version one held a filesystem tar.</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string QwenTrayVersion { get; set; } = "";
    public string WslDistroName { get; set; } = "";

    /// <summary>Whether Start with Windows was on, carried as a flag because the registry value names the source machine's executable.</summary>
    public bool AutostartEnabled { get; set; }

    public long DistroVhdBytes { get; set; }
    public string SourceUserName { get; set; } = "";
}

/// <summary>The manifest and the decrypted settings that came out of one bundle.</summary>
public sealed record BundleContents(BackupManifest Manifest, string SettingsJson);

/// <summary>
/// Reads and writes a bundle: a folder holding a manifest, the settings as plaintext, and the
/// <c>ext4.vhdx</c> the distro boots from. The disk is a sibling of the two small files rather than a
/// member of an archive, because an import hands that same file straight to <c>wsl --import-in-place</c>
/// and a copy made on the way in or out would be the whole cost of the operation.
/// </summary>
public sealed class BackupBundle(IFileSystem fileSystem)
{
    public const string ManifestEntry = "manifest.json";
    public const string SettingsEntry = "settings.json";
    public const string VhdEntry = WslDistroLocation.VhdFileName;

    /// <summary>
    /// Bytes handled per step of a disk copy. The disk is read once either way, so this is only about
    /// keeping the number of writes down.
    /// </summary>
    private const int ChunkBytes = 1024 * 1024;

    public static string VhdPathIn(string bundleDirectory) => Path.Combine(bundleDirectory, VhdEntry);

    /// <summary>
    /// Writes the bundle into a folder that does not exist yet, so a mistyped name cannot destroy an
    /// earlier backup. The folder appears only once its three files are complete, which keeps a run that
    /// died halfway from leaving something that looks like a backup.
    /// </summary>
    public async Task WriteAsync(string bundleDirectory, BackupManifest manifest, string settingsJson, string sourceVhdPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bundleDirectory)) throw new InvalidOperationException("Set a bundle path first.");
        if (fileSystem.DirectoryExists(bundleDirectory))
        {
            throw new InvalidOperationException($"\"{bundleDirectory}\" already exists. Change the bundle name rather than overwrite it.");
        }

        if (!fileSystem.FileExists(sourceVhdPath))
        {
            throw new FileNotFoundException($"The distro's virtual disk is missing at {sourceVhdPath}.", sourceVhdPath);
        }

        var parent = Path.GetDirectoryName(bundleDirectory);
        if (string.IsNullOrEmpty(parent)) parent = ".";
        fileSystem.CreateDirectory(parent);

        var staging = Path.Combine(parent, $".{Path.GetFileName(bundleDirectory)}.staging-{Guid.NewGuid():N}");
        try
        {
            fileSystem.CreateDirectory(staging);
            await fileSystem.WriteAllBytesAsync(
                Path.Combine(staging, ManifestEntry),
                JsonSerializer.SerializeToUtf8Bytes(manifest, QwenTrayJsonContext.Default.BackupManifest),
                cancellationToken);
            await fileSystem.WriteAllBytesAsync(
                Path.Combine(staging, SettingsEntry),
                Encoding.UTF8.GetBytes(settingsJson),
                cancellationToken);
            await CopyAsync(sourceVhdPath, VhdPathIn(staging), cancellationToken);

            fileSystem.MoveDirectory(staging, bundleDirectory);
        }
        finally
        {
            if (fileSystem.DirectoryExists(staging)) fileSystem.DeleteDirectory(staging, recursive: true);
        }
    }

    public async Task<BundleContents> ReadAsync(string bundleDirectory, CancellationToken cancellationToken)
    {
        if (!fileSystem.DirectoryExists(bundleDirectory))
        {
            throw new FileNotFoundException($"No bundle at {bundleDirectory}.", bundleDirectory);
        }

        var manifest = JsonSerializer.Deserialize(
            await ReadEntryBytesAsync(bundleDirectory, ManifestEntry, cancellationToken),
            QwenTrayJsonContext.Default.BackupManifest) ?? throw new InvalidOperationException($"The bundle has no usable {ManifestEntry}.");

        if (manifest.SchemaVersion > BackupManifest.CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"This bundle was written by a newer QwenTray (schema {manifest.SchemaVersion}). Update QwenTray before importing it.");
        }

        if (manifest.SchemaVersion < BackupManifest.CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"This bundle uses schema {manifest.SchemaVersion}, which this QwenTray no longer reads.");
        }

        return new BundleContents(
            manifest,
            Encoding.UTF8.GetString(await ReadEntryBytesAsync(bundleDirectory, SettingsEntry, cancellationToken)));
    }

    /// <summary>
    /// Copies the bundle's disk to <paramref name="destinationVhdPath"/>, which is where the distro will
    /// boot it from. The bundle keeps its own copy, so a bundle stays a backup after an import rather than
    /// becoming something whose deletion destroys a live distro.
    /// </summary>
    public async Task<string> CopyVhdOutAsync(string bundleDirectory, string destinationVhdPath, CancellationToken cancellationToken)
    {
        var source = VhdPathIn(bundleDirectory);
        if (!fileSystem.FileExists(source))
        {
            throw new FileNotFoundException($"The bundle at {bundleDirectory} has no {VhdEntry}.", source);
        }

        var parent = Path.GetDirectoryName(destinationVhdPath);
        if (!string.IsNullOrEmpty(parent)) fileSystem.CreateDirectory(parent);

        await CopyAsync(source, destinationVhdPath, cancellationToken);
        return destinationVhdPath;
    }

    private async Task<byte[]> ReadEntryBytesAsync(string bundleDirectory, string entry, CancellationToken cancellationToken)
    {
        var path = Path.Combine(bundleDirectory, entry);
        if (!fileSystem.FileExists(path)) throw new FileNotFoundException($"The bundle has no {entry}.", path);

        return await fileSystem.ReadAllBytesAsync(path);
    }

    private async Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var buffer = new byte[ChunkBytes];

        await using var source = fileSystem.OpenFileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var target = fileSystem.CreateFileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            // A disk image still holds what its owner deleted, and a distro that was never trimmed carries
            // whole regions of zero. Writing those out costs a full pass over bytes nobody will read, so
            // the position moves past them instead and the region stays unallocated at the destination.
            if (ContainsData(buffer.AsSpan(0, read)))
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            else
            {
                target.Position += read;
            }
        }

        // The loop above never writes a run of zeros at the end of the file, and the vhdx footer has to sit
        // at the very end, so the length is stated rather than left to the last write.
        target.SetLength(source.Length);
    }

    internal static bool ContainsData(ReadOnlySpan<byte> chunk) => chunk.IndexOfAnyExcept((byte)0) >= 0;
}
