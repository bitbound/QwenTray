namespace QwenTray.Updates;

/// <summary>
/// Decides when the automatic update check is due.
/// </summary>
internal static class UpdateCheckSchedule
{
    /// <summary>Automatic checks run no more often than this.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(2);

    /// <summary>True when the check has never run or the interval has elapsed since it last did.</summary>
    public static bool IsDue(DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc) =>
        lastCheckUtc is null || nowUtc - lastCheckUtc.Value >= Interval;
}
