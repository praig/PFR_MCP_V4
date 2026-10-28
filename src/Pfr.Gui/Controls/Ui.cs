using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Pfr.Gui.Controls;

/// <summary>Small helpers so code-built screens look consistent.</summary>
public static class Ui
{
    public static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#F47067"));
    public static readonly IBrush Success = new SolidColorBrush(Color.Parse("#57AB5A"));
    public static readonly IBrush Surface = new SolidColorBrush(Color.FromArgb(14, 255, 255, 255));
    public static readonly IBrush Divider = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));

    /// <summary>The dark inside of a recessed panel.</summary>
    public static readonly IBrush Well = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));

    /// <summary>
    /// A recessed (sunken) panel for lists and other areas that should read as set into the page: a darker inside, an
    /// inner shadow along the top and left edges, and a bevelled edge that is dark above and light below.
    /// </summary>
    public static Border Recessed(Control child) => new()
    {
        Background = Well,
        BorderThickness = new Thickness(1),
        BorderBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(150, 0, 0, 0), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(70, 255, 255, 255), Offset = 1 },
            },
        },
        BoxShadow = BoxShadows.Parse("inset 1 2 6 0 #A0000000"),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(3),
        ClipToBounds = true,
        Child = child,
    };

    public static TextBlock Heading(string text, double size = 20) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 0, 0, 8),
    };

    public static TextBlock Muted(string text) => new()
    {
        Text = text,
        Opacity = 0.7,
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Label(string text) => new()
    {
        Text = text,
        Opacity = 0.7,
        Margin = new Thickness(0, 8, 0, 4),
    };

    public static TextBlock Message() => new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 0),
        IsVisible = false,
    };

    public static void ShowError(TextBlock target, string text)
    {
        target.Text = text;
        target.Foreground = Danger;
        target.IsVisible = true;
    }

    public static void ShowInfo(TextBlock target, string text)
    {
        target.Text = text;
        target.ClearValue(TextBlock.ForegroundProperty);
        target.Opacity = 0.8;
        target.IsVisible = true;
    }

    public static Button Accent(string text) => new() { Content = text, Classes = { "accent" } };

    public static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in children) row.Children.Add(c);
        return row;
    }

    public static TextBox Password(string watermark) => new()
    {
        PasswordChar = '•',
        Watermark = watermark,
        Width = 320,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    public static TextBox Multiline(string watermark, double height = 120) => new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Watermark = watermark,
        Height = height,
        VerticalContentAlignment = VerticalAlignment.Top,
    };

    /// <summary>
    /// A delete-style button that asks for a second click within a few seconds,
    /// instead of a modal dialog.
    /// </summary>
    public static Button Confirming(string text, Action onConfirmed, string confirmText = "Click again to confirm")
    {
        var button = new Button { Content = text };
        var armed = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            armed = false;
            button.Content = text;
            button.ClearValue(Button.ForegroundProperty);
        };
        button.Click += (_, _) =>
        {
            if (!armed)
            {
                armed = true;
                button.Content = confirmText;
                button.Foreground = Danger;
                timer.Start();
                return;
            }
            timer.Stop();
            armed = false;
            button.Content = text;
            button.ClearValue(Button.ForegroundProperty);
            onConfirmed();
        };
        return button;
    }
}
