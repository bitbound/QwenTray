using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Bitbound.SystemAbstractions.TestUtilities.FileSystem;
using QwenTray.Services;
using Xunit;

namespace QwenTray.Tests;

public sealed class ConfigArchiveTests
{
    private const string HostRoot = "/distro";
    private const string Archive = "/backups/qwen-config.zip";

    [Fact]
    public async Task AnExportCarriesTheListedFilesAndEveryFileUnderAFolder()
    {
        var fileSystem = ConfigFiles();
        var archive = new QwenConfigArchive(fileSystem);

        var result = await archive.ExportAsync(
            HostRoot,
            Archive,
            Manifest(),
            ["/home/qwen/.qwen/QWEN.md", "/home/qwen/.qwen/agents", "/home/qwen/.qwen/missing.md"],
            CancellationToken.None);

        Assert.Equal(3, result.FilesWritten);
        Assert.Equal(["/home/qwen/.qwen/missing.md"], result.Missing);
        Assert.Empty(result.Invalid);

        var manifest = await archive.ReadManifestAsync(Archive, CancellationToken.None);
        Assert.Equal(ConfigArchiveManifest.Kind, manifest.Type);
        Assert.Equal("QwenTray", manifest.WslDistroName);
        Assert.Equal("qwen", manifest.LinuxUser);
        Assert.Equal(
            [
                "/home/qwen/.qwen/QWEN.md",
                "/home/qwen/.qwen/agents/planner.md",
                "/home/qwen/.qwen/agents/reviewer.md",
            ],
            manifest.Files);
    }

    [Fact]
    public async Task ARestoreWritesEveryFileBackToThePathRecordedForIt()
    {
        var fileSystem = ConfigFiles();
        var archive = new QwenConfigArchive(fileSystem);
        await archive.ExportAsync(HostRoot, Archive, Manifest(), ["/home/qwen/.qwen"], CancellationToken.None);

        var result = await archive.RestoreAsync("/restored", Archive, CancellationToken.None);

        Assert.Equal(4, result.FilesWritten);
        Assert.Empty(result.Missing);
        Assert.Equal("the rules", await ReadTextAsync(fileSystem, "/restored/home/qwen/.qwen/QWEN.md"));
        Assert.Equal("""{"apiKey":"sekret"}""", await ReadTextAsync(fileSystem, "/restored/home/qwen/.qwen/settings.json"));
        Assert.Equal("the planner", await ReadTextAsync(fileSystem, "/restored/home/qwen/.qwen/agents/planner.md"));
        Assert.Equal("the reviewer", await ReadTextAsync(fileSystem, "/restored/home/qwen/.qwen/agents/reviewer.md"));
    }

    [Fact]
    public async Task AnExistingArchiveIsNotOverwritten()
    {
        var fileSystem = ConfigFiles();
        var existing = Encoding.UTF8.GetBytes("an earlier backup");
        fileSystem.AddFile(Archive, existing, System.IO.FileAttributes.Normal, null);
        var archive = new QwenConfigArchive(fileSystem);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => archive.ExportAsync(HostRoot, Archive, Manifest(), ["/home/qwen/.qwen/QWEN.md"], CancellationToken.None));

        Assert.Equal(existing, await fileSystem.ReadAllBytesAsync(Archive));
    }

    [Fact]
    public async Task AnArchiveFromANewerQwenTrayIsRefused()
    {
        var fileSystem = ConfigFiles();
        var archive = new QwenConfigArchive(fileSystem);
        var manifest = Manifest();
        manifest.SchemaVersion = ConfigArchiveManifest.CurrentSchemaVersion + 1;

        await archive.ExportAsync(HostRoot, Archive, manifest, ["/home/qwen/.qwen/QWEN.md"], CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => archive.ReadManifestAsync(Archive, CancellationToken.None));
        Assert.Contains("newer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AZipThatIsNotAConfigArchiveIsRefused()
    {
        var fileSystem = ConfigFiles();
        var archive = new QwenConfigArchive(fileSystem);
        var manifest = Manifest();
        manifest.Type = "qwentray-bundle";

        await archive.ExportAsync(HostRoot, Archive, manifest, ["/home/qwen/.qwen/QWEN.md"], CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => archive.ReadManifestAsync(Archive, CancellationToken.None));
        Assert.Contains("not a Qwen configuration backup", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheFilesNamedInTheManifestAreRestored()
    {
        var fileSystem = new FakeFileSystem();
        var manifest = Manifest();
        manifest.Files = ["/home/qwen/.qwen/QWEN.md"];
        await WriteArchiveAsync(
            fileSystem,
            manifest,
            new Dictionary<string, string>
            {
                ["files/home/qwen/.qwen/QWEN.md"] = "kept",
                ["files/home/qwen/.ssh/authorized_keys"] = "planted",
            });

        var result = await new QwenConfigArchive(fileSystem).RestoreAsync(HostRoot, Archive, CancellationToken.None);

        Assert.Equal(1, result.FilesWritten);
        Assert.Equal("kept", await ReadTextAsync(fileSystem, "/distro/home/qwen/.qwen/QWEN.md"));
        Assert.False(fileSystem.FileExists("/distro/home/qwen/.ssh/authorized_keys"));
    }

    [Fact]
    public async Task AFileTheArchiveDoesNotHoldIsReportedRatherThanFatal()
    {
        var fileSystem = new FakeFileSystem();
        var manifest = Manifest();
        manifest.Files = ["/home/qwen/.qwen/QWEN.md", "/home/qwen/.qwen/gone.md"];
        await WriteArchiveAsync(
            fileSystem,
            manifest,
            new Dictionary<string, string> { ["files/home/qwen/.qwen/QWEN.md"] = "kept" });

        var result = await new QwenConfigArchive(fileSystem).RestoreAsync(HostRoot, Archive, CancellationToken.None);

        Assert.Equal(1, result.FilesWritten);
        Assert.Equal(["/home/qwen/.qwen/gone.md"], result.Missing);
    }

    [Theory]
    [InlineData("/home/qwen/.qwen/QWEN.md")]
    [InlineData("/home/qwen/.qwen/agents/reviewer.md")]
    [InlineData("/etc")]
    [InlineData("/home/qwen/.qwen/with space.md")]
    public void AnAbsoluteLinuxPathIsBackupable(string path)
    {
        Assert.True(QwenConfigArchive.IsBackupablePath(path));
    }

    [Theory]
    [InlineData("QWEN.md")]
    [InlineData("../secret")]
    [InlineData("/home/qwen/../../etc/passwd")]
    [InlineData("/home/qwen/.qwen/./x")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData("/home/qwen/.qwen/file:name")]
    [InlineData("")]
    [InlineData("   ")]
    public void ARelativeOrEscapingPathIsRejected(string path)
    {
        Assert.False(QwenConfigArchive.IsBackupablePath(path));
    }

    [Fact]
    public void TheDefaultPathsCoverTheQwenConfigAndSettingsJson()
    {
        var paths = QwenConfigArchiveService.DefaultPaths(new IdentityProfile { LinuxUser = "trans" });

        Assert.Equal(
            [
                "/home/trans/.qwen/QWEN.md",
                "/home/trans/.qwen/agents",
                "/home/trans/.qwen/rules",
                "/home/trans/.qwen/memories",
                "/home/trans/.qwen/skills",
                "/home/trans/.qwen/workflows",
                "/home/trans/.qwen/settings.json",
            ],
            paths);
    }

    private static FakeFileSystem ConfigFiles()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile("/distro/home/qwen/.qwen/QWEN.md", "the rules"u8.ToArray(), System.IO.FileAttributes.Normal, null);
        fileSystem.AddFile("/distro/home/qwen/.qwen/settings.json", """{"apiKey":"sekret"}"""u8.ToArray(), System.IO.FileAttributes.Normal, null);
        fileSystem.AddFile("/distro/home/qwen/.qwen/agents/planner.md", "the planner"u8.ToArray(), System.IO.FileAttributes.Normal, null);
        fileSystem.AddFile("/distro/home/qwen/.qwen/agents/reviewer.md", "the reviewer"u8.ToArray(), System.IO.FileAttributes.Normal, null);
        return fileSystem;
    }

    private static ConfigArchiveManifest Manifest() => new()
    {
        CreatedAtUtc = new DateTimeOffset(2026, 10, 19, 10, 30, 0, TimeSpan.Zero),
        QwenTrayVersion = "1.4.0",
        WslDistroName = "QwenTray",
        LinuxUser = "qwen",
    };

    private static async Task WriteArchiveAsync(FakeFileSystem fileSystem, ConfigArchiveManifest manifest, Dictionary<string, string> members)
    {
        using var archive = new ZipArchive(
            fileSystem.CreateFileStream(Archive, FileMode.CreateNew, FileAccess.Write, FileShare.None),
            ZipArchiveMode.Create,
            leaveOpen: false);

        await using (var stream = archive.CreateEntry("manifest.json").Open())
        {
            await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(manifest, QwenTrayJsonContext.Default.ConfigArchiveManifest));
        }

        foreach (var (name, text) in members)
        {
            await using var stream = archive.CreateEntry(name).Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
        }
    }

    private static async Task<string> ReadTextAsync(FakeFileSystem fileSystem, string path) =>
        Encoding.UTF8.GetString(await fileSystem.ReadAllBytesAsync(path));
}
