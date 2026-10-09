using System.Runtime.InteropServices;

namespace QwenTray.Updates;

/// <summary>
/// Chooses the release asset built for this machine's architecture.
/// </summary>
internal static class ReleaseAssetSelector
{
    /// <summary>
    /// Stem shared by every published asset, as in "QwenTray-x64.exe".
    /// </summary>
    public const string BaseName = "QwenTray";

    /// <summary>
    /// The Windows executable extension every published asset carries.
    /// </summary>
    public const string Extension = ".exe";

    /// <summary>
    /// The asset this build expects. The release workflow publishes the same name.
    /// </summary>
    public static string AssetName { get; } = BuildAssetName(RuntimeInformation.ProcessArchitecture);

    /// <summary>
    /// Names to accept, best match first.
    /// </summary>
    /// <remarks>
    /// The bare name stays on the list as a fallback for architectures with no dedicated build, and it
    /// also covers releases published before the architecture suffix existed. That entry is what lets an
    /// installed copy survive a naming change, since the lookup runs inside the binary already on disk
    /// and cannot be corrected after the fact.
    /// </remarks>
    public static IReadOnlyList<string> CandidateNames { get; } = BuildCandidateNames(RuntimeInformation.ProcessArchitecture);

    public static string BuildAssetName(Architecture architecture) =>
        TryGetPlatformSuffix(architecture, out var suffix)
            ? $"{BaseName}-{suffix}{Extension}"
            : BaseName + Extension;

    public static IReadOnlyList<string> BuildCandidateNames(Architecture architecture)
    {
        var assetName = BuildAssetName(architecture);
        var bareName = BaseName + Extension;

        return string.Equals(assetName, bareName, StringComparison.Ordinal)
            ? [bareName]
            : [assetName, bareName];
    }

    public static GitHubReleaseAsset? Select(GitHubRelease release)
    {
        var assets = release.Assets;

        if (assets is null)
        {
            return null;
        }

        // GitHub reports "open" while an upload is still in flight. That asset is usable enough to fall
        // back to, but a finished one is what we want.
        GitHubReleaseAsset? incomplete = null;

        foreach (var candidate in CandidateNames)
        {
            foreach (var asset in assets)
            {
                // Case is ignored because the published spelling has carried a capital letter before.
                if (!string.Equals(asset.Name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(asset.State, "uploaded", StringComparison.OrdinalIgnoreCase))
                {
                    return asset;
                }

                incomplete ??= asset;
            }
        }

        return incomplete;
    }

    private static bool TryGetPlatformSuffix(Architecture architecture, out string suffix)
    {
        // The updater launches the downloaded asset directly, so the suffix carries the architecture the
        // release workflow published. Unrecognized architectures fall back to the bare name.
        suffix = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => string.Empty,
        };

        return suffix.Length > 0;
    }
}
