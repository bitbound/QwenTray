using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using QwenTray.Views;

namespace QwenTray.Services;

/// <param name="Title">Headline naming the action.</param>
/// <param name="Message">What happens, in terms of what the user loses or gains.</param>
/// <param name="ConfirmLabel">Verb on the affirmative button, phrased as the action itself.</param>
/// <param name="Detail">Secondary line for the blast radius.</param>
/// <param name="Destructive">Whether the affirmative button is drawn as a danger action.</param>
public sealed record ConfirmDialogRequest(
    string Title,
    string Message,
    string ConfirmLabel,
    string? Detail = null,
    bool Destructive = true);

public interface IConfirmDialogService
{
    /// <summary>
    /// Shows the prompt modally and returns whether the user confirmed. A dialog that cannot be
    /// owned by a window is treated as a refusal, never as consent.
    /// </summary>
    Task<bool> ConfirmAsync(ConfirmDialogRequest request);
}

public sealed class ConfirmDialogService : IConfirmDialogService
{
    public Task<bool> ConfirmAsync(ConfirmDialogRequest request)
    {
        var owner = ResolveOwner();
        if (owner is null)
        {
            return Task.FromResult(false);
        }

        return owner.ShowDialog<bool>(new ConfirmDialog(request));
    }

    // The window hides rather than closes when it goes to the tray, so the main window is the owner
    // whether or not it is on screen.
    private static Window? ResolveOwner()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
