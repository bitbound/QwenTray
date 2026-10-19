using System.IO.Compression;
using System.Text.Json;
using Bitbound.SystemAbstractions.FileSystem;

namespace QwenTray.Services;

/// <summary>
/// What a configuration backup holds and where it came from. Every path is absolute and Linux, because
/// the archive only ever reads and writes inside the distro.
/// </summary>
public sealed record ConfigArchiveManifest
{
    public const string Kind = "qwen-config";
    public const int CurrentSchemaVersion = 1;

    public string Type { get; set; } = Kind;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string QwenTrayVersion { get; set; } = "";
    public string WslDistroName { get; set; } = "";
    public string LinuxUser { get; set; } = "";

    /// <summary>The paths that were listed for the export, after normalization.</summary>
    public string[] Paths { get; set; } = [];

    /// <summary>Every file in the archive, as an absolute Linux path.</summary>
    public string[] Files { get; set; } = [];
}

public sealed record ConfigExportResult(string ArchivePath, int FilesWritten, string[] Missing, string[] Invalid);

public sealed record ConfigRestoreResult(int FilesWritten, string[] Missing, string[] Invalid);

/// <summary>
/// Reads and writes the ZIP that holds a configuration backup. <c>hostRoot</c> is the folder the
/// archive is read from and written to, which is the distro's share in the app and a temporary folder
/// in tests.
/// </summary>
public sealed class QwenConfigArchive(IFileSystem fileSystem)
{
    public const string ManifestEntry = "manifest.json";
    private const string FilesPrefix = "files";

    /// <summary>
    /// Packs every listed file into a new archive. A path that does not exist is reported rather than
    /// fatal, so one stale entry does not block the rest of the backup.
    /// </summary>
    public async Task<ConfigExportResult> ExportAsync(
        string hostRoot,
        string archivePath,
        ConfigArchiveManifest manifest,
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken)
    {
        if (fileSystem.FileExists(archivePath))
        {
            throw new InvalidOperationException($"\"{archivePath}\" already exists. Change the archive name rather than overwrite it.");
        }

        var files = new SortedSet<string>(StringComparer.Ordinal);
        var missing = new List<string>();
        var invalid = new List<string>();
        var listed = new List<string>();

        foreach (var raw in paths)
        {
            if (!IsBackupablePath(raw))
            {
                invalid.Add(raw);
                continue;
            }

            var path = Normalize(raw);
            listed.Add(path);
            var host = ToHostPath(hostRoot, path);
            if (fileSystem.FileExists(host))
            {
                files.Add(path);
            }
            else if (fileSystem.DirectoryExists(host))
            {
                AddDirectory(hostRoot, path, files);
            }
            else
            {
                missing.Add(path);
            }
        }

        manifest.Paths = [.. listed];
        manifest.Files = [.. files];

        var parent = Path.GetDirectoryName(archivePath);
        if (!string.IsNullOrEmpty(parent))
        {
            fileSystem.CreateDirectory(parent);
        }

        using var archive = new ZipArchive(
            fileSystem.CreateFileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None),
            ZipArchiveMode.Create,
            leaveOpen: false);

        await WriteBytesAsync(
            archive,
            ManifestEntry,
            JsonSerializer.SerializeToUtf8Bytes(manifest, QwenTrayJsonContext.Default.ConfigArchiveManifest),
            cancellationToken);

        foreach (var path in files)
        {
            var entry = archive.CreateEntry(EntryNameFor(path), CompressionLevel.Optimal);
            await using var target = entry.Open();
            await using var source = fileSystem.OpenFileStream(ToHostPath(hostRoot, path), FileMode.Open, FileAccess.Read, FileShare.Read);
            await source.CopyToAsync(target, cancellationToken);
        }

        return new ConfigExportResult(archivePath, files.Count, [.. missing], [.. invalid]);
    }

    /// <summary>Reads and checks the manifest without touching the files it names.</summary>
    public async Task<ConfigArchiveManifest> ReadManifestAsync(string archivePath, CancellationToken cancellationToken)
    {
        using var archive = OpenArchive(archivePath);

        var manifest = JsonSerializer.Deserialize(
            await ReadEntryBytesAsync(archive, ManifestEntry, cancellationToken),
            QwenTrayJsonContext.Default.ConfigArchiveManifest)
            ?? throw new InvalidOperationException($"The archive has no usable {ManifestEntry}.");

        if (!string.Equals(manifest.Type, ConfigArchiveManifest.Kind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("That ZIP is not a Qwen configuration backup.");
        }

        if (manifest.SchemaVersion > ConfigArchiveManifest.CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"This archive was written by a newer QwenTray (schema {manifest.SchemaVersion}). Update QwenTray before restoring it.");
        }

        if (manifest.SchemaVersion < ConfigArchiveManifest.CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"This archive uses schema {manifest.SchemaVersion}, which this QwenTray no longer reads.");
        }

        return manifest;
    }

    /// <summary>
    /// Writes the files the manifest names, each to the absolute path recorded for it. Members the
    /// manifest does not name are never opened, so a crafted archive cannot add a file.
    /// </summary>
    public async Task<ConfigRestoreResult> RestoreAsync(string hostRoot, string archivePath, CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(archivePath, cancellationToken);
        using var archive = OpenArchive(archivePath);

        var restored = 0;
        var missing = new List<string>();
        var invalid = new List<string>();

        foreach (var raw in manifest.Files)
        {
            if (!IsBackupablePath(raw))
            {
                invalid.Add(raw);
                continue;
            }

            var path = Normalize(raw);
            var entry = archive.GetEntry(EntryNameFor(path));
            if (entry is null)
            {
                missing.Add(path);
                continue;
            }

            var host = ToHostPath(hostRoot, path);
            var parent = Path.GetDirectoryName(host);
            if (!string.IsNullOrEmpty(parent))
            {
                fileSystem.CreateDirectory(parent);
            }

            await using (var target = fileSystem.CreateFileStream(host, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var source = await entry.OpenAsync(cancellationToken))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            restored++;
        }

        return new ConfigRestoreResult(restored, [.. missing], [.. invalid]);
    }

    /// <summary>
    /// A backup path is an absolute Linux path. Relative paths and <c>..</c> segments are refused, so a
    /// listed path cannot be re-read as something other than the folder it names.
    /// </summary>
    internal static bool IsBackupablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var trimmed = path.Trim();
        if (!trimmed.StartsWith('/')) return false;
        if (trimmed.Contains('\\') || trimmed.Contains(':')) return false;
        if (trimmed.Any(char.IsControl)) return false;

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(segment => segment is not "." and not "..");
    }

    internal static string Normalize(string path) =>
        "/" + string.Join('/', path.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries));

    internal static string EntryNameFor(string path) => $"{FilesPrefix}/{path.TrimStart('/')}";

    internal static string ToHostPath(string hostRoot, string linuxPath) =>
        Path.Combine(hostRoot, linuxPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    private void AddDirectory(string hostRoot, string linuxPath, ISet<string> files)
    {
        var host = ToHostPath(hostRoot, linuxPath);
        var trimmed = linuxPath.TrimEnd('/');

        foreach (var file in fileSystem.GetFiles(host))
        {
            files.Add($"{trimmed}/{Path.GetFileName(file)}");
        }

        foreach (var directory in fileSystem.GetDirectories(host))
        {
            AddDirectory(hostRoot, $"{trimmed}/{Path.GetFileName(directory)}", files);
        }
    }

    private ZipArchive OpenArchive(string archivePath)
    {
        if (!fileSystem.FileExists(archivePath))
        {
            throw new FileNotFoundException($"No archive at {archivePath}.", archivePath);
        }

        return new ZipArchive(
            fileSystem.OpenFileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read),
            ZipArchiveMode.Read,
            leaveOpen: false);
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
        // An exact match, because an archive can name a member "../../manifest.json".
        var entry = archive.GetEntry(name) ?? throw new InvalidOperationException($"The archive has no {name}.");
        return entry.OpenAsync(cancellationToken);
    }
}
