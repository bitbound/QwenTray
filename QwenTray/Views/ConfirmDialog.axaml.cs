using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using QwenTray.Services;

namespace QwenTray.Views;

/// <summary>
/// A yes/no prompt for an action that cannot be undone. The caller owns the consequence wording;
/// this only asks and reports the answer.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public ConfirmDialog(ConfirmDialogRequest request)
        : this()
    {
        DataContext = request;

        if (!request.Destructive)
        {
            return;
        }

        this.FindControl<PathIcon>("Glyph")?.Classes.Add("danger");
        var confirm = this.FindControl<Button>("ConfirmButton");
        if (confirm is not null)
        {
            confirm.Classes.Remove("Primary");
            confirm.Classes.Add("danger");
        }

        // Focus starts on Cancel so an accidental Enter cannot confirm.
        this.FindControl<Button>("CancelButton")?.Focus();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
