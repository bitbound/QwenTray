using System.Reflection;
using System.Text;
using System.Text.Json;
using Bitbound.SystemAbstractions.TestUtilities.FileSystem;
using Bitbound.SystemAbstractions.TestUtilities.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using QwenTray.Services;
using QwenTray.Updates;
using Xunit;

namespace QwenTray.Tests;

public sealed class BackupBundleTests
{
    private const string VhdBytes = "the distro's filesystem, as wsl left it on disk";
    private const string SourceVhd = "/wsl/{d8dbf1b9}/ext4.vhdx";
    private const string Bundle = "/bundles/qwentray-20261010-083000";

    private static BackupManifest Manifest(string distro = "QwenTray") => new()
    {
        CreatedAtUtc = new DateTimeOffset(2026, 10, 10, 8, 30, 0, TimeSpan.Zero),
        QwenTrayVersion = "1.4.0",
        WslDistroName = distro,
        AutostartEnabled = true,
        DistroVhdBytes = Encoding.UTF8.GetByteCount(VhdBytes),
        SourceUserName = "jared",
    };

    private static FakeFileSystem FileSystemWithDisk()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(SourceVhd, Encoding.UTF8.GetBytes(VhdBytes), System.IO.FileAttributes.Normal, null);
        return fileSystem;
    }

    [Fact]
    public async Task ABundleCarriesTheManifestTheSettingsAndTheDisk()
    {
        var fileSystem = FileSystemWithDisk();
        var bundle = new BackupBundle(fileSystem);
        const string settingsJson = """{"token":"sekret"}""";

        await bundle.WriteAsync(Bundle, Manifest(), settingsJson, SourceVhd, CancellationToken.None);

        var contents = await bundle.ReadAsync(Bundle, CancellationToken.None);
        Assert.Equal("QwenTray", contents.Manifest.WslDistroName);
        Assert.True(contents.Manifest.AutostartEnabled);
        Assert.Equal(settingsJson, contents.SettingsJson);

        var copied = await bundle.CopyVhdOutAsync(Bundle, Path.Combine("/work", BackupBundle.VhdEntry), CancellationToken.None);
        Assert.Equal(BackupBundle.VhdEntry, Path.GetFileName(copied));
        Assert.Equal(VhdBytes, Encoding.UTF8.GetString(await fileSystem.ReadAllBytesAsync(copied)));
    }

    [Fact]
    public async Task AFolderAlreadyAtTheDestinationIsLeftAlone()
    {
        var fileSystem = FileSystemWithDisk();
        fileSystem.AddDirectory(Bundle);
        fileSystem.AddFile(Path.Combine(Bundle, BackupBundle.ManifestEntry), "an earlier backup");
        var bundle = new BackupBundle(fileSystem);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => bundle.WriteAsync(Bundle, Manifest(), "{}", SourceVhd, CancellationToken.None));

        Assert.Equal("an earlier backup", await fileSystem.ReadAllTextAsync(Path.Combine(Bundle, BackupBundle.ManifestEntry)));
    }

    [Fact]
    public async Task AWriteWithoutTheDiskItIsToldToCopyFails()
    {
        var bundle = new BackupBundle(FileSystemWithDisk());

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => bundle.WriteAsync(Bundle, Manifest(), "{}", "/wsl/never-copied/ext4.vhdx", CancellationToken.None));
    }

    [Fact]
    public async Task AWriteThatNeverStartsCreatesNothingAtAll()
    {
        // The bundle folder is built under a staging name and renamed into place only when its three files
        // are complete, so a run that gave up early leaves neither a bundle nor a staging folder behind.
        var fileSystem = FileSystemWithDisk();
        var bundle = new BackupBundle(fileSystem);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => bundle.WriteAsync(Bundle, Manifest(), "{}", "/wsl/never-copied/ext4.vhdx", CancellationToken.None));

        Assert.False(fileSystem.DirectoryExists(Bundle));
        Assert.False(fileSystem.DirectoryExists("/bundles"));
    }

    [Fact]
    public async Task ABundleFromANewerQwenTrayIsRefusedRatherThanReadLeniently()
    {
        var fileSystem = FileSystemWithDisk();
        var bundle = new BackupBundle(fileSystem);
        var manifest = Manifest();
        manifest.SchemaVersion = BackupManifest.CurrentSchemaVersion + 1;

        await bundle.WriteAsync("/bundles/newer", manifest, "{}", SourceVhd, CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => bundle.ReadAsync("/bundles/newer", CancellationToken.None));
        Assert.Contains("newer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABundleFromTheTarEraIsRefusedByNameRatherThanHalfRead()
    {
        // Schema one held a filesystem tar rather than the disk itself, and nothing about it can be
        // upgraded in place, so the message has to say that rather than fail on a missing file.
        var fileSystem = new FakeFileSystem();
        fileSystem.AddDirectory("/bundles/old");
        fileSystem.AddFile(Path.Combine("/bundles/old", BackupBundle.ManifestEntry), """{"schemaVersion":1,"wslDistroName":"QwenTray"}""");
        var bundle = new BackupBundle(fileSystem);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => bundle.ReadAsync("/bundles/old", CancellationToken.None));
        Assert.Contains("schema 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheDiskLeavesABundleOnItsWayToARegistration()
    {
        var fileSystem = FileSystemWithDisk();
        var bundle = new BackupBundle(fileSystem);
        await bundle.WriteAsync(Bundle, Manifest(), "{}", SourceVhd, CancellationToken.None);

        await bundle.CopyVhdOutAsync(Bundle, Path.Combine("/work", BackupBundle.VhdEntry), CancellationToken.None);

        // The manifest and the settings stay inside the bundle, so an import never leaves them lying
        // around in the folder that holds a distro filesystem.
        Assert.False(fileSystem.FileExists(Path.Combine("/work", BackupBundle.ManifestEntry)));
        Assert.False(fileSystem.FileExists(Path.Combine("/work", BackupBundle.SettingsEntry)));
        Assert.True(fileSystem.FileExists(BackupBundle.VhdPathIn("/work")));
    }

    [Fact]
    public async Task ABundleWithoutItsDiskSaysSo()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddDirectory(Bundle);
        var bundle = new BackupBundle(fileSystem);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => bundle.CopyVhdOutAsync(Bundle, Path.Combine("/work", BackupBundle.VhdEntry), CancellationToken.None));
    }

    [Fact]
    public async Task APathWithNoBundleSaysSo()
    {
        var bundle = new BackupBundle(new FakeFileSystem());

        await Assert.ThrowsAsync<FileNotFoundException>(() => bundle.ReadAsync("/bundles/absent", CancellationToken.None));
    }

    /// <summary>Bundles a disk image the way an export would, so a copy can be read back out of it.</summary>
    private static async Task<string> BundleFromDisk(FakeFileSystem fileSystem, byte[] disk)
    {
        fileSystem.AddFile(SourceVhd, disk);
        await new BackupBundle(fileSystem).WriteAsync(Bundle, Manifest(), "{}", SourceVhd, CancellationToken.None);
        return Bundle;
    }

    private static byte[] DiskWith(int leadingDataBytes, int trailingZeroBytes)
    {
        var disk = new byte[leadingDataBytes + trailingZeroBytes];
        Random.Shared.NextBytes(disk.AsSpan(0, leadingDataBytes));
        disk[^1] = 0x5a;
        return disk;
    }

    [Fact]
    public async Task EveryByteOfTheDiskArrivesAtTheDestination()
    {
        // The copy skips what it can, so this is the line it must not cross: the destination has to be the
        // same disk, byte for byte, however the bytes were reached.
        var disk = DiskWith(1024 * 1024, 2 * 1024 * 1024 + 77);
        var fileSystem = new FakeFileSystem();
        var bundleDirectory = await BundleFromDisk(fileSystem, disk);

        var copied = await new BackupBundle(fileSystem).CopyVhdOutAsync(
            bundleDirectory, Path.Combine("/target", BackupBundle.VhdEntry), CancellationToken.None);

        Assert.Equal(disk, await fileSystem.ReadAllBytesAsync(copied));
    }

    [Fact]
    public async Task ADiskEndingInZerosKeepsItsLength()
    {
        // A run of zeros at the end is skipped rather than written, and the vhdx footer is read from the end
        // of the file, so a copy that came out short would be a disk wsl cannot open.
        var disk = DiskWith(1024 * 1024, 1024 * 1024);
        var fileSystem = new FakeFileSystem();
        var bundleDirectory = await BundleFromDisk(fileSystem, disk);

        var copied = await new BackupBundle(fileSystem).CopyVhdOutAsync(
            bundleDirectory, Path.Combine("/target", BackupBundle.VhdEntry), CancellationToken.None);

        Assert.Equal(disk.LongLength, fileSystem.GetFileInfo(copied).Length);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0, 0, 1, 0 }, true)]
    [InlineData(new byte[] { 7 }, true)]
    [InlineData(new byte[] { }, false)]
    public void OnlyARegionWithNothingInItIsSkipped(byte[] chunk, bool expected)
    {
        Assert.Equal(expected, BackupBundle.ContainsData(chunk));
    }

    [Fact]
    public void TheDiskIsNamedTheWayWslNamesIt()
    {
        // The copied file is handed to --import-in-place by name, so the bundle and the live distro have to
        // spell it identically.
        Assert.Equal("ext4.vhdx", BackupBundle.VhdEntry);
        Assert.Equal(Path.Combine("/bundles/one", "ext4.vhdx"), BackupBundle.VhdPathIn("/bundles/one"));
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

public sealed class WslDistroLocationTests
{
    private const string KeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss\{d8dbf1b9}";

    private static WslDistroLocation RegistryHolding(string distro, string basePath)
    {
        var registry = new FakeRegistry();
        registry.SetValue(KeyPath, "DistributionName", distro);
        registry.SetValue(KeyPath, "BasePath", basePath);
        return new WslDistroLocation(registry);
    }

    [Fact]
    public void ADistroIsFoundByTheNameWslListsItUnder()
    {
        var location = RegistryHolding("QwenTray", @"C:\Users\jared\AppData\Local\wsl\{d8dbf1b9}");

        Assert.Equal(
            Path.Combine(@"C:\Users\jared\AppData\Local\wsl\{d8dbf1b9}", "ext4.vhdx"),
            location.TryFindVhdPath("QwenTray"));
    }

    [Fact]
    public void TheNameIsMatchedTheWayWslMatchesIt()
    {
        var location = RegistryHolding("QwenTray", @"C:\Users\jared\AppData\Local\wsl\{d8dbf1b9}");

        Assert.NotNull(location.TryFindVhdPath("qwentray"));
    }

    [Fact]
    public void ADevicePrefixIsNotPartOfTheName()
    {
        // Some entries carry the \\?\ form, which File and Path treat as part of the name rather than as a
        // prefix, so a disk that is plainly there would read as missing.
        var location = RegistryHolding("QwenTray", @"\\?\C:\Users\jared\AppData\Local\wsl\{d8dbf1b9}");

        Assert.Equal(
            Path.Combine(@"C:\Users\jared\AppData\Local\wsl\{d8dbf1b9}", "ext4.vhdx"),
            location.TryFindVhdPath("QwenTray"));
    }

    [Fact]
    public void ADistroNobodyRegisteredHasNoDisk()
    {
        var location = RegistryHolding("QwenTray", @"C:\Users\jared\AppData\Local\wsl\{d8dbf1b9}");

        Assert.Null(location.TryFindVhdPath("SomethingElse"));
        Assert.Null(location.TryFindVhdPath(""));
    }

    [Fact]
    public void ADistroRecordedWithoutAFolderHasNoDisk()
    {
        var registry = new FakeRegistry();
        registry.SetValue(KeyPath, "DistributionName", "QwenTray");

        Assert.Null(new WslDistroLocation(registry).TryFindVhdPath("QwenTray"));
    }

    [Fact]
    public void NoRegistryKeyForWslAtAllIsAnsweredAsNothingFound()
    {
        Assert.Null(new WslDistroLocation(new FakeRegistry()).TryFindVhdPath("QwenTray"));
    }
}

public sealed class DistroBackupServiceTests
{
    private const string VhdBytes = "filesystem bytes";
    private const string SourceVhd = "/wsl/{d8dbf1b9}/ext4.vhdx";
    private const string Bundle = "/bundles/qwentray-20261010-083000";

    private static FakeRegistry RegistryWithDistro(string distro)
    {
        var registry = new FakeRegistry();
        registry.SetValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss\{d8dbf1b9}", "DistributionName", distro);
        registry.SetValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss\{d8dbf1b9}", "BasePath", "/wsl/{d8dbf1b9}");
        return registry;
    }

    private static DistroBackupService Service(FakeFileSystem fileSystem, FakeWslDistro? wsl = null, FakeRegistry? registry = null, Settings? settings = null)
    {
        wsl ??= new FakeWslDistro();
        registry ??= RegistryWithDistro("QwenTray");
        var provisioner = new WslDistroProvisioner(wsl, fileSystem, new FakeSystemEnvironment(), NullLogger<WslDistroProvisioner>.Instance);

        return new(
            wsl, fileSystem, new BackupBundle(fileSystem), new WslDistroLocation(registry),
            new SettingsStore(fileSystem), settings ?? new Settings { WslDistroName = "QwenTray" }, provisioner,
            new RegistryAutostartService(registry),
            new AppInfo("QwenTray", "bitbound/QwenTray", new Version(1, 4, 0)),
            NullLogger<DistroBackupService>.Instance);
    }

    private static FakeFileSystem FileSystemHolding(BackupManifest manifest, Settings settings)
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(SourceVhd, Encoding.UTF8.GetBytes(VhdBytes));
        var bundle = new BackupBundle(fileSystem);
        bundle.WriteAsync(Bundle, manifest, JsonSerializer.Serialize(settings, QwenTrayJsonContext.Default.Settings), SourceVhd, CancellationToken.None)
            .GetAwaiter().GetResult();
        return fileSystem;
    }

    [Fact]
    public async Task AUsableBundleReportsWhatItHolds()
    {
        var settings = new Settings { WslDistroName = "QwenTray", Token = "bearer-from-the-bundle" };
        var fileSystem = FileSystemHolding(new BackupManifest { WslDistroName = "QwenTray" }, settings);

        var contents = await Service(fileSystem).ProbeAsync(Bundle, CancellationToken.None);

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

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(fileSystem).ProbeAsync(Bundle, CancellationToken.None));
    }

    [Fact]
    public async Task AProfileCarryingACommandIsRefusedBeforeItIsWrittenToTheSettingsFile()
    {
        // The values in a profile are interpolated into scripts that run as root the next time the distro
        // is set up, so a bundle cannot be allowed to smuggle one in.
        var settings = new Settings { Profiles = [new IdentityProfile { Id = "x", Name = "Smuggled", LinuxUser = "qwen", WorkspaceDirectory = "/home/qwen/x'; touch /pwned; '" }] };
        var fileSystem = FileSystemHolding(new BackupManifest { WslDistroName = "QwenTray" }, settings);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(fileSystem).ProbeAsync(Bundle, CancellationToken.None));
        Assert.Contains("Smuggled", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABundleWithoutProfilesStillImportsAsTheDefaultOne()
    {
        var fileSystem = FileSystemHolding(new BackupManifest { WslDistroName = "QwenTray" }, new Settings { Profiles = [] });

        var contents = await Service(fileSystem).ProbeAsync(Bundle, CancellationToken.None);

        Assert.Empty(JsonSerializer.Deserialize(contents.SettingsJson, QwenTrayJsonContext.Default.Settings)!.Profiles);
    }

    [Fact]
    public async Task AnExportCopiesTheDiskTheDistroBootsFromAndStopsItFirst()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(SourceVhd, Encoding.UTF8.GetBytes(VhdBytes));
        var wsl = new FakeWslDistro();

        var result = await Service(fileSystem, wsl).ExportAsync(Bundle, CancellationToken.None);

        Assert.Equal(Bundle, result.BundlePath);
        Assert.Equal(VhdBytes, Encoding.UTF8.GetString(await fileSystem.ReadAllBytesAsync(BackupBundle.VhdPathIn(Bundle))));
        Assert.Single(wsl.TerminatedDistros);
        Assert.Contains("QwenTray", wsl.TerminatedDistros);

        // The bundle has to say which distro it can be registered as, or an import onto a machine that
        // already runs a different name would register the wrong thing.
        var contents = await Service(fileSystem).ProbeAsync(Bundle, CancellationToken.None);
        Assert.Equal("QwenTray", contents.Manifest.WslDistroName);
        Assert.Equal(Encoding.UTF8.GetByteCount(VhdBytes), contents.Manifest.DistroVhdBytes);
    }

    [Fact]
    public async Task AnExportOfADistroWhoseDiskIsNotWhereWslSaysIsRefused()
    {
        // A registered distro with no disk is a state the tray can reach when a folder was cleaned by hand,
        // and an export of it would otherwise produce a bundle holding settings and nothing else.
        var fileSystem = new FakeFileSystem();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(fileSystem).ExportAsync(Bundle, CancellationToken.None));
        Assert.Contains("virtual disk", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExportOntoAnExistingBundleLeavesItAlone()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(SourceVhd, Encoding.UTF8.GetBytes(VhdBytes));
        fileSystem.AddDirectory(Bundle);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(fileSystem).ExportAsync(Bundle, CancellationToken.None));
        Assert.Empty(fileSystem.GetFiles(Bundle));
    }

    [Fact]
    public async Task AnImportRegistersTheCopyRatherThanTheBundleAndLeavesTheBundleUsable()
    {
        var fileSystem = FileSystemHolding(new BackupManifest { WslDistroName = "QwenTray", DistroVhdBytes = Encoding.UTF8.GetByteCount(VhdBytes) }, new Settings());
        var wsl = new FakeWslDistro();

        // The last thing an import does is restore whether the app starts with Windows, and the fake
        // registry answers a read-only HKCU, which that write needs to have been opened for. It is the one
        // leg a run through the fake always stops on, so it is expected here rather than skipped, and the
        // writing itself belongs to RegistryAutostartService.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(fileSystem, wsl).ImportAsync(Bundle, CancellationToken.None));

        var (distro, registeredVhd) = Assert.Single(wsl.ImportInPlaces);
        Assert.Equal("QwenTray", distro);
        Assert.EndsWith(Path.Combine("QwenTray", "ext4.vhdx"), registeredVhd, StringComparison.Ordinal);
        Assert.NotEqual(BackupBundle.VhdPathIn(Bundle), registeredVhd);

        // --import-in-place binds the distro to the file it is given, so the bundle keeps its own copy and
        // stays a backup rather than becoming a file whose deletion destroys a live distro.
        Assert.Equal(VhdBytes, Encoding.UTF8.GetString(await fileSystem.ReadAllBytesAsync(BackupBundle.VhdPathIn(Bundle))));

        // The disk arrives as a sealed QwenTray host with mirrored networking, not as whatever Ubuntu it
        // was when it was copied, and that comes from the config written on the way in.
        Assert.True(fileSystem.FileExists(Path.Combine(FakeSystemEnvironment.ProfileDirectory, ".wslconfig")));
    }

    [Fact]
    public async Task AnImportClearsItsStagingFolderWhetherOrNotItFinished()
    {
        var fileSystem = FileSystemHolding(new BackupManifest { WslDistroName = "QwenTray", DistroVhdBytes = Encoding.UTF8.GetByteCount(VhdBytes) }, new Settings());
        var wsl = new FakeWslDistro { ImportExitCode = 1 };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(fileSystem, wsl).ImportAsync(Bundle, CancellationToken.None));

        var transfer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "qwentray", "transfer");
        if (fileSystem.DirectoryExists(transfer))
        {
            Assert.Empty(fileSystem.GetDirectories(transfer));
        }
    }

    [Fact]
    public void TheDefaultBundleNameIsStampedAndSitsInTheFolderItWasGiven()
    {
        var named = DistroBackupService.DefaultBundleDirectory("/backups");
        Assert.StartsWith(Path.Combine("/backups", "qwentray-"), named, StringComparison.Ordinal);

        // The stamp is what keeps an export from landing on an earlier bundle, so a name without a complete
        // one would have nothing to fall back on when the folder already exists.
        Assert.Matches(@"^qwentray-\d{8}-\d{6}$", Path.GetFileName(named));

        Assert.StartsWith(
            Path.Combine(DistroBackupService.BackupDirectory, "qwentray-"),
            DistroBackupService.DefaultBundleDirectory(null),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SizesAreStatedInTheLargestWholeUnit()
    {
        Assert.EndsWith("KB", DistroBackupService.FormatBytes(512L), StringComparison.Ordinal);
        Assert.EndsWith("MB", DistroBackupService.FormatBytes(2L * 1024 * 1024), StringComparison.Ordinal);
        Assert.EndsWith("GB", DistroBackupService.FormatBytes(3L * 1024 * 1024 * 1024), StringComparison.Ordinal);
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
            ConfigBackupPaths = ["/home/qwen/.qwen/QWEN.md"],
            LastUpdateCheckUtc = DateTimeOffset.UnixEpoch,
            ExtraArgs = ["--verbose"],
        };
        var target = new Settings { WslDistroName = "Local", Token = "local-token", Port = 4170, ConfigBackupPaths = [], ExtraArgs = [] };

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
        var travelled = Encoding.UTF8.GetBytes(token);

        Assert.NotEqual(protectedBytes, travelled);
        Assert.Equal(token, SettingsCipher.Unprotect(protectedBytes));
        Assert.Equal(token, Encoding.UTF8.GetString(travelled));
    }
}
