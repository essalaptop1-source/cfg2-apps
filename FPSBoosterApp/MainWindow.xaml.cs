using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using FPSBoosterApp.Services;

namespace FPSBoosterApp;

public partial class MainWindow : Window
{
    private sealed class TweakRow
    {
        public required CheckBox Check { get; init; }
        public required Ellipse Dot { get; init; }
        public required TextBlock StateText { get; init; }
        public FrameworkElement? Lock { get; set; }
    }

    private readonly Dictionary<TweakId, TweakRow> _rows = new();
    private readonly List<Path> _featureLocks = new();
    private bool _busy;

    // ---- live system stats ----
    private readonly DispatcherTimer _statsTimer;
    private long _lastIdle, _lastKernel, _lastUser;
    private ulong _totalRam;
    private int _tick;
    private Process? _monitoredGame;
    private DateTime _lastGameSample;
    private TimeSpan _lastGameCpu;
    private bool _refreshingGames;

    public MainWindow()
    {
        InitializeComponent();
        BuildTweakList();
        BuildPremiumFeatures();
        LicenseService.RefreshStatus();
        UpdateTabs();
        RefreshPremiumUi();
        RefreshAll("Ready");

        // Report this launch (device / IP / HWID / premium status) - never blocks.
        _ = Task.Run(TelemetryService.ReportLaunchAsync);

        InitStats();
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += StatsTimer_Tick;
        _statsTimer.Start();
        RefreshGamesList();

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
            AdminStatusText.Text = "Not elevated - some tweaks need admin";
        }

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

    // ================================================================ Tabs

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            var premium = tag == "premium";
            PanelBoost.Visibility = premium ? Visibility.Collapsed : Visibility.Visible;
            PanelPremium.Visibility = premium ? Visibility.Visible : Visibility.Collapsed;
            UpdateTabs();
        }
    }

    private void UpdateTabs()
    {
        var premium = PanelPremium.Visibility == Visibility.Visible;
        StyleTab(TabBoost, !premium);
        StyleTab(TabPremium, premium);
    }

    private void StyleTab(Button b, bool active)
    {
        b.Background = active ? (Brush)FindResource("AccentGradientBrush") : (Brush)FindResource("SurfaceAltBrush");
        b.Foreground = active ? (Brush)FindResource("OnAccentBrush") : (Brush)FindResource("TextSecondaryBrush");
        b.BorderBrush = active ? (Brush)FindResource("AccentBorderBrush") : (Brush)FindResource("BorderBrush");
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
                IsEnabled = !tweak.IsPremium, // premium rows unlock after activation
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
            if (tweak.IsPremium)
            {
                titlePanel.Children.Add(new Border
                {
                    Background = (Brush)FindResource("AccentGradientBrush"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(8, 0, 0, 0),
                    Child = new TextBlock
                    {
                        Text = "PREMIUM",
                        FontSize = 8.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = (Brush)FindResource("OnAccentBrush"),
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

            var row = new TweakRow { Check = check, Dot = dot, StateText = stateText };
            if (tweak.IsPremium)
            {
                var lockIcon = new Path
                {
                    Style = (Style)FindResource("StrokeIcon"),
                    Data = (Geometry)FindResource("IconShield"),
                    Width = 12,
                    Height = 12,
                    Stroke = (Brush)FindResource("TextTertiaryBrush"),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                statePanel.Children.Insert(0, lockIcon);
                row.Lock = lockIcon;
            }

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
            _rows[tweak.Id] = row;
        }
    }

    // ================================================================ Premium

    private void BuildPremiumFeatures()
    {
        PremiumFeaturesPanel.Children.Clear();
        _featureLocks.Clear();
        foreach (var tweak in BoostService.Tweaks.Where(t => t.IsPremium))
        {
            var lockIcon = new Path
            {
                Style = (Style)FindResource("StrokeIcon"),
                Data = (Geometry)FindResource("IconShield"),
                Width = 14,
                Height = 14,
                Stroke = (Brush)FindResource("TextTertiaryBrush"),
                VerticalAlignment = VerticalAlignment.Top,
            };

            var textPanel = new StackPanel();
            textPanel.Children.Add(new TextBlock
            {
                Text = tweak.Title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextBrush"),
            });
            textPanel.Children.Add(new TextBlock
            {
                Text = tweak.Description,
                FontSize = 11,
                Foreground = (Brush)FindResource("TextTertiaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(textPanel, 1);
            grid.Children.Add(lockIcon);
            grid.Children.Add(textPanel);

            PremiumFeaturesPanel.Children.Add(new Border
            {
                Background = (Brush)FindResource("SurfaceBrush"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = grid,
            });
            _featureLocks.Add(lockIcon);
        }
    }

    private void RefreshPremiumUi()
    {
        var active = LicenseService.IsPremiumActive;

        PremiumBadge.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PremiumPillText.Text = active ? "ACTIVE" : "LOCKED";
        PremiumPill.Background = active
            ? new SolidColorBrush(Color.FromRgb(87, 242, 135))
            : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        PremiumPillText.Foreground = active ? new SolidColorBrush(Color.FromRgb(6, 18, 11)) : Brushes.White;
        PremiumSubtitle.Text = active
            ? "Premium is active. The extra tweaks are unlocked in the BOOST tab."
            : "Unlock the advanced tweaks with a license key.";

        PremiumDot.Fill = active
            ? (Brush)FindResource("OnlineBrush")
            : (Brush)FindResource("TextDisabledBrush");
        PremiumSideText.Text = active ? "Premium: active" : "Premium: locked";
        PremiumSideText.Foreground = active
            ? (Brush)FindResource("OnlineBrush")
            : (Brush)FindResource("TextTertiaryBrush");

        foreach (var tweak in BoostService.Tweaks.Where(t => t.IsPremium))
        {
            if (_rows.TryGetValue(tweak.Id, out var row))
            {
                row.Check.IsEnabled = active;
                if (row.Lock != null)
                    row.Lock.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        foreach (var lockIcon in _featureLocks)
        {
            lockIcon.Stroke = active
                ? (Brush)FindResource("AccentBrush")
                : (Brush)FindResource("TextTertiaryBrush");
        }
    }

    private void KeyBox_Changed(object sender, TextChangedEventArgs e) =>
        ActivateButton.IsEnabled = Regex.IsMatch(
            KeyBox.Text.Trim().ToUpperInvariant(),
            @"^[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$");

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(key)) return;
        ActivateButton.IsEnabled = false;
        KeyStatusText.Text = "Activating…";
        KeyStatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        var (ok, message) = await Task.Run(() => LicenseService.TryActivateAsync(key));
        KeyStatusText.Text = message;
        KeyStatusText.Foreground = ok
            ? (Brush)FindResource("OnlineBrush")
            : (Brush)FindResource("DangerBrush");
        if (ok)
        {
            RefreshPremiumUi();
            RefreshAll(message);
        }
        ActivateButton.IsEnabled = true;
    }

    private void PremiumGame_Changed(object sender, TextChangedEventArgs e) =>
        BoostService.GameProcessName = PremiumGameBox.Text.Trim();

    // ================================================================ Running games

    private sealed class GameEntry
    {
        public GameEntry(Process p)
        {
            Process = p;
            Label = $"{p.ProcessName}  -  {p.MainWindowTitle}";
        }
        public Process Process { get; }
        public string Label { get; }
        public override string ToString() => Label;
    }

    private void RefreshGames_Click(object sender, RoutedEventArgs e) => RefreshGamesList();

    private void RefreshGamesList()
    {
        _refreshingGames = true;
        var previous = ActiveGamesCombo.SelectedItem as GameEntry;
        ActiveGamesCombo.Items.Clear();
        var me = Environment.ProcessId;
        foreach (var p in Process.GetProcesses()
                     .Where(p => !string.IsNullOrWhiteSpace(p.MainWindowTitle) && p.Id != me)
                     .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            ActiveGamesCombo.Items.Add(new GameEntry(p));
        }
        if (previous != null)
        {
            ActiveGamesCombo.SelectedItem = ActiveGamesCombo.Items
                .OfType<GameEntry>()
                .FirstOrDefault(g => g.Process.Id == previous.Process.Id);
        }
        _refreshingGames = false;
    }

    private void ActiveGames_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingGames || ActiveGamesCombo.SelectedItem is not GameEntry entry) return;

        var name = entry.Process.ProcessName;
        BoostService.GameProcessName = name;
        PremiumGameBox.Text = name;

        try
        {
            var path = entry.Process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path))
            {
                BoostService.GameExePath = path;
                GameExeBox.Text = path;
            }
        }
        catch
        {
            // process may have exited - path stays as typed
        }

        _monitoredGame = entry.Process;
        _lastGameSample = DateTime.UtcNow;
        _lastGameCpu = TimeSpan.Zero;
        GameStatsText.Text = $"Monitoring {name}";

        // the overlay follows the picked game too
        _overlay?.Attach(entry.Process);
    }

    private OverlayWindow? _overlay;

    private void Overlay_Click(object sender, RoutedEventArgs e)
    {
        if (_overlay is { IsVisible: true })
        {
            _overlay.Close();
            _overlay = null;
            return;
        }

        _overlay?.Close(); // it may have been closed via its x button
        _overlay = new OverlayWindow();
        var picked = ActiveGamesCombo.SelectedItem as GameEntry;
        _overlay.Attach(picked?.Process);
        _overlay.Show();
    }

    // ================================================================ System stats

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    private void InitStats()
    {
        var mem = new MemoryStatusEx();
        if (GlobalMemoryStatusEx(mem))
            _totalRam = mem.TotalPhys;
        GetSystemTimes(out var idle, out var kernel, out var user);
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;
    }

    private void StatsTimer_Tick(object? sender, EventArgs e)
    {
        _tick++;
        SampleCpu();
        SampleRam();
        SampleGame();
        if (_tick % 10 == 0) RefreshGamesList(); // keep the running-games list fresh
    }

    private void SampleCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return;
        var total1 = _lastKernel + _lastUser;
        var total2 = kernel + user;
        var dTotal = total2 - total1;
        if (dTotal <= 0) return;
        var pct = 100.0 * (1.0 - (double)(idle - _lastIdle) / dTotal);
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;
        CpuText.Text = $"{Math.Clamp(pct, 0, 100):F0}%";
    }

    private void SampleRam()
    {
        var mem = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(mem) || mem.TotalPhys == 0) return;
        var used = mem.TotalPhys - mem.AvailPhys;
        RamText.Text = $"{used / 1073741824.0:F1} / {mem.TotalPhys / 1073741824.0:F0} GB";
    }

    private void SampleGame()
    {
        if (_monitoredGame == null) return;
        try
        {
            _monitoredGame.Refresh();
            if (_monitoredGame.HasExited)
            {
                _monitoredGame = null;
                GameStatsText.Text = "No game selected";
                return;
            }
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastGameSample).TotalSeconds;
            if (elapsed > 0 && _lastGameCpu > TimeSpan.Zero)
            {
                var cpu = 100.0 * (_monitoredGame.TotalProcessorTime - _lastGameCpu).TotalSeconds
                          / elapsed / Environment.ProcessorCount;
                var memMb = _monitoredGame.WorkingSet64 / 1048576.0;
                GameStatsText.Text =
                    $"{_monitoredGame.ProcessName}: CPU {Math.Clamp(cpu, 0, 100):F0}%  ·  {memMb:F0} MB";
            }
            _lastGameSample = now;
            _lastGameCpu = _monitoredGame.TotalProcessorTime;
        }
        catch
        {
            _monitoredGame = null;
            GameStatsText.Text = "No game selected";
        }
    }

    // ================================================================ Actions

    private async void Boost_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        BoostButton.IsEnabled = false;
        SetStatus("Applying tweaks…", busy: true);

        var selected = _rows
            .Where(r => r.Value.Check.IsChecked == true)
            .Select(r => r.Key)
            .ToList();
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
        StatusDot.Fill = error
            ? (Brush)FindResource("DangerBrush")
            : busy ? (Brush)FindResource("TextTertiaryBrush") : (Brush)FindResource("AccentBrush");
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

    protected override void OnClosed(EventArgs e)
    {
        _overlay?.Close();
        base.OnClosed(e);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
