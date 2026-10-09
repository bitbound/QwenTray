namespace QwenTray.Services;

public enum SnackbarKind
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record SnackbarMessage(string Text, SnackbarKind Kind, TimeSpan Duration);

/// <summary>
/// Reports the outcome of a button press in the floating notification area.
/// </summary>
public interface ISnackbarService
{
    void Show(string text, SnackbarKind kind = SnackbarKind.Info);

    event EventHandler<SnackbarMessage>? Shown;
}

/// <summary>
/// Broadcasts messages to whatever hosts them. A message raised before a host exists is dropped
/// rather than replayed, so nothing is queued here.
/// </summary>
public sealed class SnackbarService : ISnackbarService
{
    private static readonly TimeSpan InfoDuration = TimeSpan.FromSeconds(3.5);
    private static readonly TimeSpan WarningDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorDuration = TimeSpan.FromSeconds(6);

    public event EventHandler<SnackbarMessage>? Shown;

    public void Show(string text, SnackbarKind kind = SnackbarKind.Info)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var duration = kind switch
        {
            SnackbarKind.Error => ErrorDuration,
            SnackbarKind.Warning => WarningDuration,
            _ => InfoDuration,
        };

        Shown?.Invoke(this, new SnackbarMessage(text, kind, duration));
    }
}
