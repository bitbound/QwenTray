using Bitbound.SystemAbstractions.FileSystem;

namespace QwenTray;

/// <summary>
/// One Linux identity inside the QwenTray distro. Its home directory carries the ssh key, the gh
/// credentials, the git config, and the Qwen Code configuration, so selecting a profile and
/// restarting the daemon is the whole switch.
/// </summary>
public sealed class IdentityProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string LinuxUser { get; set; } = "";
    public string WorkspaceDirectory { get; set; } = "";

    /// <summary>Absolute Linux path to this user's qwen launcher. Empty means the conventional location.</summary>
    public string QwenExecutable { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string HomeDirectory => $"/home/{LinuxUser}";

    /// <summary>Each Linux user installs qwen under its own home, so the launcher path is per profile.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ResolvedQwenExecutable => string.IsNullOrWhiteSpace(QwenExecutable) ? $"{HomeDirectory}/.local/bin/qwen" : QwenExecutable;

    [System.Text.Json.Serialization.JsonIgnore]
    public string ResolvedWorkspaceDirectory => string.IsNullOrWhiteSpace(WorkspaceDirectory) ? $"{HomeDirectory}/workspace" : WorkspaceDirectory;
}

public sealed class Settings
{
    public string WslDistroName { get; set; } = "QwenTray";
    public string ActiveProfileId { get; set; } = "";
    public IdentityProfile[] Profiles { get; set; } = [];

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4170;
    public string[] ExtraArgs { get; set; } = [];
    public int HealthPollSeconds { get; set; } = 5;
    public bool AutoRestart { get; set; } = true;
    public int MaxBackoffSeconds { get; set; } = 60;
    public string Token { get; set; } = "";
    public bool RequireAuth { get; set; }
    public bool EnableSessionShell { get; set; }
    public bool ServeWeb { get; set; } = true;
    public bool LocalControl { get; set; }
    public string[] AllowedOrigins { get; set; } = [];
    public string TlsCertPath { get; set; } = "";
    public string TlsKeyPath { get; set; } = "";
    public int MaxSessions { get; set; } = 32;
    public int MaxConnections { get; set; } = 256;
    public int EventRingSize { get; set; } = 8000;
    public bool RateLimit { get; set; }
    public bool DebugMode { get; set; }

    /// <summary>Configuration entries synced between Windows and the active profile's <c>~/.qwen</c>.</summary>
    public string[] SyncPaths { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public static string[] DefaultSyncPaths => ["QWEN.md", "agents", "rules", "memories", "skills", "workflows"];

    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesBearerAuth => !string.IsNullOrWhiteSpace(Token) || RequireAuth;
    [System.Text.Json.Serialization.JsonIgnore]
    private string Scheme => string.IsNullOrWhiteSpace(TlsCertPath) ? "http" : "https";

    /// <summary>Bind addresses (0.0.0.0, ::) are never connectable; map them to loopback. QwenTray is co-located with the daemon.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ClientHost => Host.Trim() is "0.0.0.0" or "::" or "[::]" ? "127.0.0.1" : Host.Trim();

    [System.Text.Json.Serialization.JsonIgnore]
    public Uri LoopbackUri => new($"{Scheme}://127.0.0.1:{Port}");

    [System.Text.Json.Serialization.JsonIgnore]
    public Uri HealthUri => new(LoopbackUri, "/health");

    [System.Text.Json.Serialization.JsonIgnore]
    public Uri WebUri => new($"{Scheme}://{ClientHost}:{Port}");

    /// <summary>The profile selected by <see cref="ActiveProfileId"/>, the first one, or the default.</summary>
    public IdentityProfile ResolveActiveProfile()
    {
        var selected = Profiles.FirstOrDefault(profile => string.Equals(profile.Id, ActiveProfileId, StringComparison.Ordinal));
        return selected ?? Profiles.FirstOrDefault() ?? CreateDefaultProfile();
    }

    public static IdentityProfile CreateDefaultProfile() => new()
    {
        Id = "default",
        Name = "Default",
        LinuxUser = "qwen",
        WorkspaceDirectory = "/home/qwen/workspace",
    };
}

public sealed class SettingsStore(IFileSystem fileSystem)
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "qwentray");
    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public static string LogFilePath => Path.Combine(DirectoryPath, "qwentray.log");

    public Settings Load()
    {
        if (!fileSystem.DirectoryExists(DirectoryPath))
        {
            fileSystem.CreateDirectory(DirectoryPath);
        }

        if (!fileSystem.FileExists(FilePath))
        {
            var seeded = new Settings
            {
                Profiles = [Settings.CreateDefaultProfile()],
            };
            seeded.ActiveProfileId = seeded.Profiles[0].Id;
            seeded.SyncPaths = Settings.DefaultSyncPaths;
            Save(seeded);
            return seeded;
        }

        return System.Text.Json.JsonSerializer.Deserialize(fileSystem.ReadAllText(FilePath), QwenTrayJsonContext.Default.Settings) ?? new Settings();
    }

    public void Save(Settings settings)
    {
        if (!fileSystem.DirectoryExists(DirectoryPath))
        {
            fileSystem.CreateDirectory(DirectoryPath);
        }

        fileSystem.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(settings, QwenTrayJsonContext.Default.Settings));
        RestrictToCurrentUser();
    }

    // The file-system abstraction has no ACL member, so this single write stays on the BCL. Losing it
    // would leave settings.json readable by other accounts on the machine.
    private static void RestrictToCurrentUser()
    {
        if (!OperatingSystem.IsWindows()) return;

        var security = new System.Security.AccessControl.FileSecurity();
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User;
        if (identity is null) return;

        security.SetOwner(identity);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        new System.IO.FileInfo(FilePath).SetAccessControl(security);
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Settings))]
public partial class QwenTrayJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
