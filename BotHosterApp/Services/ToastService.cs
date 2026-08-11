using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MediaColor = System.Windows.Media.Color;

namespace BotHosterApp.Services;

/// <summary>
/// Custom notification toasts: small dark cards that appear in the top-right
/// corner of the screen (below the very top edge, above the taskbar), stack
/// when several arrive at once, auto-dismiss after a few seconds and close on
/// click. No Windows Action-Center toasts are used.
/// </summary>
public static class ToastService
{
    private static readonly List<ToastWindow> Open = new();
    private const double Width = 340;
    private const double Margin = 16;
    private const double Gap = 8;
    private const int HistoryMax = 30;

    /// <summary>Recent notifications shown in the Account tab (newest first).</summary>
    public static readonly System.Collections.ObjectModel.ObservableCollection<ToastEntry> History = new();

    /// <summary>Call from the UI thread.</summary>
    public static void Show(string title, string message, bool isError = false)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            History.Insert(0, new ToastEntry(title, message, isError, DateTime.Now));
            while (History.Count > HistoryMax) History.RemoveAt(History.Count - 1);

            var toast = new ToastWindow(title, message, isError);
            Position(toast);
            Open.Add(toast);
            toast.Closed += (_, _) => Open.Remove(toast);
            toast.Show();
        });
    }

    private static void Position(ToastWindow toast)
    {
        var area = SystemParameters.WorkArea;
        var x = area.Right - Width - Margin;
        var y = area.Top + 12; // a bit down from the very top edge
        foreach (var o in Open)
            y = Math.Max(y, o.Top + o.Height + Gap);
        y = Math.Min(y, area.Bottom - toast.Height - Margin);
        toast.Left = x;
        toast.Top = y;
    }
}

public sealed record ToastEntry(string Title, string Message, bool IsError, DateTime Time)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public Brush AccentBrush => new SolidColorBrush(IsError
        ? MediaColor.FromRgb(0xE8, 0x11, 0x23)
        : MediaColor.FromRgb(0x58, 0x65, 0xF2));
}

internal sealed class ToastWindow : Window
{
    private readonly DispatcherTimer _timer;

    public ToastWindow(string title, string message, bool isError)
    {
        Width = 340;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;

        var accent = new SolidColorBrush(MediaColor.FromRgb(
            (byte)(isError ? 0xE8 : 0x58), (byte)(isError ? 0x11 : 0x65), (byte)(isError ? 0x23 : 0xF2)));

        var border = new Border
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(0x11, 0x11, 0x13)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x27, 0x27, 0x2A)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 10, 12),
            Margin = new Thickness(6),
            Child = new Grid(),
        };
        var grid = (Grid)border.Child;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

        // accent strip on the left
        var strip = new Border
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = accent,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 6, 0, 6),
        };
        grid.Children.Add(strip);

        // icon: logo image or glyph
        var iconBox = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(MediaColor.FromRgb(0x18, 0x18, 0x1B)),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var logo = App.TryLoadLogo();
        if (logo != null)
        {
            iconBox.Child = new Image { Source = logo, Width = 22, Height = 22, Stretch = Stretch.Uniform };
        }
        else
        {
            iconBox.Child = new TextBlock
            {
                Text = isError ? "!" : "✓",
                FontWeight = FontWeights.Bold,
                Foreground = accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        Grid.SetColumn(iconBox, 0);
        grid.Children.Add(iconBox);

        var textStack = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        textStack.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 11,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0xA1, 0xA1, 0xAA)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
            MaxHeight = 60,
        });
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        var close = new Button
        {
            Content = new TextBlock { Text = "×", FontSize = 14, Foreground = new SolidColorBrush(MediaColor.FromRgb(0x71, 0x71, 0x7A)) },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Width = 22,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);

        Content = border;
        MouseLeftButtonUp += (_, _) => Close();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _timer.Tick += (_, _) => Close();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }
}
