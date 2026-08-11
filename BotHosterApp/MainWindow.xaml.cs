using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BotHosterApp.Services;
using Discord;
using MediaColor = System.Windows.Media.Color;

namespace BotHosterApp;

public partial class MainWindow : Window
{
    private readonly BotManager _manager = new();
    private readonly Dictionary<BotEntry, List<(string Text, LogSeverity Severity)>> _logs = new();
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private BotEntry? _selected;

    private static readonly Brush InfoBrush = new SolidColorBrush(MediaColor.FromRgb(0xD4, 0xD4, 0xD8));
    private static readonly Brush WarnBrush = new SolidColorBrush(MediaColor.FromRgb(0xFE, 0xBB, 0x3D));
    private static readonly Brush ErrorBrush = new SolidColorBrush(MediaColor.FromRgb(0xF8, 0x71, 0x71));
    private static readonly Brush DimBrush = new SolidColorBrush(MediaColor.FromRgb(0x71, 0x71, 0x7A));

    private sealed class GuildEntry
    {
        public required ulong Id { get; init; }
        public required string Name { get; init; }
        public required int Members { get; init; }
    }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => InitAsync();
        Closed += (_, _) =>
        {
            _ticker.Stop();
            _ = _manager.StopAllAsync();
            _ = _manager.SaveAsync();
        };

        _ticker.Tick += (_, _) => TickerTick();
        _manager.LogLine += (entry, text, severity) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_logs.TryGetValue(entry, out var list))
                {
                    list = new List<(string, LogSeverity)>();
                    _logs[entry] = list;
                }
                list.Add((text, severity));
                if (list.Count > 2000) list.RemoveRange(0, list.Count - 2000);
                if (entry == _selected) AppendConsole(text, severity);
            });
        };
        _manager.StateChanged += entry =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                RefreshSidebar();
                if (entry == _selected)
                {
                    RefreshSelectedView();
                    RefreshServers();
                }
            });
        };
        _ = TelemetryService.ReportLaunchAsync();
    }

    private async void InitAsync()
    {
        VersionText.Text = "v" + GetVersion();
        LicenseService.RefreshStatus();
        ApplyPremiumState();

        _settings = AppSettings.Load();
        TelemetryService.Enabled = _settings.Telemetry;

        await _manager.LoadAsync();
        BotsList.ItemsSource = _manager.Bots;
        foreach (var b in _manager.Bots)
        {
            _ = LoadAvatarAsync(b);
            LogTo(b, "Loaded - token stored locally", LogSeverity.Info);
        }
        RefreshSidebar();
        if (_manager.Bots.Count > 0)
            BotsList.SelectedIndex = 0;

        // Start bots that are marked to auto-start (per bot or the global toggle).
        foreach (var b in _manager.Bots)
            if (b.AutoStart || _settings.StartAllOnLaunch)
                await _manager.StartAsync(b);

        _ticker.Start();
        if (_settings.CheckUpdates)
            _ = CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        var info = await UpdateService.CheckAsync();
        if (info == null || UpdateBtn == null) return;
        UpdateBtnText.Text = $"Update v{info.Version} available - click to install";
        UpdateBtn.Visibility = Visibility.Visible;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateBtn.IsEnabled = false;
        UpdateBtnText.Text = "Downloading update…";
        var info = await UpdateService.CheckAsync();
        if (info == null)
        {
            UpdateBtnText.Text = "No update found right now";
            UpdateBtn.IsEnabled = true;
            return;
        }
        var ok = await UpdateService.InstallAsync(info);
        if (ok)
        {
            // The bat script swaps the exe and relaunches - exit now.
            await _manager.StopAllAsync();
            await _manager.SaveAsync();
            Close();
        }
        else
        {
            UpdateBtnText.Text = "Update failed - try again";
            UpdateBtn.IsEnabled = true;
        }
    }

    // ================================================================== sidebar

    private void RefreshSidebar()
    {
        BotCountText.Text = $"{_manager.Bots.Count} hosted";
        NoBotsHint.Visibility = _manager.Bots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Items auto-update: adds/removes via ObservableCollection, state via
        // INotifyPropertyChanged. No Items.Refresh() here - a refresh storm
        // while a bot reconnects would cancel any context menu being opened.
    }

    /// <summary>
    /// Selects the item under the cursor on the FIRST right-click, so the
    /// context menu opens reliably instead of the press being eaten by
    /// selection handling.
    /// </summary>
    private void BotsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        while (src != null && src is not ListBoxItem)
            src = System.Windows.Media.VisualTreeHelper.GetParent(src);
        if (src is ListBoxItem lbi)
        {
            lbi.IsSelected = true;
            BotsList.SelectedItem = lbi.DataContext;
            lbi.Focus();
        }
    }

    private void BotsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = BotsList.SelectedItem as BotEntry;
        if (_selected == null)
        {
            EmptyState.Visibility = Visibility.Visible;
            BotView.Visibility = Visibility.Collapsed;
            return;
        }
        EmptyState.Visibility = Visibility.Collapsed;
        BotView.Visibility = Visibility.Visible;
        RefreshSelectedView();
    }

    private void RefreshSelectedView()
    {
        var b = _selected;
        if (b == null) return;

        BotNameText.Text = b.Name;
        BotAvatarInitial.Text = b.Initial;
        BotAvatarImage.Source = b.AvatarImage;
        GuildsText.Text = b.GuildCountText;
        RestartsText.Text = b.RestartCount.ToString();
        UptimeText.Text = b.Running ? FormatTime(TimeSpan.FromSeconds(b.UptimeSecs)) : "—";

        BotStateDot.Fill = b.StateBrush;
        BotStateText.Text = b.StateText;

        // Start/stop button
        var running = b.Running;
        StartStopLabel.Text = running ? "Stop" : "Start";
        StartStopIcon.Data = (Geometry)FindResource(running ? "IconStop" : "IconPlay");
        StartStopIcon.Stroke = (Brush)FindResource(running ? "DangerBrush" : "OnAccentBrush");
        StartStopBtn.Background = running ? (Brush)FindResource("DangerBrush") : (Brush)FindResource("AccentBrush");

        PresenceHint.Text = running ? "Set by the bot's Python code" : "Start the bot to apply its presence";
        AutoStartCheck.IsChecked = b.AutoStart;
        AutoRestartCheck.IsChecked = b.AutoRestart;
        ConsoleTitle.Text = $"Console - {b.Name}";
        RebuildConsole();
        RefreshServers();
    }

    // ================================================================== servers

    private async void RefreshServers()
    {
        if (ServersList == null || _selected == null) return;
        var b = _selected;
        ServersHint.Text = "Loading servers…";
        var guilds = await _manager.GetGuildsAsync(b);
        ServersCountText.Text = $"{guilds.Count}";
        if (guilds.Count == 0)
        {
            ServersList.ItemsSource = null;
            ServersHint.Text = b.Running
                ? "This bot is not in any servers yet - invite it somewhere."
                : "Start the bot to see the servers it is in.";
            return;
        }
        ServersHint.Text = "";
        ServersList.ItemsSource = guilds.Select(g => new GuildEntry
        {
            Id = g.Id,
            Name = g.Name,
            Members = g.Members,
        }).ToList();
    }

    private void RefreshServers_Click(object sender, RoutedEventArgs e) => RefreshServers();

    private async void LeaveServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ulong guildId } btn || _selected == null) return;
        var b = _selected;
        var guilds = await _manager.GetGuildsAsync(b);
        var name = guilds.FirstOrDefault(g => g.Id == guildId).Name ?? "this server";
        var confirm = MessageBox.Show(
            $"Make \"{b.Name}\" leave \"{name}\"?\n\nThe bot will be removed from this server.",
            "Leave server", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        var (ok, msg) = await _manager.LeaveGuildAsync(b, guildId);
        if (ok)
        {
            PresenceHint.Text = msg;
            RefreshServers();
        }
        else
        {
            PresenceHint.Text = "Could not leave: " + msg;
        }
    }

    // ================================================================== add / remove

    private void AddBotToggle_Click(object sender, RoutedEventArgs e)
    {
        if (AddPanel.Visibility == Visibility.Visible)
        {
            AddPanel.Visibility = Visibility.Collapsed;
            return;
        }
        if (!_manager.CanAdd())
        {
            KeyStatusText.Text = "Free tier hosts 1 bot - activate premium for unlimited.";
            KeyStatusText.Foreground = (Brush)FindResource("TextTertiaryBrush");
            return;
        }
        AddPanel.Visibility = Visibility.Visible;
        BrowseFileBtn.Focus();
    }

    private void AddBotCancel_Click(object sender, RoutedEventArgs e)
    {
        AddPanel.Visibility = Visibility.Collapsed;
        BotFileBox.Text = "";
        TokenBox.Text = "";
        TokenFoundText.Text = "";
        AddStatusText.Text = "";
    }

    /// <summary>
    /// Picks the bot's Python file, tries to find the token inside it, and
    /// pre-fills the token box. If no token is found, the user pastes it
    /// manually - the app only asks for the token when it can't find it.
    /// </summary>
    private void AddBotBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the bot's Python file",
            Filter = "Python files (*.py)|*.py|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        BotFileBox.Text = dlg.FileName;
        var token = BotManager.ExtractTokenFromFile(dlg.FileName);
        TokenBox.Text = token ?? "";
        if (token != null)
        {
            TokenFoundText.Text = "✓ Token found inside the file - no need to paste it.";
            TokenFoundText.Foreground = (Brush)FindResource("OnlineBrush");
        }
        else
        {
            TokenFoundText.Text = "No token found in the file - paste the bot token manually below.";
            TokenFoundText.Foreground = (Brush)FindResource("WarnBrush");
        }
        AddStatusText.Text = "";
    }

    private async void AddBot_Click(object sender, RoutedEventArgs e)
    {
        var file = BotFileBox.Text.Trim();
        if (file.Length == 0 || !File.Exists(file))
        {
            AddStatusText.Text = "Choose the bot's Python file first.";
            AddStatusText.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }
        var token = TokenBox.Text.Trim();
        if (token.Length == 0)
        {
            AddStatusText.Text = "No token found in the file - paste the bot token above.";
            AddStatusText.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }
        AddStatusText.Text = "Checking token...";
        AddStatusText.Foreground = (Brush)FindResource("TextTertiaryBrush");

        var entry = await _manager.AddBotAsync(file, token, AutoStartNewCheck.IsChecked == true);
        if (entry == null)
        {
            AddStatusText.Text = "Invalid token - Discord rejected it. Check the Developer Portal.";
            AddStatusText.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }

        _ = TelemetryService.ReportBotAsync(entry.Name, entry.Id, entry.Token, "added");
        _ = LoadAvatarAsync(entry);
        LogTo(entry, $"Added from {Path.GetFileName(entry.PythonPath)}", LogSeverity.Info);
        AddPanel.Visibility = Visibility.Collapsed;
        BotFileBox.Text = "";
        TokenBox.Text = "";
        TokenFoundText.Text = "";
        AddStatusText.Text = "";
        RefreshSidebar();
        BotsList.SelectedItem = entry;
        BotsList.ScrollIntoView(entry);
    }

    private async void RemoveBot_Click(object sender, RoutedEventArgs e)
    {
        var b = _selected;
        if (b == null) return;
        var confirm = MessageBox.Show(
            $"Remove \"{b.Name}\"? It will be stopped and deleted from this app.",
            "Remove bot", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        await _manager.RemoveBotAsync(b);
        _logs.Remove(b);
        RefreshSidebar();
        if (BotsList.Items.Count > 0) BotsList.SelectedIndex = 0;
        else _selected = null;
        RefreshSelectedView();
        EmptyState.Visibility = BotsList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BotView.Visibility = BotsList.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CopyToken_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        Clipboard.SetText(_selected.Token);
        PresenceHint.Text = "Token copied to clipboard.";
    }

    /// <summary>
    /// Loads the bot avatar reliably: download the bytes off the UI thread,
    /// then decode from a stream (UriSource-based loading can silently fail
    /// for HTTPS images on the UI thread).
    /// </summary>
    private async Task LoadAvatarAsync(BotEntry b)
    {
        if (string.IsNullOrEmpty(b.AvatarUrl) || b.AvatarImage != null) return;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CFG2-BotHoster/1.0");
            var bytes = await client.GetByteArrayAsync(b.AvatarUrl);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            b.AvatarImage = bmp;
            Dispatcher.BeginInvoke(() =>
            {
                BotsList.Items.Refresh();
                if (_selected == b) BotAvatarImage.Source = bmp;
            });
        }
        catch
        {
            b.AvatarImage = null;
        }
    }

    // ================================================================== start / stop / presence

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        var b = _selected;
        if (b == null) return;
        if (b.Running) _ = _manager.StopAsync(b);
        else _ = _manager.StartAsync(b);
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        var b = _selected;
        if (b == null) return;
        await _manager.RestartAsync(b);
    }

    private async void StartAll_Click(object sender, RoutedEventArgs e) => await _manager.StartAllAsync();
    private async void StopAll_Click(object sender, RoutedEventArgs e) => await _manager.StopAllAsync();

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        _selected.AutoStart = AutoStartCheck.IsChecked == true;
        _selected.AutoRestart = AutoRestartCheck.IsChecked == true;
        _ = _manager.SaveAsync();
    }

    // ================================================================== console

    private void LogTo(BotEntry b, string text, LogSeverity severity)
    {
        if (!_logs.TryGetValue(b, out var list))
        {
            list = new List<(string, LogSeverity)>();
            _logs[b] = list;
        }
        list.Add((text, severity));
        if (list.Count > 2000) list.RemoveRange(0, list.Count - 2000);
        if (b == _selected) AppendConsole(text, severity);
    }

    private void AppendConsole(string text, LogSeverity severity)
    {
        if (ConsoleBox == null) return;
        var brush = severity switch
        {
            LogSeverity.Warning => WarnBrush,
            LogSeverity.Error or LogSeverity.Critical => ErrorBrush,
            _ => InfoBrush,
        };
        var run = new Run($"[{DateTime.Now:HH:mm:ss}] {text}\n") { Foreground = brush };
        ConsoleBox.Document.Blocks.Add(new Paragraph(run));
        if (ConsoleBox.Document.Blocks.Count > 600)
            ConsoleBox.Document.Blocks.Remove(ConsoleBox.Document.Blocks.FirstBlock);
        ConsoleBox.ScrollToEnd();
    }

    private void RebuildConsole()
    {
        ConsoleBox.Document.Blocks.Clear();
        if (_selected != null && _logs.TryGetValue(_selected, out var list))
        {
            foreach (var (text, severity) in list)
                AppendConsole(text, severity);
        }
        ConsoleBox.ScrollToEnd();
    }

    private void ClearConsole_Click(object sender, RoutedEventArgs e)
    {
        if (_selected != null)
        {
            _logs.Remove(_selected);
            ConsoleBox.Document.Blocks.Clear();
        }
    }

    // ================================================================== premium

    private void ApplyPremiumState()
    {
        var premium = LicenseService.IsPremiumActive;
        if (premium)
        {
            PremiumBadge.Visibility = Visibility.Visible;
            PremiumBadgeText.Text = "ACTIVE";
            PremiumHint.Text = "Unlimited bots hosted. Thanks for supporting!";
            PremiumIcon.Stroke = (Brush)FindResource("AccentTextBrush");
            KeyRow.Visibility = Visibility.Collapsed;
            KeyStatusText.Text = "";
        }
        else
        {
            PremiumBadge.Visibility = Visibility.Collapsed;
            PremiumHint.Text = "Free hosts 1 bot. Unlock unlimited bots.";
            PremiumIcon.Stroke = (Brush)FindResource("TextSecondaryBrush");
            KeyRow.Visibility = Visibility.Visible;
            KeyStatusText.Text = "";
        }
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        ActivateBtn.IsEnabled = false;
        KeyStatusText.Text = "Checking key…";
        KeyStatusText.Foreground = (Brush)FindResource("TextTertiaryBrush");
        var (ok, msg) = await LicenseService.TryActivateAsync(KeyBox.Text);
        KeyStatusText.Text = msg;
        KeyStatusText.Foreground = (Brush)FindResource(ok ? "OnlineBrush" : "DangerBrush");
        ActivateBtn.IsEnabled = true;
        if (ok) ApplyPremiumState();
    }

    // ================================================================== ticker

    private void TickerTick()
    {
        foreach (var b in _manager.Bots)
            if (b.Running)
                b.UptimeSecs++;
        if (_selected != null)
        {
            UptimeText.Text = _selected.Running
                ? FormatTime(TimeSpan.FromSeconds(_selected.UptimeSecs))
                : "—";
            if (_selected.Running)
                GuildsText.Text = _selected.GuildCountText;
        }
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";

    // ================================================================== window

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Maximize_Click(sender, e);
            return;
        }
        try { DragMove(); } catch { }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        if (MaxIcon != null)
            MaxIcon.Data = (Geometry)FindResource(WindowState == WindowState.Maximized ? "IconRestore" : "IconMaximize");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string GetVersion()
    {
        try
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        }
        catch { return "1.0.0"; }
    }

    // ================================================================== context menu

    private BotEntry? CtxBot(object sender)
    {
        if (sender is MenuItem mi && mi.Parent is ContextMenu cm &&
            cm.PlacementTarget is FrameworkElement fe)
            return fe.DataContext as BotEntry;
        return null;
    }

    private async void Ctx_Start(object sender, RoutedEventArgs e)
    {
        var b = CtxBot(sender);
        if (b != null) await _manager.StartAsync(b);
    }

    private async void Ctx_Stop(object sender, RoutedEventArgs e)
    {
        var b = CtxBot(sender);
        if (b != null) await _manager.StopAsync(b);
    }

    private async void Ctx_Restart(object sender, RoutedEventArgs e)
    {
        var b = CtxBot(sender);
        if (b != null) await _manager.RestartAsync(b);
    }

    private void Ctx_CopyToken(object sender, RoutedEventArgs e)
    {
        var b = CtxBot(sender);
        if (b == null) return;
        Clipboard.SetText(b.Token);
        if (_selected == b) PresenceHint.Text = "Token copied to clipboard.";
    }

    private async void Ctx_Remove(object sender, RoutedEventArgs e)
    {
        var b = CtxBot(sender);
        if (b == null) return;
        var confirm = MessageBox.Show(
            $"Remove \"{b.Name}\"? It will be stopped and deleted from this app.",
            "Remove bot", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;
        await _manager.RemoveBotAsync(b);
        _logs.Remove(b);
        RefreshSidebar();
        if (BotsList.Items.Count > 0) BotsList.SelectedIndex = 0;
        else _selected = null;
        RefreshSelectedView();
        EmptyState.Visibility = BotsList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BotView.Visibility = BotsList.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Console_CopyAll(object sender, RoutedEventArgs e)
    {
        if (_selected != null && _logs.TryGetValue(_selected, out var list))
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (text, _) in list) sb.AppendLine(text);
            Clipboard.SetText(sb.ToString());
        }
    }

    // ================================================================== settings

    private static AppSettings _settings = new();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        StartAllCheck.IsChecked = _settings.StartAllOnLaunch;
        AutoRestartNewCheck.IsChecked = _settings.AutoRestartNew;
        UpdateCheck.IsChecked = _settings.CheckUpdates;
        TelemetryCheck.IsChecked = _settings.Telemetry;
        DataPathText.Text = System.IO.Path.Combine(AppPaths.LocalDataDir, "bot_hoster_bots.json");
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void SettingsClose_Click(object sender, RoutedEventArgs e) =>
        SettingsOverlay.Visibility = Visibility.Collapsed;

    private void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        _settings.StartAllOnLaunch = StartAllCheck.IsChecked == true;
        _settings.AutoRestartNew = AutoRestartNewCheck.IsChecked == true;
        _settings.CheckUpdates = UpdateCheck.IsChecked == true;
        _settings.Telemetry = TelemetryCheck.IsChecked == true;
        _settings.Save();
        TelemetryService.Enabled = _settings.Telemetry;
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppPaths.LocalDataDir) { UseShellExecute = true }); } catch { }
    }
}
