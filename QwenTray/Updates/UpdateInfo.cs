namespace QwenTray.Updates;

/// <summary>
/// A newer release that can be applied: the tag it was published under and the asset to download.
/// </summary>
public sealed record UpdateInfo(string Version, string AssetName, string DownloadUrl, string? Digest);
