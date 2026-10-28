using Bitbound.SystemAbstractions.Windows.Registry;

namespace QwenTray.Services;

/// <summary>
/// Finds the virtual disk a distro boots from. wsl.exe has no command that prints it, so the only
/// record of the mapping is the registry: every registered distro has a key naming it and the folder
/// holding its disk, and WSL always calls that disk <c>ext4.vhdx</c>.
/// </summary>
public sealed class WslDistroLocation(IRegistryManager registry)
{
    public const string VhdFileName = "ext4.vhdx";

    internal const string LxssKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Lxss";
    internal const string DistributionNameValue = "DistributionName";
    internal const string BasePathValue = "BasePath";

    /// <summary>
    /// The disk <paramref name="distro"/> boots from, or nothing when no registered distro carries that
    /// name. Nothing is created here, and a returned path can still be missing from disk.
    /// </summary>
    public string? TryFindVhdPath(string distro)
    {
        if (string.IsNullOrWhiteSpace(distro)) return null;

        using var root = registry.CurrentUser.OpenSubKey(LxssKeyPath, writable: false);
        if (root is null) return null;

        foreach (var keyName in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(keyName, writable: false);
            if (key is null) continue;
            if (!string.Equals(key.GetValue(DistributionNameValue) as string, distro, StringComparison.OrdinalIgnoreCase)) continue;
            if (key.GetValue(BasePathValue) is not string basePath || string.IsNullOrWhiteSpace(basePath)) continue;

            return Path.Combine(StripDevicePrefix(basePath.Trim()), VhdFileName);
        }

        return null;
    }

    /// <summary>
    /// Strips the <c>\\?\</c> form some entries carry, which <see cref="Path"/> and <see cref="File"/>
    /// treat as part of the name rather than as a prefix.
    /// </summary>
    internal static string StripDevicePrefix(string path)
        => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
}
