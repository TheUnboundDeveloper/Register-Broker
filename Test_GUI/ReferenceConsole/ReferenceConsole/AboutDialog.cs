using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ReferenceConsole;

/*---------------------------------------------------------------------------*\
| AboutDialog                                                                |
|                                                                            |
|   A modal "About" card for the reference console: what the project is, the |
|   framework + GUI versions (shown separately), the open-source URL, a hard |
|   privacy disclaimer, and a pointer back to the in-app feedback button.    |
|   Built in code to match the console's generated-control style (see        |
|   ColorPickerWindow / ConfirmDialog) and avoid a XAML/code-behind pair.    |
\*---------------------------------------------------------------------------*/
public sealed class AboutDialog : Window
{
    private const string RepoUrl = "https://github.com/TheUnboundDeveloper/Register-Broker";

    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#E6E8F2"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9CA4C4"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#7C5CFF"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#34D399"));

    public AboutDialog(string frameworkVersion, string guiVersion)
    {
        Title = "About Register Broker";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1E1E2E"));

        var root = new StackPanel { Margin = new Thickness(20), Spacing = 14 };

        // ----- header: logo + name -----
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://ReferenceConsole/Assets/rc-logo.png"));
            header.Children.Add(new Image { Width = 40, Height = 40, Source = new Bitmap(s) });
        }
        catch { /* logo missing -> just the text */ }
        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock { Text = "Register Broker", FontSize = 17, FontWeight = FontWeight.Bold, Foreground = Text });
        titleStack.Children.Add(new TextBlock { Text = "Reference Console", FontSize = 12, Foreground = Muted });
        header.Children.Add(titleStack);
        root.Children.Add(header);

        // ----- what it is -----
        root.Children.Add(Para(
            "Register Broker is a universal low-level hardware-access framework. One narrow, signed " +
            "kernel driver plus an authenticated broker service let ordinary, non-admin applications read " +
            "PC sensors and drive RGB hardware through a controlled, scoped, audited interface — replacing " +
            "the old per-app elevation / WinRing0 model. Clients name logical ids and can never scan or " +
            "write outside the baked, kernel-enforced map."));
        root.Children.Add(Para(
            "This Reference Console is a first-party demonstrator client: everything it shows is served " +
            "non-admin over the broker's pipes."));

        // ----- versions (shown separately) -----
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };
        AddRow(grid, 0, "Framework version", "v" + frameworkVersion);
        AddRow(grid, 1, "Console (GUI) version", "v" + guiVersion);
        root.Children.Add(Card(grid));

        // ----- open-source URL -----
        var link = new TextBlock
        {
            Text = RepoUrl, Foreground = Accent, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            TextDecorations = TextDecorations.Underline, FontSize = 12,
        };
        link.PointerPressed += (_, _) => OpenUrl(RepoUrl);
        var linkRow = new StackPanel { Spacing = 3 };
        linkRow.Children.Add(new TextBlock { Text = "Open source (AGPL-3.0 with Commercial Exception)", Foreground = Muted, FontSize = 12 });
        linkRow.Children.Add(link);
        root.Children.Add(linkRow);

        // ----- privacy disclaimer -----
        var shield = new TextBlock { Text = "🔒", FontSize = 18, Foreground = Green, VerticalAlignment = VerticalAlignment.Top };
        var disc = new TextBlock
        {
            Text = "No data is ever mined, recorded, seen, researched, or distributed as part of this " +
                   "project. All hardware access stays local to your machine.",
            TextWrapping = TextWrapping.Wrap, Foreground = Text, FontSize = 12,
        };
        var discRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        discRow.Children.Add(shield);
        discRow.Children.Add(disc);
        DockOrWidth(disc, 360);
        root.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#16321F")),
            BorderBrush = Green, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Child = discRow,
        });

        // ----- feedback pointer -----
        root.Children.Add(Para(
            "Have additional questions or concerns? Use the ✉ Send feedback button in the main " +
            "window to reach the developer."));

        // ----- buttons -----
        var openBtn = new Button { Content = "Open on GitHub", MinWidth = 110, HorizontalContentAlignment = HorizontalAlignment.Center };
        openBtn.Click += (_, _) => OpenUrl(RepoUrl);
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 84, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(openBtn);
        buttons.Children.Add(close);
        root.Children.Add(buttons);

        Content = new ScrollViewer { Content = root, MaxHeight = 760, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    private static TextBlock Para(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#C8CCE0")), FontSize = 12,
    };

    private static Border Card(Control child) => new()
    {
        Background = new SolidColorBrush(Color.Parse("#252537")),
        BorderBrush = new SolidColorBrush(Color.Parse("#33384F")), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Child = child,
    };

    private static void AddRow(Grid grid, int row, string label, string value)
    {
        var l = new TextBlock { Text = label, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, row == 0 ? 0 : 6, 16, 0) };
        var v = new TextBlock { Text = value, Foreground = Text, FontSize = 13, FontWeight = FontWeight.SemiBold,
            FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"), Margin = new Thickness(0, row == 0 ? 0 : 6, 0, 0) };
        Grid.SetRow(l, row); Grid.SetColumn(l, 0);
        Grid.SetRow(v, row); Grid.SetColumn(v, 1);
        grid.Children.Add(l); grid.Children.Add(v);
    }

    private static void DockOrWidth(Control c, double w) { if (c is TextBlock) c.MaxWidth = w; }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser available -> ignore */ }
    }
}
