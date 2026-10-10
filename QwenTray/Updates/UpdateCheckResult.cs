namespace QwenTray.Updates;

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed,
}

/// <summary>
/// The outcome of a release check. A missing update cannot, on its own, say whether the app is
/// current or whether the check reached GitHub at all, so the status records that separately.
/// </summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update)
{
    public static UpdateCheckResult UpToDate { get; } = new(UpdateCheckStatus.UpToDate, null);

    public static UpdateCheckResult Failed { get; } = new(UpdateCheckStatus.Failed, null);

    public static UpdateCheckResult Available(UpdateInfo update) => new(UpdateCheckStatus.UpdateAvailable, update);
}
