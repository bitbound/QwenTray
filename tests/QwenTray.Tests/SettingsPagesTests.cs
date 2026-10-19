using Bitbound.SystemAbstractions.TestUtilities.FileSystem;
using Bitbound.SystemAbstractions.TestUtilities.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using QwenTray.Services;
using QwenTray.Updates;
using QwenTray.ViewModels;
using Xunit;

namespace QwenTray.Tests;

/// <summary>
/// Builds the four settings pages over one settings object and the shared save footer. The services the
/// pages only reach when a command runs are left as doubles or nulls, so these tests exercise the page
/// state and the shared save without a distro.
/// </summary>
internal sealed class SettingsPages
{
    private readonly PageProvider _provider = new();

    public SettingsPages(Settings? settings = null)
    {
        Settings = settings ?? new Settings { Profiles = [Settings.CreateDefaultProfile()], ActiveProfileId = "default" };
        Settings.ActiveProfileId = Settings.ResolveActiveProfile().Id;

        var fileSystem = new FakeFileSystem();
        var wsl = new FakeWslDistro();
        var store = new SettingsStore(fileSystem);
        var autostart = new RegistryAutostartService(new FakeRegistry());
        var provisioner = new WslDistroProvisioner(wsl, fileSystem, null!, NullLogger<WslDistroProvisioner>.Instance);
        var supervisor = new DaemonSupervisor(Settings, wsl, new SystemdUserService(wsl));

        Save = new SettingsSaveViewModel(_provider, Settings, store, supervisor, autostart, NullLogger<SettingsSaveViewModel>.Instance);
        Hold = new DistroHold();
        var distroBackup = new DistroBackupService(wsl, fileSystem, new BackupBundle(fileSystem), store, Settings, provisioner, autostart, new AppInfo("QwenTray", "bitbound/QwenTray", new Version(1, 4, 0)), NullLogger<DistroBackupService>.Instance);
        Environment = new EnvironmentViewModel(
            Settings, store, supervisor, wsl, provisioner,
            new DevToolInstaller(wsl, NullLogger<DevToolInstaller>.Instance),
            Hold, Snackbars, null!, Save, NullLogger<EnvironmentViewModel>.Instance);
        Identity = new IdentityViewModel(
            Settings, new IdentityService(wsl, NullLogger<IdentityService>.Instance),
            provisioner, Hold, Save, Snackbars, null!, NullLogger<IdentityViewModel>.Instance);
        Backup = new BackupViewModel(
            Settings, supervisor, distroBackup,
            new QwenConfigArchiveService(
                new QwenConfigArchive(fileSystem), wsl,
                new AppInfo("QwenTray", "bitbound/QwenTray", new Version(1, 4, 0)),
                NullLogger<QwenConfigArchiveService>.Instance),
            Hold, Environment, Save, Snackbars, null!, NullLogger<BackupViewModel>.Instance);
        Firewall = new FakeFirewallRules();
        Server = new ServerViewModel(Settings, supervisor, provisioner, Firewall, Hold, Identity, Save, Snackbars, NullLogger<ServerViewModel>.Instance);
        Page = new SettingsViewModel(Settings, autostart, null!, null!, supervisor, null!, Snackbars, Save, NullLogger<SettingsViewModel>.Instance);

        _provider.Environment = Environment;
        _provider.Backup = Backup;
        _provider.Identity = Identity;
        _provider.Server = Server;
        _provider.Settings = Page;
    }

    public Settings Settings { get; }

    public SettingsSaveViewModel Save { get; }

    /// <summary>The one hold the pages share, so a test can raise it the way a running backup does.</summary>
    public DistroHold Hold { get; }

    public EnvironmentViewModel Environment { get; }

    public BackupViewModel Backup { get; }

    public IdentityViewModel Identity { get; }

    public ServerViewModel Server { get; }

    /// <summary>The firewall double the Server page holds, so a test can inspect what it was asked.</summary>
    public FakeFirewallRules Firewall { get; }

    /// <summary>The Settings page, which is the tray app's own preferences.</summary>
    public SettingsViewModel Page { get; }

    public RecordingSnackbars Snackbars { get; } = new();

    /// <summary>
    /// Populates the Identity page's profile list from the settings and selects the active one, which is
    /// what its own InitializeAsync does once the distro answers.
    /// </summary>
    public void PrimeProfiles()
    {
        Identity.Profiles.Clear();
        foreach (var profile in Settings.Profiles) Identity.Profiles.Add(profile);
        Identity.SelectedProfile = Settings.ResolveActiveProfile();
    }

    /// <summary>
    /// Runs each page's own initialization, which is what copies the settings into the text fields. The
    /// shared save reads those fields, so a test that saves has to initialize first.
    /// </summary>
    public async Task InitializeAsync()
    {
        await Environment.InitializeAsync();
        await Backup.InitializeAsync();
        await Identity.InitializeAsync();
        await Server.InitializeAsync();
        await Page.InitializeAsync();
    }

    private sealed class PageProvider : IServiceProvider
    {
        public EnvironmentViewModel? Environment { get; set; }

        public BackupViewModel? Backup { get; set; }

        public IdentityViewModel? Identity { get; set; }

        public ServerViewModel? Server { get; set; }

        public SettingsViewModel? Settings { get; set; }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(EnvironmentViewModel)) return Environment;
            if (serviceType == typeof(BackupViewModel)) return Backup;
            if (serviceType == typeof(IdentityViewModel)) return Identity;
            if (serviceType == typeof(ServerViewModel)) return Server;
            if (serviceType == typeof(SettingsViewModel)) return Settings;
            return null;
        }
    }
}

/// <summary>
/// Stands in for the firewall service on the Server page, which only reaches it when a button runs.
/// Each call is recorded so a test can prove which port the page asked for.
/// </summary>
internal sealed class FakeFirewallRules : IFirewallRuleService
{
    public List<int> AllowedPorts { get; } = [];

    public int RemoveCalls { get; private set; }

    public FirewallRuleResult NextResult { get; set; } = FirewallRuleResult.Created;

    public Task<FirewallRuleResult> AllowInboundPortAsync(int port, CancellationToken cancellationToken)
    {
        AllowedPorts.Add(port);
        return Task.FromResult(NextResult);
    }

    public Task<FirewallRuleResult> RemoveAsync(CancellationToken cancellationToken)
    {
        RemoveCalls++;
        return Task.FromResult(NextResult);
    }
}

internal sealed class RecordingSnackbars : ISnackbarService
{
    public List<(string Text, SnackbarKind Kind)> Messages { get; } = [];

    public event EventHandler<SnackbarMessage>? Shown;

    public void Show(string text, SnackbarKind kind = SnackbarKind.Info)
    {
        Messages.Add((text, kind));
        Shown?.Invoke(this, new SnackbarMessage(text, kind, TimeSpan.Zero));
    }
}

public sealed class SettingsSaveTests
{
    [Fact]
    public void EveryPageHostsTheSameSaveFooter()
    {
        var pages = new SettingsPages();

        Assert.Same(pages.Save, pages.Environment.Save);
        Assert.Same(pages.Save, pages.Identity.Save);
        Assert.Same(pages.Save, pages.Server.Save);
        Assert.Same(pages.Save, pages.Page.Save);
    }

    [Fact]
    public async Task SavingReadsEveryPagesFieldsOntoTheOneSettingsObject()
    {
        var pages = new SettingsPages();
        await pages.InitializeAsync();

        pages.Environment.WslDistroName = "MyTray";
        pages.Backup.BundlePath = @"D:\bundles\qwentray-20260101-120000.zip";
        pages.Server.Host = "0.0.0.0";
        pages.Server.Port = "5199";
        pages.Server.Token = " bearer ";
        pages.Server.RequireAuth = true;
        pages.Server.AllowedOrigins = "https://a.example\r\nhttps://b.example";
        pages.Server.TlsCertPath = "/certs/a.pem";
        pages.Server.TlsKeyPath = "/certs/a.key";
        pages.Server.EnableSessionShell = true;
        pages.Server.ServeWeb = false;
        pages.Server.MaxSessions = "8";
        pages.Server.MaxConnections = "64";
        pages.Server.EventRingSize = "100";
        pages.Server.RateLimit = true;
        pages.Server.DebugMode = true;
        pages.Server.ExtraArgs = "--verbose\n--log-level debug";
        pages.Server.QwenExecutable = "/opt/qwen/bin/qwen";
        pages.Backup.ConfigPaths = "/home/qwen/.qwen/QWEN.md\n/home/qwen/.qwen/agents";
        pages.Page.HealthPollSeconds = "12";
        pages.Page.AutoRestart = false;
        pages.Page.CheckForUpdates = false;
        pages.Page.MaxBackoffSeconds = "30";

        Assert.Null(pages.Save.TryApplyToSettings());

        var settings = pages.Settings;
        Assert.Equal("MyTray", settings.WslDistroName);
        // Only the folder is kept, because the file name is generated fresh for each export.
        Assert.Equal(@"D:\bundles", settings.BundleDirectory);
        Assert.Equal("0.0.0.0", settings.Host);
        Assert.Equal(5199, settings.Port);
        Assert.Equal("bearer", settings.Token);
        Assert.True(settings.RequireAuth);
        Assert.Equal(["https://a.example", "https://b.example"], settings.AllowedOrigins);
        Assert.Equal("/certs/a.pem", settings.TlsCertPath);
        Assert.Equal("/certs/a.key", settings.TlsKeyPath);
        Assert.True(settings.EnableSessionShell);
        Assert.False(settings.ServeWeb);
        Assert.Equal(8, settings.MaxSessions);
        Assert.Equal(64, settings.MaxConnections);
        Assert.Equal(100, settings.EventRingSize);
        Assert.True(settings.RateLimit);
        Assert.True(settings.DebugMode);
        Assert.Equal(["--verbose", "--log-level debug"], settings.ExtraArgs);
        Assert.Equal(["/home/qwen/.qwen/QWEN.md", "/home/qwen/.qwen/agents"], settings.ConfigBackupPaths);
        Assert.Equal(12, settings.HealthPollSeconds);
        Assert.False(settings.AutoRestart);
        Assert.False(settings.CheckForUpdates);
        Assert.Equal(30, settings.MaxBackoffSeconds);
        // The executable path is edited on the Server page but stored on the active profile.
        Assert.Equal("/opt/qwen/bin/qwen", settings.ResolveActiveProfile().QwenExecutable);
    }

    [Fact]
    public void SavingWithoutVisitingAnyOtherPageKeepsTheStoredSettings()
    {
        // The shared save reads every page's fields, so a page the user never opened must still carry
        // what was loaded from settings, not the field defaults.
        var settings = new Settings
        {
            WslDistroName = "MyTray",
            BundleDirectory = @"E:\My Backups",
            Port = 8080,
            Token = "saved-token",
            RequireAuth = true,
            ServeWeb = true,
            LocalControl = true,
            MaxSessions = 8,
            RateLimit = true,
            ExtraArgs = ["--verbose"],
            ConfigBackupPaths = ["/home/qwen/.qwen/QWEN.md"],
            AutoRestart = true,
            CheckForUpdates = true,
            HealthPollSeconds = 12,
            MaxBackoffSeconds = 30,
            Profiles = [Settings.CreateDefaultProfile()],
            ActiveProfileId = "default",
        };
        var pages = new SettingsPages(settings);

        Assert.Null(pages.Save.TryApplyToSettings());

        Assert.Equal("MyTray", settings.WslDistroName);
        Assert.Equal(@"E:\My Backups", settings.BundleDirectory);
        Assert.Equal(8080, settings.Port);
        Assert.Equal("saved-token", settings.Token);
        Assert.True(settings.RequireAuth);
        Assert.True(settings.ServeWeb);
        Assert.True(settings.LocalControl);
        Assert.Equal(8, settings.MaxSessions);
        Assert.True(settings.RateLimit);
        Assert.Equal(["--verbose"], settings.ExtraArgs);
        Assert.Equal(["/home/qwen/.qwen/QWEN.md"], settings.ConfigBackupPaths);
        Assert.True(settings.AutoRestart);
        Assert.True(settings.CheckForUpdates);
        Assert.Equal(12, settings.HealthPollSeconds);
        Assert.Equal(30, settings.MaxBackoffSeconds);
    }

    [Fact]
    public async Task TheHealthPollIsClampedToTheRangeTheTrayUses()
    {
        var pages = new SettingsPages();
        await pages.InitializeAsync();
        pages.Page.HealthPollSeconds = "600";

        Assert.Null(pages.Save.TryApplyToSettings());

        Assert.Equal(60, pages.Settings.HealthPollSeconds);
    }

    [Theory]
    [InlineData("70000", "Port must be a number between 0 and 65535.")]
    [InlineData("nope", "Port must be a number between 0 and 65535.")]
    public async Task ABadPortStopsTheSave(string port, string expected)
    {
        var pages = new SettingsPages();
        await pages.InitializeAsync();
        pages.Server.Port = port;

        Assert.Equal(expected, pages.Save.TryApplyToSettings());
    }

    [Fact]
    public async Task AnEmptyHostStopsTheSave()
    {
        var pages = new SettingsPages();
        await pages.InitializeAsync();
        pages.Server.Host = "  ";

        Assert.Equal("Hostname cannot be empty.", pages.Save.TryApplyToSettings());
    }

    [Fact]
    public async Task DevicePairingDemandsLoopbackAndTheWebShell()
    {
        var pages = new SettingsPages();
        await pages.InitializeAsync();
        pages.Server.LocalControl = true;
        pages.Server.ServeWeb = true;
        pages.Server.Host = "0.0.0.0";
        Assert.Contains("127.0.0.1", pages.Save.TryApplyToSettings()!, StringComparison.Ordinal);

        pages.Server.Host = "127.0.0.1";
        pages.Server.ServeWeb = false;
        Assert.Contains("Web Shell", pages.Save.TryApplyToSettings()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad;name")]
    [InlineData("-u")]
    public async Task ABadDistroNameStopsTheSave(string distro)
    {
        var pages = new SettingsPages();
        await pages.InitializeAsync();
        pages.Environment.WslDistroName = distro;

        Assert.Contains("distro name", pages.Save.TryApplyToSettings(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProfileWithNoOthersLeftStopsTheSave()
    {
        var pages = new SettingsPages();
        await pages.InitializeAsync();
        pages.Identity.Profiles.Clear();

        Assert.Equal("Add at least one identity profile.", pages.Save.TryApplyToSettings());
    }

    [Fact]
    public async Task AnUnsafeProfileStopsTheSaveBeforeAnythingIsWritten()
    {
        var pages = new SettingsPages();
        await pages.InitializeAsync();
        pages.Environment.WslDistroName = "MyTray";
        pages.Identity.Profiles.Add(new IdentityProfile { Id = "bad", Name = "Bad", LinuxUser = "qwen", WorkspaceDirectory = "/home/qwen/x'; touch /pwned; '" });

        var error = pages.Save.TryApplyToSettings();

        Assert.Contains("Bad", error!, StringComparison.Ordinal);
        // The profile is validated before anything is copied, so the shared object is untouched.
        Assert.NotEqual("MyTray", pages.Settings.WslDistroName);
    }

    [Fact]
    public async Task TwoProfilesSharingALinuxUserStopTheSave()
    {
        var pages = new SettingsPages();
        await pages.InitializeAsync();
        pages.Identity.Profiles.Add(new IdentityProfile { Id = "second", Name = "Second", LinuxUser = "qwen" });

        Assert.Contains("qwen", pages.Save.TryApplyToSettings()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValidationFailureIsShownInTheSharedFooter()
    {
        var pages = new SettingsPages();
        await pages.InitializeAsync();
        pages.Server.Port = "70000";

        pages.Save.SaveCommand.Execute(null);

        Assert.Equal("Port must be a number between 0 and 65535.", pages.Save.SaveError);
        Assert.Null(pages.Save.SaveMessage);
    }
}

public sealed class IdentityPageTests
{
    [Fact]
    public void AddingAProfileDerivesAUserNameAndSelectsIt()
    {
        var pages = new SettingsPages();
        pages.PrimeProfiles();
        pages.Identity.NewProfileName = "Work Account";

        pages.Identity.AddProfileCommand.Execute(null);

        Assert.Equal(2, pages.Identity.Profiles.Count);
        var added = pages.Identity.Profiles[1];
        Assert.Equal("Work Account", added.Name);
        Assert.Equal("work_account", added.LinuxUser);
        Assert.Equal("/home/work_account/workspace", added.WorkspaceDirectory);
        Assert.Same(added, pages.Identity.SelectedProfile);
    }

    [Fact]
    public void AddingAProfileWithAUserNameAlreadyInUseIsRefused()
    {
        var pages = new SettingsPages();
        pages.PrimeProfiles();
        pages.Identity.NewProfileName = "Second";
        pages.Identity.NewProfileUser = "qwen";

        pages.Identity.AddProfileCommand.Execute(null);

        Assert.Single(pages.Identity.Profiles);
        Assert.Contains("qwen", pages.Save.SaveError!, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicatingCopiesTheDefinitionButNotTheSelectedProfilesConfiguration()
    {
        var pages = new SettingsPages();
        pages.PrimeProfiles();

        pages.Identity.DuplicateProfileCommand.Execute(null);

        Assert.Equal(2, pages.Identity.Profiles.Count);
        var copy = pages.Identity.Profiles[1];
        Assert.Equal("Default copy", copy.Name);
        Assert.Equal("default_copy", copy.LinuxUser);
        // A copy starts with no qwen path of its own, so it falls back to the conventional location.
        Assert.Equal(string.Empty, copy.QwenExecutable);
    }

    [Fact]
    public void RemovingTheLastProfileIsRefused()
    {
        var pages = new SettingsPages();
        pages.PrimeProfiles();

        pages.Identity.RemoveProfileCommand.Execute(null);

        Assert.Single(pages.Identity.Profiles);
        Assert.Equal("At least one profile is required.", pages.Save.SaveError);
    }

    [Fact]
    public void RemovingAProfileSelectsItsNeighbour()
    {
        var pages = new SettingsPages();
        var second = new IdentityProfile { Id = "second", Name = "Second", LinuxUser = "second" };
        pages.Settings.Profiles = [pages.Settings.Profiles[0], second];
        pages.PrimeProfiles();
        pages.Identity.SelectedProfile = second;

        pages.Identity.RemoveProfileCommand.Execute(null);

        Assert.Single(pages.Identity.Profiles);
        Assert.Equal("default", pages.Identity.SelectedProfile!.Id);
    }
}

public sealed class SettingsPageBusyGateTests
{
    [Fact]
    public void AHeldDistroDisablesEveryPageThatWorksInsideIt()
    {
        var pages = new SettingsPages();
        pages.PrimeProfiles();

        Assert.True(pages.Identity.CanManageIdentity);
        Assert.True(pages.Identity.CanManageGitHub);
        Assert.True(pages.Backup.CanArchiveConfig);
        Assert.True(pages.Backup.CanImportBundle);
        Assert.True(pages.Server.CanCheckQwen);
        Assert.True(pages.Environment.CanSetupDistro);
        Assert.True(pages.Environment.DevTools[0].InstallCommand.CanExecute(null));

        pages.Hold.IsHeld = true;

        Assert.False(pages.Identity.CanManageIdentity);
        Assert.False(pages.Identity.CanManageGitHub);
        Assert.False(pages.Backup.CanArchiveConfig);
        Assert.False(pages.Backup.CanImportBundle);
        Assert.False(pages.Server.CanCheckQwen);
        Assert.False(pages.Environment.CanSetupDistro);
        Assert.False(pages.Environment.CanCheckDistro);
        Assert.False(pages.Environment.DevTools[0].InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task AToolInstallHandsTheHoldBackWhenItFinishes()
    {
        // An install takes the same hold a backup does, so the two cannot overlap. A hold left raised
        // after it finished would freeze every one of those buttons for the rest of the session.
        var pages = new SettingsPages();
        pages.PrimeProfiles();

        await pages.Environment.DevTools[0].InstallCommand.ExecuteAsync(null);

        Assert.False(pages.Hold.IsHeld);
        Assert.True(pages.Environment.CanSetupDistro);
        Assert.True(pages.Backup.CanArchiveConfig);
    }

    [Fact]
    public void TheServerPageFollowsTheIdentityPagesSelection()
    {
        var pages = new SettingsPages();
        var work = new IdentityProfile { Id = "work", Name = "Work", LinuxUser = "work", QwenExecutable = "/opt/qwen/bin/qwen" };
        pages.Settings.Profiles = [pages.Settings.Profiles[0], work];
        pages.PrimeProfiles();

        Assert.Equal("/home/qwen/.local/bin/qwen", pages.Server.QwenExecutable);

        pages.Identity.SelectedProfile = work;

        Assert.Equal("/opt/qwen/bin/qwen", pages.Server.QwenExecutable);
    }
}
