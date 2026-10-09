using System.Reflection;

namespace QwenTray.Updates;

/// <summary>
/// Identifies this build to the updater: the repository whose releases are checked, the product name
/// GitHub's User-Agent asks for, and the running version.
/// </summary>
public sealed record AppInfo(string ProductName, string RepositorySlug, Version Version)
{
    public static AppInfo Current()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (UpdateVersionParser.TryParse(informational, out var version))
        {
            return new AppInfo("QwenTray", "bitbound/QwenTray", version);
        }

        return new AppInfo("QwenTray", "bitbound/QwenTray", assembly.GetName().Version ?? new Version(0, 0));
    }
}
