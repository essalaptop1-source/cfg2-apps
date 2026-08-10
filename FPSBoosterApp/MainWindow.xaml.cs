using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using FPSBoosterApp.Services;

namespace FPSBoosterApp;

public partial class MainWindow : Window
{
    private sealed class TweakRow
    {
        public required CheckBox Check { get; init; }
        public required Ellipse Dot { get; init; }
        public required TextBlock StateText { get; init; }
    }

    private readonly Dictionary<TweakId, TweakRow> _rows = new();
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        BuildTweakList();

        var isAdmin = BoostService.IsAdmin();
        if (isAdmin)
        {
            AdminBadge.Visibility = Visibility.Visible;
            AdminButton.Visibility = Visibility.Collapsed;
            AdminHint.Visibility = Visibility.Collapsed;
            AdminStatusText.Text = "Running as administrator";
        }
        else
        {
            AdminButton.Visibility = Visibility.Visible;
            AdminHint.Visibility = Visibility.Visible;
            AdminStatusText.Text = "Not elevated - power plan & Nagle need admin";
        }

        RefreshAll("Ready");

        // Modern entrance: fade the window in once it's shown.
        Opacity = 0;
        Loaded += (_, _) =>
        {
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            BeginAnimation(UIElement.OpacityProperty, fade);
        };
    }

    // ================================================================ Tweak list

    private void BuildTweakList()
    {
        TweakList.Children.Clear();
        foreach (var tweak in BoostService.Tweaks)
        {
            var check = new CheckBox
            {
                IsChecked = tweak.Recommended,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 10, 0),
                ToolTip = tweak.NeedsAdmin ? "Needs administrator rights" : null,
            };

            var titlePanel = new StackPanel { Orientation = Orientation.Horizontal };
            titlePanel.Children.Add(new TextBlock
            {
                Text = tweak.Title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextBrush"),
            });
            if (tweak.NeedsAdmin)
            {
                titlePanel.Children.Add(new Border
                {
                    Background = (Brush)FindResource("SurfaceAltBrush"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(8, 0, 0, 0),
                    Child = new TextBlock
                    {
                        Text = "ADMIN",
                        FontSize = 8.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = (Brush)FindResource("TextTertiaryBrush"),
                    },
                });
            }

            var textPanel = new StackPanel();
            textPanel.Children.Add(titlePanel);
            textPanel.Children.Add(new TextBlock
            {
                Text = tweak.Description,
                FontSize = 11,
                Foreground = (Brush)FindResource("TextTertiaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });

            var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0) };
            var stateText = new TextBlock { FontSize = 10.5, Foreground = (Brush)FindResource("TextTertiaryBrush") };
            var statePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8, 4, 0, 0),
            };
            statePanel.Children.Add(dot);
            statePanel.Children.Add(stateText);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(textPanel, 1);
            Grid.SetColumn(statePanel, 2);
            grid.Children.Add(check);
            grid.Children.Add(textPanel);
            grid.Children.Add(statePanel);

            var card = new Border
            {
                Background = (Brush)FindResource("SurfaceBrush"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = grid,
            };
            TweakList.Children.Add(card);
            _rows[tweak.Id] = new TweakRow { Check = check, Dot = dot, StateText = stateText };
        }
    }

    // ================================================================ Actions

    private async void Boost_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        BoostButton.IsEnabled = false;
        SetStatus("Applying tweaks…", busy: true);

        var selected = _rows.Where(r => r.Value.Check.IsChecked == true).Select(r => r.Key).ToList();
        var ok = 0;
        var failed = new List<string>();
        foreach (var id in selected)
        {
            var (success, message) = await Task.Run(() => BoostService.Apply(id));
            if (success) ok++;
            else failed.Add($"{BoostService.Tweaks.First(t => t.Id == id).Title}: {message}");
        }

        if (failed.Count == 0)
            SetStatus(ok == 0 ? "Nothing to apply" : $"Applied {ok} of {selected.Count} tweaks");
        else
            SetStatus($"Applied {ok} of {selected.Count} - {failed.Count} failed", error: true);

        RefreshAll(StatusText.Text);
        _busy = false;
        BoostButton.IsEnabled = true;
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        RestoreButton.IsEnabled = false;
        SetStatus("Restoring…", busy: true);

        var applied = BoostService.Tweaks.Where(t => BoostService.IsApplied(t.Id)).Select(t => t.Id).ToList();
        var ok = 0;
        var failed = new List<string>();
        foreach (var id in applied)
        {
            var (success, message) = await Task.Run(() => BoostService.Restore(id));
            if (success) ok++;
            else failed.Add($"{BoostService.Tweaks.First(t => t.Id == id).Title}: {message}");
        }

        if (failed.Count == 0)
            SetStatus(ok == 0 ? "Nothing to restore" : $"Restored {ok} of {applied.Count} tweaks");
        else
            SetStatus($"Restored {ok} of {applied.Count} - {failed.Count} failed", error: true);

        RefreshAll(StatusText.Text);
        _busy = false;
        RestoreButton.IsEnabled = true;
    }

    private void GameExe_Changed(object sender, TextChangedEventArgs e) =>
        BoostService.GameExePath = GameExeBox.Text.Trim();

    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe) ?? "",
            };
            Process.Start(psi);
            Close();
        }
        catch (Exception ex)
        {
            SetStatus("Elevation was cancelled or failed", error: true);
            StatusText.Text = ex.Message;
        }
    }

    // ================================================================ Status

    private void RefreshAll(string message)
    {
        var applied = 0;
        foreach (var tweak in BoostService.Tweaks)
        {
            var isApplied = BoostService.IsApplied(tweak.Id);
            if (isApplied) applied++;
            if (_rows.TryGetValue(tweak.Id, out var row))
            {
                row.Dot.Fill = isApplied
                    ? (Brush)FindResource("OnlineBrush")
                    : (Brush)FindResource("TextDisabledBrush");
                row.StateText.Text = isApplied ? "Applied" : "Not applied";
                row.StateText.Foreground = isApplied
                    ? (Brush)FindResource("TextSecondaryBrush")
                    : (Brush)FindResource("TextTertiaryBrush");
            }
        }
        AppliedCountText.Text = $"{applied} of {BoostService.Tweaks.Count} applied";
        StatusText.Text = message;
        SetStatus(message);
    }

    private void SetStatus(string text, bool busy = false, bool error = false)
    {
        StatusBarText.Text = text;
        StatusDot.Fill = error ? (Brush)FindResource("DangerBrush") : (busy ? (Brush)FindResource("TextTertiaryBrush") : (Brush)FindResource("AccentBrush"));
        StatusDot.Width = busy ? 8 : 6;
        StatusDot.Height = busy ? 8 : 6;
    }

    // ================================================================ Window chrome

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
