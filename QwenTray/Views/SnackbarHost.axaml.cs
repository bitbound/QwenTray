using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using QwenTray.Services;

namespace QwenTray.Views;

/// <summary>
/// Renders snackbar messages as a stack of fading cards near the bottom of the window. It subscribes
/// on attach, so a message raised while the window is hidden to the tray is dropped.
/// </summary>
public partial class SnackbarHost : UserControl
{
    /// <summary>
    /// Longer stacks than this become a wall of text, so the oldest is evicted.
    /// </summary>
    private const int MaxVisible = 3;

    private ISnackbarService? _snackbars;

    public SnackbarHost()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // The container is built before the shell window exists, but the XAML previewer loads this
    // control with no container at all, so the service is taken from whatever is present.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _snackbars = App.Services?.GetService<ISnackbarService>();
        if (_snackbars is not null)
        {
            _snackbars.Shown += OnShown;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_snackbars is not null)
        {
            _snackbars.Shown -= OnShown;
            _snackbars = null;
        }
    }

    private void OnShown(object? sender, SnackbarMessage message)
    {
        Dispatcher.UIThread.Post(() => Add(message));
    }

    private void Add(SnackbarMessage message)
    {
        var stack = this.FindControl<StackPanel>("SnackStack");

        if (stack is null)
        {
            return;
        }

        var card = new Border { Classes = { "snack" } };

        switch (message.Kind)
        {
            case SnackbarKind.Success:
                card.Classes.Add("success");
                break;
            case SnackbarKind.Warning:
                card.Classes.Add("warning");
                break;
            case SnackbarKind.Error:
                card.Classes.Add("error");
                break;
            default:
                card.Classes.Add("info");
                break;
        }

        card.Child = new TextBlock
        {
            Text = message.Text,
            Classes = { "snackText" },
        };

        stack.Children.Add(card);

        while (stack.Children.Count > MaxVisible)
        {
            stack.Children.RemoveAt(0);
        }

        // Fade in on the next frame, so the transition has a starting value to animate from.
        Dispatcher.UIThread.Post(() => card.Opacity = 1, DispatcherPriority.Loaded);

        var timer = new DispatcherTimer { Interval = message.Duration };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Remove(stack, card);
        };

        timer.Start();
    }

    private static void Remove(StackPanel stack, Border card)
    {
        card.Opacity = 0;

        // Give the fade time to run before the element leaves the tree.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            stack.Children.Remove(card);
        };

        timer.Start();
    }
}
