namespace QwenTray.Updates;

/// <summary>
/// Checks a release source for a newer build and applies it.
/// </summary>
public interface IUpdateService
{
    UpdateInfo? AvailableUpdate { get; }

    event EventHandler<UpdateInfo>? UpdateAvailable;

    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the update to a staging folder and hands off to it, which replaces this executable.
    /// </summary>
    Task ApplyAsync(CancellationToken cancellationToken = default);
}
