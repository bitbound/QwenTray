using System.IO.Compression;
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
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string QwenTrayVersion { get; set; } = "";
    public string WslDistroName { get; set; } = "";

    /// <summary>Whether Start with Windows was on, carried as a flag because the registry value names the source machine's executable.</summary>
    public bool AutostartEnabled { get; set; }

    public long DistroTarBytes { get; set; }
    public string SourceUserName { get; set; } = "";
}

/// <summary>The manifest and the decrypted settings that came out of one bundle.</summary>
public sealed record BundleContents(BackupManifest Manifest, string SettingsJson);

/// <summary>
/// Reads and writes the single-file bundle: a zip holding a manifest, the settings as plaintext, and the
/// tar that <see cref="IWslDistro.ExportAsync"/> wrote.
/// </summary>
public sealed class BackupBundle(IFileSystem fileSystem)
{
    public const string ManifestEntry = "manifest.json";
    public const string SettingsEntry = "settings.json";
    public const string TarEntry = "distro.tar";

    public static string TarPathIn(string destinationDirectory) => Path.Combine(destinationDirectory, TarEntry);

    /// <summary>
    /// Writes the bundle, refusing a path that already holds a file so a mistyped name cannot destroy an
    /// earlier backup. The tar is stored rather than compressed, because it is already dense and would
    /// cost minutes to deflate for almost nothing.
    /// </summary>
    public async Task WriteAsync(string bundlePath, BackupManifest manifest, string settingsJson, string tarPath, CancellationToken cancellationToken)
    {
        if (fileSystem.FileExists(bundlePath))
        {
            throw new InvalidOperationException($"\"{bundlePath}\" already exists. Change the bundle name rather than overwrite it.");
        }

        var parent = Path.GetDirectoryName(bundlePath);
        if (!string.IsNullOrEmpty(parent))
        {
            fileSystem.CreateDirectory(parent);
        }

        if (!fileSystem.FileExists(tarPath))
        {
            throw new FileNotFoundException($"The exported filesystem is missing at {tarPath}.", tarPath);
        }

        using var archive = new ZipArchive(
            fileSystem.CreateFileStream(bundlePath, FileMode.CreateNew, FileAccess.Write, FileShare.None),
            ZipArchiveMode.Create,
            leaveOpen: false);

        await WriteBytesAsync(archive, ManifestEntry, JsonSerializer.SerializeToUtf8Bytes(manifest, QwenTrayJsonContext.Default.BackupManifest), cancellationToken);
        await WriteBytesAsync(archive, SettingsEntry, Encoding.UTF8.GetBytes(settingsJson), cancellationToken);

        var entry = archive.CreateEntry(TarEntry, CompressionLevel.NoCompression);
        await using (var target = entry.Open())
        await using (var source = fileSystem.OpenFileStream(tarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await source.CopyToAsync(target, cancellationToken);
        }
    }

    public async Task<BundleContents> ReadAsync(string bundlePath, CancellationToken cancellationToken)
    {
        using var archive = OpenArchive(bundlePath);

        var manifest = JsonSerializer.Deserialize(
            await ReadEntryBytesAsync(archive, ManifestEntry, cancellationToken),
            QwenTrayJsonContext.Default.BackupManifest) ?? throw new InvalidOperationException($"The bundle has no usable {ManifestEntry}.");

        if (manifest.SchemaVersion > BackupManifest.CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"This bundle was written by a newer QwenTray (schema {manifest.SchemaVersion}). Update QwenTray before importing it.");
        }

        if (manifest.SchemaVersion < BackupManifest.CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"This bundle uses schema {manifest.SchemaVersion}, which this QwenTray no longer reads.");
        }

        return new BundleContents(manifest, Encoding.UTF8.GetString(await ReadEntryBytesAsync(archive, SettingsEntry, cancellationToken)));
    }

    /// <summary>
    /// Copies the filesystem tar out of the bundle, because <c>wsl --import</c> reads a file rather than
    /// an archive member. Only the one named member is written, so a member crafted to escape the
    /// destination directory is never opened.
    /// </summary>
    public async Task<string> ExtractTarAsync(string bundlePath, string destinationDirectory, CancellationToken cancellationToken)
    {
        fileSystem.CreateDirectory(destinationDirectory);

        using var archive = OpenArchive(bundlePath);
        var target = TarPathIn(destinationDirectory);

        await using (var output = fileSystem.CreateFileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var input = await ReadEntryStreamAsync(archive, TarEntry, cancellationToken))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        return target;
    }

    private ZipArchive OpenArchive(string bundlePath)
    {
        if (!fileSystem.FileExists(bundlePath))
        {
            throw new FileNotFoundException($"No bundle at {bundlePath}.", bundlePath);
        }

        return new ZipArchive(fileSystem.OpenFileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read), ZipArchiveMode.Read, leaveOpen: false);
    }

    private static async Task WriteBytesAsync(ZipArchive archive, string name, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task<byte[]> ReadEntryBytesAsync(ZipArchive archive, string name, CancellationToken cancellationToken)
    {
        await using var stream = await ReadEntryStreamAsync(archive, name, cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static Task<Stream> ReadEntryStreamAsync(ZipArchive archive, string name, CancellationToken cancellationToken)
    {
        // An exact match, because a malicious archive can name a member "../../manifest.json".
        var entry = archive.GetEntry(name) ?? throw new InvalidOperationException($"The bundle has no {name}.");
        return entry.OpenAsync(cancellationToken);
    }
}
