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

        var dialog = new ConfirmDialog(request);
        return dialog.ShowDialog<bool>(owner);
    }

    // A modal dialog needs an owner that is on screen, and Avalonia throws rather than showing one
    // without. The tray keeps the main window hidden instead of closed, so it is brought back first.
    // Every confirm is raised by a click inside that window, which is where the answer is expected.
    private static Window? ResolveOwner()
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var main = desktop?.MainWindow;

        if (main is null)
        {
            return null;
        }

        if (!main.IsVisible)
        {
            main.Show();
            main.WindowState = WindowState.Normal;
            main.Activate();
        }

        return main.IsVisible ? main : null;
    }
}
