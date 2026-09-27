namespace QwenTray;

public sealed class Settings
{
    public string QwenExecutable { get; set; } = @"C:\Users\trans\AppData\Local\qwen-code\bin\qwen.cmd";
    public string WorkspaceDirectory { get; set; } = @"%USERPROFILE%\Documents\Qwen";
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
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesBearerAuth => !string.IsNullOrWhiteSpace(Token) || RequireAuth;
    [System.Text.Json.Serialization.JsonIgnore]
    public Uri HealthUri => new($"{(string.IsNullOrWhiteSpace(TlsCertPath) ? "http" : "https")}://{Host}:{Port}/health");
    [System.Text.Json.Serialization.JsonIgnore]
    public Uri WebUri => new($"{(string.IsNullOrWhiteSpace(TlsCertPath) ? "http" : "https")}://{Host}:{Port}");
}

public static class SettingsStore
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "qwentray");
    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public static string LogFilePath => Path.Combine(DirectoryPath, "qwentray.log");

    public static Settings Load()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!File.Exists(FilePath))
        {
            Save(new Settings());
        }

        return System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(FilePath), QwenTrayJsonContext.Default.Settings) ?? new Settings();
    }

    public static void Save(Settings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(settings, QwenTrayJsonContext.Default.Settings));
        if (OperatingSystem.IsWindows())
        {
            var security = new System.Security.AccessControl.FileSecurity();
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (identity is not null)
            {
                security.SetOwner(identity);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
                new System.IO.FileInfo(FilePath).SetAccessControl(security);
            }
        }
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Settings))]
public partial class QwenTrayJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
