using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ReferenceConsole;

/*---------------------------------------------------------------------------*\
| RenameDialog                                                               |
|                                                                            |
|   A small, dependency-free modal text prompt, built in code to match the   |
|   console's generated-control style (see ConfirmDialog / ColorPickerWindow).|
|   Used to give a sensor, RGB device, or dashboard card a LOCAL display      |
|   name. Resolves the typed text via ShowDialog<string?> (null = cancelled).|
\*---------------------------------------------------------------------------*/
public sealed class RenameDialog : Window
{
    private readonly TextBox _input;

    public RenameDialog(string title, string prompt, string current)
    {
        Title = title;
        Width = 400;
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
            Text = prompt, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#C8CCE0")),
        };

        _input = new TextBox { Text = current, PlaceholderText = "Display name" };
        // Enter commits, matching the default button.
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) Close(_input.Text); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 84 };
        cancel.Click += (_, _) => Close((string?)null);
        var ok = new Button { Content = "Rename", IsDefault = true, MinWidth = 84 };
        ok.Click += (_, _) => Close(_input.Text);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var root = new StackPanel { Margin = new Thickness(18), Spacing = 14 };
        root.Children.Add(heading);
        root.Children.Add(body);
        root.Children.Add(_input);
        root.Children.Add(buttons);
        Content = root;

        Opened += (_, _) => { _input.SelectAll(); _input.Focus(); };
    }

    /// <summary>
    /// Prompt for a local display name over <paramref name="owner"/>. Resolves the typed text,
    /// or null if cancelled. Clearing the box and confirming resolves to "" (caller treats an
    /// empty/blank result as "reset to the broker name").
    /// </summary>
    public static Task<string?> Show(Window owner, string title, string current)
        => new RenameDialog(title,
                "Enter a local display name. This is saved on this PC only — it never changes the broker or the hardware/driver name. Leave it blank to restore the original.",
                current)
            .ShowDialog<string?>(owner);
}
