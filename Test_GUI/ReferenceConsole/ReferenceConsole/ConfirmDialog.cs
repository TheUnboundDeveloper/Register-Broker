using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ReferenceConsole;

/*---------------------------------------------------------------------------*\
| ConfirmDialog                                                              |
|                                                                            |
|   A small, dependency-free modal yes/no confirmation. Built entirely in    |
|   code to match the console's generated-control style (see                 |
|   ColorPickerWindow) and avoid pulling in a message-box package. Returns    |
|   the user's choice via ShowDialog<bool> (true = confirmed).               |
\*---------------------------------------------------------------------------*/
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmText, string cancelText)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1E1E2E"));

        var heading = new TextBlock
        {
            Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
        };
        var body = new TextBlock
        {
            Text = message, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#C8CCE0")),
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        // An empty cancelText makes this an info-only dialog (single OK button).
        if (!string.IsNullOrEmpty(cancelText))
        {
            var cancel = new Button { Content = cancelText, IsCancel = true, MinWidth = 84 };
            cancel.Click += (_, _) => Close(false);
            buttons.Children.Add(cancel);
        }
        var confirm = new Button { Content = confirmText, IsDefault = true, MinWidth = 84 };
        confirm.Click += (_, _) => Close(true);
        buttons.Children.Add(confirm);

        var root = new StackPanel { Margin = new Thickness(18), Spacing = 14 };
        root.Children.Add(heading);
        root.Children.Add(body);
        root.Children.Add(buttons);
        Content = root;
    }

    /// <summary>Show the dialog modally over <paramref name="owner"/>; resolves true when confirmed.</summary>
    public static Task<bool> Show(Window owner, string title, string message,
        string confirmText = "Confirm", string cancelText = "Cancel")
        => new ConfirmDialog(title, message, confirmText, cancelText).ShowDialog<bool>(owner);

    /// <summary>Show an info-only acknowledgement (single OK button).</summary>
    public static Task<bool> Info(Window owner, string title, string message, string okText = "OK")
        => new ConfirmDialog(title, message, okText, "").ShowDialog<bool>(owner);
}
