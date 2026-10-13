using System.Reflection;
using System.Text;
using System.Text.Json;
using Bitbound.SystemAbstractions.TestUtilities.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;
using QwenTray.Services;
using QwenTray.Updates;
using Xunit;

namespace QwenTray.Tests;

public sealed class BackupBundleTests
{
    private const string TarBytes = "the distro's filesystem, as wsl wrote it";

    private static BackupManifest Manifest(string distro = "QwenTray") => new()
    {
        CreatedAtUtc = new DateTimeOffset(2026, 10, 10, 8, 30, 0, TimeSpan.Zero),
        QwenTrayVersion = "1.4.0",
        WslDistroName = distro,
        AutostartEnabled = true,
        DistroTarBytes = Encoding.UTF8.GetByteCount(TarBytes),
        SourceUserName = "jared",
    };

    private static FakeFileSystem FileSystemWithTar()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile("/export/distro.tar", Encoding.UTF8.GetBytes(TarBytes), System.IO.FileAttributes.Normal, null);
        return fileSystem;
    }

    [Fact]
    public async Task ABundleCarriesTheManifestTheSettingsAndTheFilesystem()
    {
        var fileSystem = FileSystemWithTar();
        var bundle = new BackupBundle(fileSystem);
        const string settingsJson = """{"token":"sekret"}""";

        await bundle.WriteAsync("/bundles/qwentray.zip", Manifest(), settingsJson, "/export/distro.tar", CancellationToken.None);

        var contents = await bundle.ReadAsync("/bundles/qwentray.zip", CancellationToken.None);
        Assert.Equal("QwenTray", contents.Manifest.WslDistroName);
        Assert.True(contents.Manifest.AutostartEnabled);
        Assert.Equal(settingsJson, contents.SettingsJson);

        var tar = await bundle.ExtractTarAsync("/bundles/qwentray.zip", "/work", CancellationToken.None);
        Assert.Equal(Path.Combine("/work", "distro.tar"), tar);
        Assert.Equal(TarBytes, Encoding.UTF8.GetString(await fileSystem.ReadAllBytesAsync(tar)));
    }

    [Fact]
    public async Task AFileAlreadyAtTheDestinationIsLeftAlone()
    {
        var fileSystem = FileSystemWithTar();
        var existing = Encoding.UTF8.GetBytes("an earlier backup");
        fileSystem.AddFile("/bundles/qwentray.zip", existing, System.IO.FileAttributes.Normal, null);
        var bundle = new BackupBundle(fileSystem);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => bundle.WriteAsync("/bundles/qwentray.zip", Manifest(), "{}", "/export/distro.tar", CancellationToken.None));

        Assert.Equal(existing, await fileSystem.ReadAllBytesAsync("/bundles/qwentray.zip"));
    }

    [Fact]
    public async Task AWriteWithoutTheFilesystemItIsToldToPackFails()
    {
        var bundle = new BackupBundle(FileSystemWithTar());

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => bundle.WriteAsync("/bundles/qwentray.zip", Manifest(), "{}", "/export/never-written.tar", CancellationToken.None));
    }

    [Fact]
    public async Task ABundleFromANewerQwenTrayIsRefusedRatherThanReadLeniently()
    {
        var fileSystem = FileSystemWithTar();
        var bundle = new BackupBundle(fileSystem);
        var manifest = Manifest();
        manifest.SchemaVersion = BackupManifest.CurrentSchemaVersion + 1;

        await bundle.WriteAsync("/bundles/newer.zip", manifest, "{}", "/export/distro.tar", CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => bundle.ReadAsync("/bundles/newer.zip", CancellationToken.None));
        Assert.Contains("newer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheFilesystemMemberIsWrittenOutOfAnArchive()
    {
        var fileSystem = FileSystemWithTar();
        var bundle = new BackupBundle(fileSystem);
        await bundle.WriteAsync("/bundles/qwentray.zip", Manifest(), "{}", "/export/distro.tar", CancellationToken.None);

        await bundle.ExtractTarAsync("/bundles/qwentray.zip", "/work", CancellationToken.None);

        // The manifest and the settings stay inside the archive, so an import never leaves them lying
        // around in the directory that holds a distro filesystem.
        Assert.False(fileSystem.FileExists("/work/manifest.json"));
        Assert.False(fileSystem.FileExists("/work/settings.json"));
        Assert.True(fileSystem.FileExists("/work/distro.tar"));
    }

    [Fact]
    public async Task APathWithNoBundleSaysSo()
    {
        var bundle = new BackupBundle(new FakeFileSystem());

        await Assert.ThrowsAsync<FileNotFoundException>(() => bundle.ReadAsync("/bundles/absent.zip", CancellationToken.None));
    }

    [Fact]
    public void TheManifestTravelsThroughTheSourceGeneratedContext()
    {
        var json = JsonSerializer.Serialize(Manifest(), QwenTrayJsonContext.Default.BackupManifest);

        // The context names every member camelCase, which is the contract across machines.
        Assert.Contains("wslDistroName", json, StringComparison.Ordinal);
        Assert.Equal(Manifest(), JsonSerializer.Deserialize(json, QwenTrayJsonContext.Default.BackupManifest));
    }
}

public sealed class DistroBackupServiceTests
{
    private const string TarBytes = "filesystem bytes";

    private static DistroBackupService Service(FakeFileSystem fileSystem, Settings? settings = null)
        => new(new FakeWslDistro(), fileSystem, new BackupBundle(fileSystem), null!, settings ?? new Settings(), null!, null!,
            new AppInfo("QwenTray", "bitbound/QwenTray", new Version(1, 4, 0)), NullLogger<DistroBackupService>.Instance);

    private static FakeFileSystem FileSystemHolding(BackupManifest manifest, Settings settings)
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile("/export/distro.tar", Encoding.UTF8.GetBytes(TarBytes), System.IO.FileAttributes.Normal, null);
        var bundle = new BackupBundle(fileSystem);
        bundle.WriteAsync("/bundles/qwentray.zip", manifest, JsonSerializer.Serialize(settings, QwenTrayJsonContext.Default.Settings), "/export/distro.tar", CancellationToken.None)
            .GetAwaiter().GetResult();
        return fileSystem;
    }

    [Fact]
    public async Task AUsableBundleReportsWhatItHolds()
    {
        var settings = new Settings { WslDistroName = "QwenTray", Token = "bearer-from-the-bundle" };
        var fileSystem = FileSystemHolding(new BackupManifest { WslDistroName = "QwenTray" }, settings);

        var contents = await Service(fileSystem).ProbeAsync("/bundles/qwentray.zip", CancellationToken.None);

        Assert.Equal("QwenTray", contents.Manifest.WslDistroName);
        Assert.Contains("bearer-from-the-bundle", contents.SettingsJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-u")]
    [InlineData("--terminate")]
    [InlineData("Distro & notepad.exe")]
    [InlineData("bad;name")]
    [InlineData(".hidden")]
    [InlineData("")]
    public async Task ABundleNamingADistroThatWouldBeReadAsAnOptionIsRefused(string distro)
    {
        var fileSystem = FileSystemHolding(new BackupManifest { WslDistroName = distro }, new Settings());

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(fileSystem).ProbeAsync("/bundles/qwentray.zip", CancellationToken.None));
    }

    [Fact]
    public async Task AProfileCarryingACommandIsRefusedBeforeItIsWrittenToTheSettingsFile()
    {
        // The values in a profile are interpolated into scripts that run as root the next time the distro
        // is set up, so a bundle cannot be allowed to smuggle one in.
        var settings = new Settings { Profiles = [new IdentityProfile { Id = "x", Name = "Smuggled", LinuxUser = "qwen", WorkspaceDirectory = "/home/qwen/x'; touch /pwned; '" }] };
        var fileSystem = FileSystemHolding(new BackupManifest { WslDistroName = "QwenTray" }, settings);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(fileSystem).ProbeAsync("/bundles/qwentray.zip", CancellationToken.None));
        Assert.Contains("Smuggled", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABundleWithoutProfilesStillImportsAsTheDefaultOne()
    {
        var fileSystem = FileSystemHolding(new BackupManifest { WslDistroName = "QwenTray" }, new Settings { Profiles = [] });

        var contents = await Service(fileSystem).ProbeAsync("/bundles/qwentray.zip", CancellationToken.None);

        Assert.Empty(JsonSerializer.Deserialize(contents.SettingsJson, QwenTrayJsonContext.Default.Settings)!.Profiles);
    }

    [Fact]
    public void TheMeasurementAsksTheDistroForBytesUsedOnItsOwnRootFilesystem()
    {
        var script = DistroBackupService.BuildMeasureScript();

        Assert.Contains("df -B1 --output=used /", script, StringComparison.Ordinal);
        Assert.Contains("tail -1", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(512L, "KB")]
    [InlineData(2L * 1024 * 1024, "MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "GB")]
    public void SizesAreStatedInTheLargestWholeUnit(long bytes, string expectedUnit)
    {
        Assert.EndsWith(expectedUnit, DistroBackupService.FormatBytes(bytes), StringComparison.Ordinal);
    }
}

public sealed class ImportedSettingsTests
{
    [Fact]
    public void CopyFromMovesEveryWritableSetting()
    {
        var source = new Settings
        {
            WslDistroName = "Bundled",
            ActiveProfileId = "work",
            Profiles = [new IdentityProfile { Id = "work", Name = "Work", LinuxUser = "work" }],
            Token = "from-the-bundle",
            Port = 5199,
            SyncPaths = ["QWEN.md"],
            LastUpdateCheckUtc = DateTimeOffset.UnixEpoch,
            ExtraArgs = ["--verbose"],
        };
        var target = new Settings { WslDistroName = "Local", Token = "local-token", Port = 4170, SyncPaths = [], ExtraArgs = [] };

        target.CopyFrom(source);

        // Walked rather than listed, so a setting added later cannot be silently left behind by an import.
        foreach (var property in typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || !property.CanRead) continue;
            Assert.Equal(property.GetValue(source), property.GetValue(target));
        }
    }

    [WindowsFact]
    public void TheBearerTokenIsProtectedOnThisMachineAndNotCarriedAsABlob()
    {
        // A DPAPI blob only opens for the account that wrote it, which is the whole reason a bundle holds
        // the settings as plaintext and the arrival re-protects them.
        const string token = "bearer-travelling-between-machines";

        var protectedBytes = SettingsCipher.Protect(token);
        var travelled = System.Text.Encoding.UTF8.GetBytes(token);

        Assert.NotEqual(protectedBytes, travelled);
        Assert.Equal(token, SettingsCipher.Unprotect(protectedBytes));
        Assert.Equal(token, System.Text.Encoding.UTF8.GetString(travelled));
    }
}
