using System.Text.RegularExpressions;
using Bitbound.SystemAbstractions.FileSystem;

namespace QwenTray;

/// <summary>
/// One Linux identity inside the QwenTray distro. Its home directory carries the ssh key, the gh
/// credentials, the git config, and the Qwen Code configuration, so selecting a profile and
/// restarting the daemon is the whole switch.
/// </summary>
public sealed class IdentityProfile
{
    // These values are interpolated into shell scripts that run as root, so they are restricted to a
    // charset that cannot carry shell metacharacters.
    private static readonly Regex LinuxUserNamePattern = new("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.Compiled);
    private static readonly Regex SafeAbsolutePathPattern = new("^/[A-Za-z0-9_./-]*$", RegexOptions.Compiled);
    private static readonly Regex GpgEmailPattern = new(@"\A[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}\z", RegexOptions.Compiled);

    // The name is placed in front of the email inside double quotes, so it may not carry a quote, a
    // backslash, a dollar sign, or a backtick, and it may not carry angle brackets because those are
    // how gpg delimits the email half of a user id.
    private static readonly Regex GpgNamePattern = new(@"\A[A-Za-z0-9 ,'_.-]{1,64}\z", RegexOptions.Compiled);

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string LinuxUser { get; set; } = "";
    public string WorkspaceDirectory { get; set; } = "";

    /// <summary>Absolute Linux path to this user's qwen launcher. Empty means the conventional location.</summary>
    public string QwenExecutable { get; set; } = "";

    /// <summary>The email on this profile's signing key. Empty means <see cref="ResolvedEmail"/>'s default.</summary>
    public string Email { get; set; } = "";

    /// <summary>The display name on this profile's signing key. Empty means <see cref="ResolvedGpgName"/>'s default.</summary>
    public string GpgName { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string HomeDirectory => $"/home/{LinuxUser}";

    /// <summary>Each Linux user installs qwen under its own home, so the launcher path is per profile.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ResolvedQwenExecutable => string.IsNullOrWhiteSpace(QwenExecutable) ? $"{HomeDirectory}/.local/bin/qwen" : QwenExecutable;

    [System.Text.Json.Serialization.JsonIgnore]
    public string ResolvedWorkspaceDirectory => string.IsNullOrWhiteSpace(WorkspaceDirectory) ? $"{HomeDirectory}/workspace" : WorkspaceDirectory;

    /// <summary>The email on the signing key, defaulted from the Linux user when unset.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ResolvedEmail => string.IsNullOrWhiteSpace(Email) ? $"{LinuxUser}@qwentray.local" : Email.Trim();

    /// <summary>The name on the signing key, defaulted to the product and the Linux user when unset.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ResolvedGpgName => string.IsNullOrWhiteSpace(GpgName) ? $"QwenTray {LinuxUser}" : GpgName.Trim();

    /// <summary>Whether every value is safe to place inside a root shell script.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsShellSafe => IsValidLinuxUser(LinuxUser)
        && IsSafeAbsolutePath(ResolvedQwenExecutable)
        && IsSafeAbsolutePath(ResolvedWorkspaceDirectory)
        && IsValidGpgEmail(ResolvedEmail)
        && IsValidGpgName(ResolvedGpgName);

    internal static bool IsValidLinuxUser(string? user) => user is not null && LinuxUserNamePattern.IsMatch(user);

    /// <summary>Derives a suggestion for a Linux user name from a free-form profile name.</summary>
    internal static string SlugifyLinuxUser(string value)
    {
        var lowered = value.Trim().ToLowerInvariant();
        var cleaned = new string([.. lowered.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_')]);
        if (cleaned.Length == 0 || (!char.IsAsciiLetter(cleaned[0]) && cleaned[0] != '_')) cleaned = "user" + cleaned;
        return cleaned.Length > 32 ? cleaned[..32] : cleaned;
    }

    internal static bool IsSafeAbsolutePath(string? path) => path is not null && SafeAbsolutePathPattern.IsMatch(path);

    internal static bool IsValidGpgEmail(string? email) => email is not null && GpgEmailPattern.IsMatch(email);

    internal static bool IsValidGpgName(string? name) => name is not null && GpgNamePattern.IsMatch(name);
}

public sealed class Settings
{
    public string WslDistroName { get; set; } = "QwenTray";

    /// <summary>
    /// The folder a bundle is written to. Empty means the app's own backups folder. The file name is
    /// generated inside it, because an export refuses to overwrite an earlier bundle.
    /// </summary>
    public string BundleDirectory { get; set; } = "";

    /// <summary>
    /// The folder a configuration backup is written to. Empty means the app's own backups folder. Only
    /// the folder is kept, because the file name carries a timestamp generated at export.
    /// </summary>
    public string ConfigArchiveDirectory { get; set; } = "";

    public string ActiveProfileId { get; set; } = "";
    public IdentityProfile[] Profiles { get; set; } = [];

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4170;
    public string[] ExtraArgs { get; set; } = [];
    public int HealthPollSeconds { get; set; } = 5;
    public bool AutoRestart { get; set; } = true;
    public int MaxBackoffSeconds { get; set; } = 60;
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>When the automatic update check last ran, in UTC. Null means it has never run.</summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
    public string Token { get; set; } = "";
    public bool RequireAuth { get; set; }
    public bool EnableSessionShell { get; set; }
    public bool ServeWeb { get; set; } = true;
    public bool LocalControl { get; set; }

    /// <summary>
    /// Exposes the daemon to the network through a socket-activated forwarder inside the distro, which is
    /// what lets the daemon keep binding loopback. Off by default, because it opens the daemon to the
    /// network.
    /// </summary>
    public bool LanProxy { get; set; }

    /// <summary>
    /// The port the forwarder listens on. It has to differ from <see cref="Port"/>, because the daemon
    /// keeps its own port and the forwarder binds a second one alongside it.
    /// </summary>
    public int LanProxyPort { get; set; } = 4171;

    public string[] AllowedOrigins { get; set; } = [];
    public string TlsCertPath { get; set; } = "";
    public string TlsKeyPath { get; set; } = "";
    public int MaxSessions { get; set; } = 32;
    public int MaxConnections { get; set; } = 256;
    public int EventRingSize { get; set; } = 8000;
    public bool RateLimit { get; set; }
    public bool DebugMode { get; set; }

    /// <summary>Absolute Linux paths inside the distro that a configuration backup includes.</summary>
    public string[] ConfigBackupPaths { get; set; } = [];

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

    /// <summary>
    /// The Web Shell address with the bearer token in the URL fragment, which is how the page reads
    /// one. A fragment is never sent to the server, and the daemon builds its own pairing links the
    /// same way. Falls back to the bare address when no token is configured.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Uri WebShellUri
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Token))
            {
                return WebUri;
            }

            var builder = new UriBuilder(WebUri)
            {
                // Escaped the way the daemon escapes it, because the page decodes the fragment.
                Fragment = "token=" + Uri.EscapeDataString(Token.Trim()),
            };
            return builder.Uri;
        }
    }

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

    private static readonly Regex DistroNamePattern = new("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.Compiled);

    /// <summary>
    /// Whether a name can be handed to wsl.exe as a distro name. Arguments are quoted only where they
    /// carry whitespace, so a name holding an ampersand or starting with a dash would arrive as a new
    /// option rather than a value.
    /// </summary>
    internal static bool IsSafeDistroName(string? name) => name is not null && DistroNamePattern.IsMatch(name);

    /// <summary>
    /// Replaces every value on this instance with the ones from <paramref name="other"/>. The container
    /// hands out one Settings that the supervisor and the Settings page already hold, so an import writes
    /// through the existing instance instead of swapping it. A new setting belongs here too.
    /// </summary>
    public void CopyFrom(Settings other)
    {
        WslDistroName = other.WslDistroName;
        ActiveProfileId = other.ActiveProfileId;
        Profiles = other.Profiles;
        Host = other.Host;
        Port = other.Port;
        ExtraArgs = other.ExtraArgs;
        HealthPollSeconds = other.HealthPollSeconds;
        AutoRestart = other.AutoRestart;
        MaxBackoffSeconds = other.MaxBackoffSeconds;
        CheckForUpdates = other.CheckForUpdates;
        LastUpdateCheckUtc = other.LastUpdateCheckUtc;
        Token = other.Token;
        RequireAuth = other.RequireAuth;
        EnableSessionShell = other.EnableSessionShell;
        ServeWeb = other.ServeWeb;
        LocalControl = other.LocalControl;
        LanProxy = other.LanProxy;
        LanProxyPort = other.LanProxyPort;
        AllowedOrigins = other.AllowedOrigins;
        TlsCertPath = other.TlsCertPath;
        TlsKeyPath = other.TlsKeyPath;
        MaxSessions = other.MaxSessions;
        MaxConnections = other.MaxConnections;
        EventRingSize = other.EventRingSize;
        RateLimit = other.RateLimit;
        DebugMode = other.DebugMode;
        ConfigBackupPaths = other.ConfigBackupPaths;
    }
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
            Save(seeded);
            return seeded;
        }

        var bytes = ReadAllBytes();

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(SettingsCipher.Unprotect(bytes), QwenTrayJsonContext.Default.Settings) ?? new Settings();
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Not a DPAPI blob, so this file was written before encryption. Read it as plaintext and
            // rewrite it protected.
            var legacy = System.Text.Json.JsonSerializer.Deserialize(System.Text.Encoding.UTF8.GetString(bytes), QwenTrayJsonContext.Default.Settings) ?? new Settings();
            TryMigrate(legacy);
            return legacy;
        }
    }

    public void Save(Settings settings)
    {
        if (!fileSystem.DirectoryExists(DirectoryPath))
        {
            fileSystem.CreateDirectory(DirectoryPath);
        }

        // settings.json carries the bearer token, so it is DPAPI-protected rather than left as plaintext.
        WriteAllBytes(SettingsCipher.Protect(System.Text.Json.JsonSerializer.Serialize(settings, QwenTrayJsonContext.Default.Settings)));
        RestrictToCurrentUser();
    }

    private byte[] ReadAllBytes()
    {
        using var stream = fileSystem.OpenFileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private void WriteAllBytes(byte[] bytes)
    {
        using var stream = fileSystem.CreateFileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes, 0, bytes.Length);
    }

    // A failed rewrite must not keep the app from starting, and must leave the file the user still has.
    private void TryMigrate(Settings settings)
    {
        try
        {
            Save(settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
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

/// <summary>
/// Protects the settings file with DPAPI. The scope is the current Windows user, so the file only
/// opens for the same account that wrote it.
/// </summary>
internal static class SettingsCipher
{
    private static readonly byte[] Entropy = System.Text.Encoding.UTF8.GetBytes("QwenTray.Settings.v1");

    public static byte[] Protect(string plaintext) => System.Security.Cryptography.ProtectedData.Protect(
        System.Text.Encoding.UTF8.GetBytes(plaintext),
        Entropy,
        System.Security.Cryptography.DataProtectionScope.CurrentUser);

    public static string Unprotect(byte[] ciphertext) => System.Text.Encoding.UTF8.GetString(
        System.Security.Cryptography.ProtectedData.Unprotect(ciphertext, Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser));
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Settings))]
[System.Text.Json.Serialization.JsonSerializable(typeof(QwenTray.Services.BackupManifest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(QwenTray.Services.ConfigArchiveManifest))]
public partial class QwenTrayJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
