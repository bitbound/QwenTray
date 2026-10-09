using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Bitbound.SystemAbstractions.FileSystem;
using Bitbound.SystemAbstractions.Processes;
using Microsoft.Extensions.Logging;

namespace QwenTray.Updates;

/// <summary>
/// Checks GitHub Releases for a newer build and hands the download off to replace this executable.
/// </summary>
public sealed class GitHubReleaseUpdateService(
    HttpClient httpClient,
    AppInfo appInfo,
    IFileSystem fileSystem,
    IProcessManager processes,
    ILogger<GitHubReleaseUpdateService> logger) : IUpdateService
{
    private UpdateInfo? _availableUpdate;
    private bool _updateAvailableRaised;

    public UpdateInfo? AvailableUpdate => _availableUpdate;

    public event EventHandler<UpdateInfo>? UpdateAvailable;

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        GitHubRelease? release;

        try
        {
            release = await FetchLatestReleaseAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            // A private repository answers 404 without a token, which surfaces here as an HttpRequestException.
            logger.LogWarning(exception, "Could not check for a QwenTray update.");
            return null;
        }

        if (release is null)
        {
            return null;
        }

        var tag = release.TagName;

        if (!UpdateVersionParser.TryParse(tag, out var releaseVersion))
        {
            logger.LogWarning("Ignoring release with unrecognized version tag \"{Tag}\".", tag);
            return null;
        }

        var currentVersion = UpdateVersionParser.NormalizeCurrent(appInfo.Version);

        if (releaseVersion <= currentVersion)
        {
            logger.LogDebug("QwenTray {Version} is already the latest release.", currentVersion);
            return null;
        }

        var asset = ReleaseAssetSelector.Select(release);

        if (asset?.Name is not { Length: > 0 } assetName
            || asset.DownloadUrl is not { Length: > 0 } downloadUrl)
        {
            logger.LogWarning(
                "Release {Tag} has no {AssetName} asset, so this update cannot be applied.",
                tag,
                ReleaseAssetSelector.AssetName);
            return null;
        }

        var update = new UpdateInfo(tag!.Trim(), assetName, downloadUrl, asset.Digest);
        _availableUpdate = update;

        if (!_updateAvailableRaised)
        {
            _updateAvailableRaised = true;
            UpdateAvailable?.Invoke(this, update);
        }

        logger.LogInformation("Update available: QwenTray {Tag}.", update.Version);

        return update;
    }

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        var update = _availableUpdate ?? await CheckAsync(cancellationToken);

        if (update is null)
        {
            throw new InvalidOperationException("No update is available to apply.");
        }

        var stagingDirectory = UpdateStagingDirectory;
        fileSystem.CreateDirectory(stagingDirectory);

        var downloadPath = Path.Combine(stagingDirectory, update.AssetName);

        logger.LogInformation("Downloading QwenTray {Version} to {Path}.", update.Version, downloadPath);

        await using (var target = fileSystem.CreateFileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var download = await httpClient.GetStreamAsync(update.DownloadUrl, cancellationToken))
        {
            await download.CopyToAsync(target, cancellationToken);
        }

        await VerifyDownloadAsync(downloadPath, update, cancellationToken);

        var currentExecutable = Environment.ProcessPath;

        if (string.IsNullOrEmpty(currentExecutable))
        {
            throw new InvalidOperationException("Could not determine the path of the running executable.");
        }

        var handoff = new UpdateHandoff(currentExecutable, Environment.ProcessId);
        var startInfo = new ProcessStartInfo(downloadPath)
        {
            UseShellExecute = false,
        };

        foreach (var argument in UpdateHandoff.BuildArguments(handoff))
        {
            startInfo.ArgumentList.Add(argument);
        }

        logger.LogInformation("Handing off to {Path} to replace {Current}.", downloadPath, currentExecutable);

        processes.Start(startInfo);
    }

    /// <summary>Staging folder for downloads, reused and overwritten for every update.</summary>
    private static string UpdateStagingDirectory => Path.Combine(SettingsStore.DirectoryPath, "update");

    private async Task VerifyDownloadAsync(string downloadPath, UpdateInfo update, CancellationToken cancellationToken)
    {
        if (!UpdateDigest.TryParse(update.Digest, out var expected))
        {
            logger.LogWarning(
                "Release {Version} carries no SHA-256 digest, so the download is trusted as served.",
                update.Version);
            return;
        }

        if (await UpdateDigest.MatchesAsync(downloadPath, expected, fileSystem, cancellationToken))
        {
            return;
        }

        fileSystem.DeleteFile(downloadPath);

        throw new InvalidOperationException($"The downloaded {update.AssetName} did not match its published SHA-256 digest.");
    }

    private async Task<GitHubRelease?> FetchLatestReleaseAsync(CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{appInfo.RepositorySlug}/releases/latest";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // GitHub rejects requests with no User-Agent; the media type pins the current representation.
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("User-Agent", $"{appInfo.ProductName}/{appInfo.Version}");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);

        return await JsonSerializer.DeserializeAsync(content, UpdateSerializerContext.Default.GitHubRelease, cancellationToken);
    }
}
