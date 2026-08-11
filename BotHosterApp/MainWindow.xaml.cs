using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private readonly Dictionary<BotEntry, string> _lastToastState = new();
    private readonly Dictionary<BotEntry, DateTime> _lastErrorToast = new();
    private readonly Dictionary<BotEntry, TimeSpan> _lastCpu = new();
    private ICollectionView? _botsView;

    private static readonly Brush InfoBrush = new SolidColorBrush(MediaColor.FromRgb(0xD4, 0xD4, 0xD8));
    private static readonly Brush InfoTagBrush = new SolidColorBrush(MediaColor.FromRgb(0x7D, 0xD3, 0xFC));
    private static readonly Brush WarnBrush = new SolidColorBrush(MediaColor.FromRgb(0xFE, 0xBB, 0x3D));
    private static readonly Brush ErrorBrush = new SolidColorBrush(MediaColor.FromRgb(0xF8, 0x71, 0x71));
    private static readonly Brush DimBrush = new SolidColorBrush(MediaColor.FromRgb(0x71, 0x71, 0x7A));

    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _exiting;
    private bool _startToTray;
    private bool _autoScroll = true;

    public MainWindow()
    {
        InitializeComponent();
        _startToTray = Environment.GetCommandLineArgs().Contains("--tray");
        RootGrid.SizeChanged += (_, e) =>
        {
            if (RootClip != null)
                RootClip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        };
        Loaded += (_, _) =>
        {
            Opacity = 0;
            BeginAnimation(OpacityProperty,
                Anim(1, 280, new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            ClampToWorkArea();
            SetupTray();
            if (_startToTray)
                // Hide after Show() fully completes, otherwise the pending
                // Show re-applies Visibility and the window pops up.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(Hide));
            InitAsync();
        };
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

                // Toast on bot errors, throttled so a traceback doesn't spam.
                if (severity == LogSeverity.Error)
                {
                    var last = _lastErrorToast.GetValueOrDefault(entry);
                    if (DateTime.UtcNow - last > TimeSpan.FromSeconds(20))
                    {
                        _lastErrorToast[entry] = DateTime.UtcNow;
                        ToastService.Show("Bot error", $"{entry.Name}: {text}", isError: true);
                    }
                }
            });
        };
        _manager.StateChanged += entry =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                RefreshSidebar();
                UpdateStatusBar();
                if (entry == _selected)
                {
                    RefreshSelectedView();
                    UpdateDotPulse();
                }

                // Toast notifications: small cards in the top-right corner, a
                // bit below the top edge and above the taskbar. They show even
                // when the window is hidden in the tray (24/7 mode). Toasts
                // fire on actual state transitions only, so a bot that is
                // merely re-invoked in the same state stays quiet.
                var prev = _lastToastState.GetValueOrDefault(entry);
                if (prev == entry.LiveState) return;
                _lastToastState[entry] = entry.LiveState;
                switch (entry.LiveState)
                {
                    case "running":
                        ToastService.Show("Bot online", $"{entry.Name} connected to Discord.");
                        break;
                    case "restarting":
                        ToastService.Show("Bot crashed", $"{entry.Name} stopped responding - restarting it...", isError: true);
                        break;
                    case "offline":
                        if (!entry.Running)
                            ToastService.Show("Bot stopped", $"{entry.Name} is no longer running.");
                        break;
                }
            });
        };
        _ = TelemetryService.ReportLaunchAsync();
    }

    /// <summary>System tray icon: lets the app keep running 24/7 in the
    /// background when the window is closed. Exit lives in the tray menu.</summary>
    private void SetupTray()
    {
        try
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "CFG2 Bot Hoster - bots running in the background",
                Visible = true,
            };
            try
            {
                if (Environment.ProcessPath != null)
                    _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            }
            catch { }

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open CFG2 Bot Hoster", null, (_, _) =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            });
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => TrayExit());
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, _) =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            };
        }
        catch { }
    }

    private void TrayExit()
    {
        _exiting = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        Close();
    }

    /// <summary>
    /// Keeps the window fully on screen. On small displays (e.g. 720p) the
    /// centered window can be taller than the work area, which puts the
    /// titlebar and top content off-screen and clips text.
    /// </summary>
    private void ClampToWorkArea()
    {
        try
        {
            var wa = SystemParameters.WorkArea;
            if (Height > wa.Height - 12)
                Height = Math.Max(MinHeight, wa.Height - 12);
            if (Width > wa.Width - 12)
                Width = Math.Max(MinWidth, wa.Width - 12);
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
        }
        catch { }
    }

    /// <summary>Close hides to the tray instead of quitting when tray mode is on.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting && _settings.KeepInTray && _trayIcon != null)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private async void InitAsync()
    {
        VersionText.Text = "v" + GetVersion();
        var logo = App.TryLoadLogo();
        if (logo != null)
        {
            TitleLogo.Source = logo;
            RailLogo.Source = logo;
        }
        LicenseService.RefreshStatus();
        ApplyPremiumState();

        _settings = AppSettings.Load();
        TelemetryService.Enabled = _settings.Telemetry;

        GoogleAuthService.LoadSession();
        NotificationList.ItemsSource = ToastService.History;

        await _manager.LoadAsync();
        _botsView = CollectionViewSource.GetDefaultView(_manager.Bots);
        _botsView.Filter = BotFilter;
        BotsList.ItemsSource = _botsView;
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

        // Balance notification for premium users whose key has an explicit balance.
        if (LicenseService.IsPremiumActive && LicenseService.HasBalance && LicenseService.Balance <= 0)
            ToastService.Show("Balance empty", "Your premium balance is $0.00 - ask the developer to top it up.", isError: true);

        // Ask once per open while startup mode is off.
        if (!_settings.LaunchOnStartup)
            AskStartupOverlay.Visibility = Visibility.Visible;

        // Google login gate (only when the developer enabled it AND it's configured).
        if (_settings.RequireGoogleLogin && !GoogleAuthService.IsSignedIn)
        {
            if (GoogleAuthService.IsConfigured)
            {
                GateLogo.Source = App.TryLoadLogo();
                GoogleGateOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                ToastService.Show("Google sign-in not configured",
                    "Require Google login is on but no Client ID is compiled in yet - see GoogleAuthService.cs.",
                    isError: true);
            }
        }
    }

    // ================================================================== main tabs / account

    private void MainTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tab }) return;
        var isBots = tab == "bots";
        RailBotsBtn.Style = (Style)FindResource(isBots ? "RailIconActiveButton" : "RailIconButton");
        RailAccountBtn.Style = (Style)FindResource(isBots ? "RailIconButton" : "RailIconActiveButton");
        AccountPanel.Visibility = isBots ? Visibility.Collapsed : Visibility.Visible;
        if (isBots)
        {
            ShowBotsView();
        }
        else
        {
            EmptyState.Visibility = Visibility.Collapsed;
            BotView.Visibility = Visibility.Collapsed;
            RefreshAccountView();
            FadeIn(AccountPanel);
        }
    }

    private void ShowBotsView()
    {
        EmptyState.Visibility = _selected == null ? Visibility.Visible : Visibility.Collapsed;
        BotView.Visibility = _selected == null ? Visibility.Collapsed : Visibility.Visible;
        FadeIn(_selected == null ? EmptyState : BotView);
    }

    private void RefreshAccountView()
    {
        var premium = LicenseService.IsPremiumActive;
        var g = GoogleAuthService.Current;

        // Sign-in card
        GoogleSignedOutPanel.Visibility = g == null ? Visibility.Visible : Visibility.Collapsed;
        GoogleSignedInPanel.Visibility = g == null ? Visibility.Collapsed : Visibility.Visible;
        if (g != null)
        {
            GoogleNameText.Text = string.IsNullOrWhiteSpace(g.Name) ? "Google user" : g.Name;
            GoogleEmailText.Text = string.IsNullOrWhiteSpace(g.Email) ? "Signed in via Google" : g.Email;
            GoogleAvatarEllipse.Fill = string.IsNullOrWhiteSpace(g.Picture)
                ? null
                : new ImageBrush(new BitmapImage(new Uri(g.Picture))) { Stretch = Stretch.UniformToFill };
        }
        if (GoogleGateOverlay.Visibility == Visibility.Visible && GoogleAuthService.IsSignedIn)
            GoogleGateOverlay.Visibility = Visibility.Collapsed;

        // Limits
        LimitsStatusText.Text = premium
            ? "Premium is active on this device. All limits are unlocked."
            : "Free tier: 1 bot. Add more bots with a premium key (bottom of the sidebar).";

        // Balance
        BalanceText.Foreground = (Brush)FindResource("TextBrush");
        if (!premium)
        {
            BalanceText.Text = "$0.00";
            BalanceHint.Text = "Free tier has no balance. Activate a premium key to use balance.";
        }
        else if (!LicenseService.HasBalance)
        {
            BalanceText.Text = "—";
            BalanceHint.Text = "No balance is set on this key. The developer can add \"|balance:50\" to your key line in keys.txt.";
        }
        else
        {
            BalanceText.Text = LicenseService.Balance.ToString("C2");
            BalanceText.Foreground = (Brush)FindResource(LicenseService.Balance > 0 ? "OnlineBrush" : "DangerBrush");
            BalanceHint.Text = LicenseService.Balance > 0
                ? "Spent balance comes off this amount. Contact the developer to top up."
                : "Your balance is empty - contact the developer to top up.";
        }

        // Notifications
        NotificationsEmptyText.Visibility = ToastService.History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void GoogleSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (!GoogleAuthService.IsConfigured)
        {
            var notice = "Google sign-in needs a one-time setup by the developer: create a free OAuth Client ID at console.cloud.google.com/apis/credentials (type: Desktop app, redirect http://localhost:52621/) and paste it into GoogleAuthService.cs. It's compiled in, so end users never configure anything.";
            if (GoogleStatusText != null)
            {
                GoogleStatusText.Text = notice;
                GoogleStatusText.Foreground = (Brush)FindResource("WarnBrush");
            }
            if (GateStatusText != null) GateStatusText.Text = notice;
            return;
        }
        GoogleSignInBtn.IsEnabled = false;
        GateSignInBtn.IsEnabled = false;
        GoogleStatusText.Text = "Opening Google…";
        var (ok, msg, profile) = await GoogleAuthService.SignInAsync();
        GoogleSignInBtn.IsEnabled = true;
        GateSignInBtn.IsEnabled = true;
        GoogleStatusText.Text = msg;
        GoogleStatusText.Foreground = (Brush)FindResource(ok ? "OnlineBrush" : "DangerBrush");
        GateStatusText.Text = msg;
        if (ok && profile != null)
        {
            ToastService.Show("Signed in", $"Welcome, {profile.Name}!");
            RefreshAccountView();
        }
    }

    private void GoogleSignOut_Click(object sender, RoutedEventArgs e)
    {
        GoogleAuthService.SignOut();
        RefreshAccountView();
    }

    private void AskStartupYes_Click(object sender, RoutedEventArgs e)
    {
        _settings.LaunchOnStartup = true;
        AppSettings.SetLaunchOnStartup(true);
        _settings.Save();
        AskStartupOverlay.Visibility = Visibility.Collapsed;
        if (StartupCheck != null) StartupCheck.IsChecked = true;
    }

    private void AskStartupNo_Click(object sender, RoutedEventArgs e)
    {
        AskStartupOverlay.Visibility = Visibility.Collapsed;
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
        var online = _manager.Bots.Count(b => b.Running);
        BotCountText.Text = $"{_manager.Bots.Count} · {online} online";
        var searching = !string.IsNullOrWhiteSpace(SearchBox.Text);
        var visible = _botsView?.Cast<object>().Any() ?? false;
        NoBotsHint.Visibility = _manager.Bots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchNoMatch.Visibility = _manager.Bots.Count > 0 && searching && !visible ? Visibility.Visible : Visibility.Collapsed;
        // Items auto-update: adds/removes via ObservableCollection, state via
        // INotifyPropertyChanged. No Items.Refresh() here - a refresh storm
        // while a bot reconnects would cancel any context menu being opened.
    }

    private bool BotFilter(object o) =>
        o is BotEntry b && (string.IsNullOrWhiteSpace(SearchBox.Text) ||
                            b.Name.Contains(SearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase));

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchClearButton.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _botsView?.Refresh();
        RefreshSidebar();
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    /// <summary>
    /// Selects the item under the cursor on the FIRST right-click, so the
    /// context menu opens reliably instead of the press being eaten by
    /// selection handling.
    /// </summary>
    private void BotsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        SelectRowUnderCursor(sender, e);
    }

    /// <summary>Right-click selects the row under the cursor first, so the
    /// context menu always acts on the right item (admin lists).</summary>
    private void AdminList_SelectRow(object sender, MouseButtonEventArgs e)
    {
        SelectRowUnderCursor(sender, e);
    }

    private static void SelectRowUnderCursor(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        var src = e.OriginalSource as DependencyObject;
        while (src != null && src is not ListBoxItem)
            src = System.Windows.Media.VisualTreeHelper.GetParent(src);
        if (src is ListBoxItem lbi)
        {
            lbi.IsSelected = true;
            list.SelectedItem = lbi.DataContext;
            lbi.Focus();
        }
    }

    private void BotsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = BotsList.SelectedItem as BotEntry;
        if (AccountPanel.Visibility == Visibility.Visible) return;
        ShowBotsView();
        if (_selected != null) RefreshSelectedView();
    }

    private void RefreshSelectedView()
    {
        var b = _selected;
        if (b == null) return;

        BotNameText.Text = b.Name;
        BotAvatarInitial.Text = b.Initial;
        SetAvatarBrush(b.AvatarImage);
        GuildsText.Text = b.GuildCountText;
        RestartsText.Text = b.RestartCount.ToString();
        UptimeText.Text = b.Running ? FormatTime(TimeSpan.FromSeconds(b.UptimeSecs)) : "—";
        BotIdText.Text = b.Id == 0 ? "—" : b.Id.ToString();
        ProcStatsText.Text = "—";

        BotStateDot.Fill = b.StateBrush;
        BotStateText.Text = b.StateText;
        UpdateDotPulse();

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
        AddPanel.Opacity = 0;
        var tt = new TranslateTransform(0, -6);
        AddPanel.RenderTransform = tt;
        AddPanel.BeginAnimation(UIElement.OpacityProperty,
            Anim(1, 170, new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        tt.BeginAnimation(TranslateTransform.YProperty,
            Anim(0, 170, new QuadraticEase { EasingMode = EasingMode.EaseOut }));
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
        SelectBotFile(dlg.FileName);
    }

    /// <summary>Picks a whole folder; the main .py (one containing the token,
    /// or the only one) is used as the entry point and all files in the
    /// folder become editable in the code editor.</summary>
    private void AddBotFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose the bot's folder - all .py files inside will be editable",
            ShowNewFolderButton = false,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        List<string> pyFiles;
        try
        {
            pyFiles = Directory.EnumerateFiles(dlg.SelectedPath, "*.py", SearchOption.TopDirectoryOnly)
                .ToList();
        }
        catch (Exception ex)
        {
            AddStatusText.Text = "Could not read folder: " + ex.Message;
            AddStatusText.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }
        if (pyFiles.Count == 0)
        {
            AddStatusText.Text = "No .py files found in that folder.";
            AddStatusText.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }

        var main = pyFiles.FirstOrDefault(f => BotManager.ExtractTokenFromFile(f) != null) ?? pyFiles[0];
        SelectBotFile(main);
        AddStatusText.Text = pyFiles.Count == 1
            ? $"1 Python file in the folder."
            : $"Folder has {pyFiles.Count} Python files - all editable in the code editor.";
        AddStatusText.Foreground = (Brush)FindResource("TextTertiaryBrush");
    }

    private void SelectBotFile(string path)
    {
        BotFileBox.Text = path;
        var token = BotManager.ExtractTokenFromFile(path);
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

    private void CopyInvite_Click(object sender, RoutedEventArgs e)
    {
        var b = _selected;
        if (b == null) return;
        if (b.Id == 0)
        {
            PresenceHint.Text = "Bot ID unknown yet - restart the bot so its info is fetched.";
            return;
        }
        var url = $"https://discord.com/oauth2/authorize?client_id={b.Id}&scope=bot&permissions=8";
        Clipboard.SetText(url);
        PresenceHint.Text = "Invite link copied (Administrator permissions).";
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var b = _selected;
        if (b == null || !_logs.TryGetValue(b, out var list) || list.Count == 0)
        {
            PresenceHint.Text = "No console output to save yet.";
            return;
        }
        var safe = string.Concat(b.Name.Split(Path.GetInvalidFileNameChars()));
        var dlg = new System.Windows.Forms.SaveFileDialog
        {
            FileName = $"{safe} {DateTime.Now:yyyy-MM-dd HH-mm}.log",
            Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt",
            Title = $"Save console - {b.Name}",
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        try
        {
            var sb = new StringBuilder();
            foreach (var (text, severity) in list)
                sb.AppendLine($"[{severity}] {text}");
            File.WriteAllText(dlg.FileName, sb.ToString());
            PresenceHint.Text = $"Log saved to {dlg.FileName}";
        }
        catch (Exception ex)
        {
            PresenceHint.Text = $"Could not save log: {ex.Message}";
        }
    }

    /// <summary>
    /// Loads the bot avatar reliably: download the bytes off the UI thread,
    /// then decode from a stream (UriSource-based loading can silently fail
    /// for HTTPS images on the UI thread). Bots added before avatar support
    /// have an empty URL, so re-fetch name/avatar/id from the token first.
    /// </summary>
    private async Task LoadAvatarAsync(BotEntry b)
    {
        if (b.AvatarImage != null) return;

        if (string.IsNullOrEmpty(b.AvatarUrl))
        {
            var info = await BotManager.FetchBotInfoAsync(b.Token);
            if (info != null)
            {
                b.Name = info.Value.Name;
                b.AvatarUrl = info.Value.Avatar;
                b.Id = info.Value.Id;
                await _manager.SaveAsync();
                Dispatcher.BeginInvoke(() =>
                {
                    BotsList.Items.Refresh();
                    if (_selected == b) RefreshSelectedView();
                });
            }
        }
        if (string.IsNullOrEmpty(b.AvatarUrl)) return;

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
                if (_selected == b) SetAvatarBrush(bmp);
            });
        }
        catch
        {
            b.AvatarImage = null;
        }
    }

    /// <summary>Paints the avatar into the header's circular crop.</summary>
    private void SetAvatarBrush(ImageSource? src)
    {
        BotAvatarEllipse.Fill = src == null
            ? null
            : new ImageBrush(src) { Stretch = Stretch.UniformToFill };
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

    // ================================================================== code editor

    private BotEntry? _editorBot;
    private string _editorFile = "";
    private bool _editorDirty;
    private bool _editorLoading;

    private void EditCode_Click(object sender, RoutedEventArgs e)
    {
        var b = _selected;
        if (b == null) return;
        _editorBot = b;
        _editorFile = "";
        _editorDirty = false;

        var dir = Path.GetDirectoryName(b.PythonPath) ?? "";
        var files = new List<FileInfo>();
        if (Directory.Exists(dir))
        {
            try
            {
                files = Directory.EnumerateFiles(dir, "*.py", SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f))
                    .OrderBy(f => f.FullName.Equals(b.PythonPath, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(f => f.Name)
                    .ToList();
            }
            catch { }
        }
        EditorFilesList.ItemsSource = files;
        EditorTitle.Text = "Edit code - " + b.Name;
        EditorPathText.Text = dir;
        EditorStatusText.Text = files.Count == 0
            ? "No .py files found next to the bot's file."
            : $"{files.Count} Python file(s) in this bot's folder - edit, save, and restart to apply.";
        RestartAfterSaveCheck.IsChecked = b.Running;
        EditorOverlay.Visibility = Visibility.Visible;

        if (files.Count > 0)
        {
            EditorFilesList.SelectedItem = files.FirstOrDefault(f => f.FullName.Equals(b.PythonPath, StringComparison.OrdinalIgnoreCase))
                                           ?? files[0];
        }
    }

    private void EditorFilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EditorFilesList.SelectedItem is not FileInfo fi) return;
        _editorLoading = true;
        _editorFile = fi.FullName;
        try
        {
            CodeEditorBox.Text = File.ReadAllText(fi.FullName);
            EditorPathText.Text = fi.FullName;
        }
        catch (Exception ex)
        {
            CodeEditorBox.Text = "";
            EditorStatusText.Text = "Could not read file: " + ex.Message;
        }
        _editorDirty = false;
        _editorLoading = false;
    }

    private void CodeEditorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_editorLoading) return;
        _editorDirty = true;
        EditorStatusText.Text = "Unsaved changes";
    }

    private async void EditorSave_Click(object sender, RoutedEventArgs e)
    {
        if (_editorBot == null || _editorFile.Length == 0)
        {
            EditorStatusText.Text = "Pick a file first.";
            return;
        }
        try
        {
            File.WriteAllText(_editorFile, CodeEditorBox.Text);
            _editorDirty = false;
            EditorStatusText.Text = "Saved - " + Path.GetFileName(_editorFile);
            if (RestartAfterSaveCheck.IsChecked == true && _editorBot.Running)
            {
                EditorStatusText.Text = "Saved - restarting bot to apply...";
                await _manager.RestartAsync(_editorBot);
                EditorStatusText.Text = "Saved - bot restarted with the new code.";
            }
        }
        catch (Exception ex)
        {
            EditorStatusText.Text = "Could not save: " + ex.Message;
        }
    }

    private void EditorClose_Click(object sender, RoutedEventArgs e)
    {
        if (_editorDirty)
        {
            var r = MessageBox.Show(
                "You have unsaved changes. Close without saving?",
                "Edit code", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
        }
        EditorOverlay.Visibility = Visibility.Collapsed;
        _editorBot = null;
        _editorFile = "";
        _editorDirty = false;
    }

    private void EditorOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_editorBot == null) return;
        var dir = Path.GetDirectoryName(_editorBot.PythonPath) ?? "";
        if (Directory.Exists(dir))
        {
            try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); } catch { }
        }
    }

    /// <summary>
    /// Exports the bot as a ready-to-run folder so it can be hosted on any
    /// always-on machine (a spare PC, a friend's PC, or a cheap VPS) - that is
    /// what actually keeps it running while this PC is off.
    /// </summary>
    private void Deploy247_Click(object sender, RoutedEventArgs e)
    {
        var b = _selected;
        if (b == null) return;
        if (string.IsNullOrWhiteSpace(b.PythonPath) || !File.Exists(b.PythonPath))
        {
            MessageBox.Show("This bot has no Python file to export.",
                "Deploy 24/7", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose where to export the 24/7 package",
            ShowNewFolderButton = true,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        try
        {
            var safeName = string.Concat(b.Name.Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_')).Trim();
            if (safeName.Length == 0) safeName = "bot";
            var dest = Path.Combine(dlg.SelectedPath, safeName + " 24-7");
            Directory.CreateDirectory(dest);

            var srcDir = Path.GetDirectoryName(b.PythonPath) ?? "";
            var files = Directory.Exists(srcDir)
                ? Directory.EnumerateFiles(srcDir, "*.py", SearchOption.AllDirectories).ToList()
                : new List<string> { b.PythonPath };
            foreach (var f in files)
            {
                var rel = Path.GetRelativePath(srcDir, f);
                var target = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target, true);
            }

            var mainName = Path.GetFileName(b.PythonPath);
            File.WriteAllText(Path.Combine(dest, "run.bat"),
                "@echo off\r\ncd /d %~dp0\r\npython -u \"" + mainName + "\"\r\n");

            File.WriteAllText(Path.Combine(dest, "README - how to run 24-7.txt"),
                $"24/7 package for \"{b.Name}\"\r\n" +
                "================================\r\n" +
                "This folder contains the bot's Python files. To run it somewhere\r\n" +
                "ALWAYS ON (so it stays online even when this PC is off), pick one:\r\n\r\n" +
                "OPTION A - ANOTHER PC (easiest)\r\n" +
                "1. Copy this folder to any PC that stays on (a spare PC, friend's PC...).\r\n" +
                "2. Install Python from python.org (tick \"Add to PATH\") and run:\r\n" +
                "      pip install discord.py\r\n" +
                "3. Double-click run.bat. Done - the bot is online 24/7 there.\r\n\r\n" +
                "OPTION B - CHEAP/FREE SERVER (VPS)\r\n" +
                "1. Create a free Oracle Cloud VM or a ~$4/mo VPS (Linux).\r\n" +
                "2. Upload this folder, install python3 + discord.py.\r\n" +
                "3. Start it with:  nohup python3 -u \"" + mainName + "\" &\r\n" +
                "The bot keeps running as long as the server is up (months at a time).\r\n\r\n" +
                "OPTION C - THIS APP ON ANOTHER PC\r\n" +
                "Install CFG2 Bot Hoster on the always-on PC, add the bot from this\r\n" +
                "folder (Browse... -> pick " + mainName + "), and enable 'Launch on\r\n" +
                "startup' in Settings. Same as Option A but with the full panel.\r\n\r\n" +
                "Note: the token is inside " + mainName + " - keep this folder private.\r\n");

            ToastService.Show("Deployed 24/7", $"{b.Name} exported to {dest}");
            PresenceHint.Text = $"24/7 package exported to {dest}";
            try { Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true }); } catch { }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not export: " + ex.Message,
                "Deploy 24/7", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
        var (tag, tagBrush, msgBrush) = severity switch
        {
            LogSeverity.Warning => ("WARN", WarnBrush, WarnBrush),
            LogSeverity.Error or LogSeverity.Critical => ("ERR!", ErrorBrush, ErrorBrush),
            _ => ("INFO", InfoTagBrush, InfoBrush),
        };
        var para = new Paragraph { Margin = new Thickness(0) };
        para.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}]") { Foreground = DimBrush });
        para.Inlines.Add(new Run($"[{tag}]") { Foreground = tagBrush, FontWeight = FontWeights.Bold });
        para.Inlines.Add(new Run(" " + text) { Foreground = msgBrush });
        ConsoleBox.Document.Blocks.Add(para);
        if (ConsoleBox.Document.Blocks.Count > 600)
            ConsoleBox.Document.Blocks.Remove(ConsoleBox.Document.Blocks.FirstBlock);
        if (_autoScroll)
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

    private void AutoScroll_Click(object sender, RoutedEventArgs e)
    {
        _autoScroll = !_autoScroll;
        if (AutoScrollIcon != null)
            AutoScrollIcon.Stroke = (Brush)FindResource(_autoScroll ? "AccentTextBrush" : "TextTertiaryBrush");
        if (AutoScrollLabel != null)
        {
            AutoScrollLabel.Foreground = (Brush)FindResource(_autoScroll ? "AccentTextBrush" : "TextTertiaryBrush");
            AutoScrollLabel.Text = _autoScroll ? "Auto-scroll" : "Paused";
        }
        if (_autoScroll)
            ConsoleBox.ScrollToEnd();
    }

    private async void RestartAll_Click(object sender, RoutedEventArgs e)
    {
        var running = _manager.Bots.Count(b => b.Running);
        if (running == 0)
        {
            ToastService.Show("Restart all", "No bots are running right now.");
            return;
        }
        ToastService.Show("Restart all", $"Restarting {running} running bot(s)...");
        await _manager.RestartAllAsync();
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        var b = _selected ?? CtxBot(sender);
        if (b == null || string.IsNullOrWhiteSpace(b.PythonPath)) return;
        var dir = Path.GetDirectoryName(b.PythonPath) ?? "";
        if (Directory.Exists(dir))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{b.PythonPath}\"") { UseShellExecute = true });
            }
            catch { }
        }
        else if (File.Exists(b.PythonPath))
        {
            try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); } catch { }
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
            ProcStatsText.Text = _selected.Running ? SampleProcStats(_selected) : "—";
        }
        UpdateStatusBar();
    }

    /// <summary>Bottom status bar: bot / running counts + longest uptime.</summary>
    private void UpdateStatusBar()
    {
        if (StatusBarText == null) return;
        var all = _manager.Bots;
        var running = all.Count(b => b.Running);
        var online = all.Count(b => b.LiveState == "running");
        var up = running == 0 ? TimeSpan.Zero : all.Where(b => b.Running).Max(b => TimeSpan.FromSeconds(b.UptimeSecs));
        StatusBarText.Text = running == 0
            ? $"{all.Count} bot{(all.Count == 1 ? "" : "s")} · none running"
            : $"{all.Count} bot{(all.Count == 1 ? "" : "s")} · {running} running · {online} online · longest uptime {FormatTime(up)}";
    }

    /// <summary>Pulses the header status dot while the selected bot is online.</summary>
    private void UpdateDotPulse()
    {
        if (BotStateDot == null) return;
        if (_selected is { Running: true } && _selected.LiveState == "running")
        {
            var anim = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            };
            BotStateDot.BeginAnimation(UIElement.OpacityProperty, anim);
        }
        else
        {
            BotStateDot.BeginAnimation(UIElement.OpacityProperty, null);
            BotStateDot.Opacity = 1;
        }
    }

    /// <summary>Live CPU % and memory of the bot's Python process.</summary>
    private string SampleProcStats(BotEntry b)
    {
        try
        {
            var proc = b.Proc;
            if (proc == null) return "—";
            var cpuDelta = proc.TotalProcessorTime - _lastCpu.GetValueOrDefault(b);
            _lastCpu[b] = proc.TotalProcessorTime;
            var cpuPct = Math.Clamp((int)Math.Round(cpuDelta.TotalSeconds * 100), 0, 999);
            var memMb = proc.WorkingSet64 / 1024.0 / 1024.0;
            return $"CPU {cpuPct}% · {memMb:0} MB";
        }
        catch
        {
            return "—";
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

    // ================================================= animations

    private static DoubleAnimation Anim(double to, double ms, IEasingFunction? ease = null)
        => new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };

    private static void FadeIn(UIElement el, double ms = 170)
    {
        if (el == null) return;
        el.Opacity = 0;
        el.BeginAnimation(UIElement.OpacityProperty,
            Anim(1, ms, new QuadraticEase { EasingMode = EasingMode.EaseOut }));
    }

    // ================================================= frameless chrome helpers
    // The window is AllowsTransparency so DWM never paints glass over it. That
    // removes native resize borders, so WM_NCHITTEST restores edge resizing and
    // WM_GETMINMAXINFO keeps the taskbar visible when maximized.

    private const int WM_NCHITTEST = 0x0084;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12,
        HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16,
        HTBOTTOMRIGHT = 17;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int RESIZE_MARGIN = 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public WinPoint ptReserved;
        public WinPoint ptMaxSize;
        public WinPoint ptMaxPosition;
        public WinPoint ptMinTrackSize;
        public WinPoint ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public WinRect rcMonitor;
        public WinRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WinRect rect);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource src)
            src.AddHook(WindowProc);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
            {
                if (WindowState == WindowState.Maximized) break;
                var screen = new WinPoint
                {
                    X = (short)((long)lParam & 0xFFFF),
                    Y = (short)(((long)lParam >> 16) & 0xFFFF)
                };
                handled = true;
                return new IntPtr(HitTest(screen));
            }
            case WM_GETMINMAXINFO:
            {
                var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref mi))
                {
                    mmi.ptMaxPosition.X = mi.rcWork.Left;
                    mmi.ptMaxPosition.Y = mi.rcWork.Top;
                    mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
                    mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
                    Marshal.StructureToPtr(mmi, lParam, false);
                    handled = true;
                }
                break;
            }
        }
        return IntPtr.Zero;
    }

    private int HitTest(WinPoint screen)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(hwnd, out var r)) return HTCLIENT;
        var x = screen.X - r.Left;
        var y = screen.Y - r.Top;
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        if (x <= RESIZE_MARGIN)
            return y <= RESIZE_MARGIN ? HTTOPLEFT : y >= h - RESIZE_MARGIN ? HTBOTTOMLEFT : HTLEFT;
        if (x >= w - RESIZE_MARGIN)
            return y <= RESIZE_MARGIN ? HTTOPRIGHT : y >= h - RESIZE_MARGIN ? HTBOTTOMRIGHT : HTRIGHT;
        if (y <= RESIZE_MARGIN) return HTTOP;
        if (y >= h - RESIZE_MARGIN) return HTBOTTOM;
        return HTCLIENT;
    }

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
        StartupCheck.IsChecked = _settings.LaunchOnStartup;
        TrayCheck.IsChecked = _settings.KeepInTray;
        RequireGoogleCheck.IsChecked = _settings.RequireGoogleLogin;
        DataPathText.Text = System.IO.Path.Combine(AppPaths.LocalDataDir, "bot_hoster_bots.json");
        LidStatusText.Text = ReadLidAction();
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>Reads the AC-power lid-close action (0=do nothing, 1=sleep,
    /// 2=hibernate, 3=shut down). Desktops/VMs have no lid setting.</summary>
    private static string ReadLidAction()
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg", "-q SCHEME_CURRENT SUB_BUTTONS LIDACTION")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return "Lid setting not available on this PC.";
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(3000);
            var m = System.Text.RegularExpressions.Regex.Match(
                output, @"Current AC Power Setting Index:\s*(0x[0-9a-fA-F]+)");
            if (!m.Success) return "Lid setting not available on this PC (no laptop lid).";
            return m.Groups[1].Value switch
            {
                "0x00000000" => "Lid close (AC): Do nothing - bots stay up when the lid closes ✓",
                "0x00000001" => "Lid close (AC): Sleep - bots pause when the lid closes",
                "0x00000002" => "Lid close (AC): Hibernate - bots pause when the lid closes",
                "0x00000003" => "Lid close (AC): Shut down - bots stop when the lid closes",
                _ => "Lid close (AC): " + m.Groups[1].Value,
            };
        }
        catch
        {
            return "Lid setting not available on this PC.";
        }
    }

    /// <summary>Sets the AC lid-close action to "Do nothing" via powercfg.
    /// Elevates with a UAC prompt since power settings are admin-only.</summary>
    private async void LidAction_Click(object sender, RoutedEventArgs e)
    {
        LidStatusText.Text = "Requesting administrator permission...";
        await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("/setacvalueindex");
                psi.ArgumentList.Add("SCHEME_CURRENT");
                psi.ArgumentList.Add("SUB_BUTTONS");
                psi.ArgumentList.Add("LIDACTION");
                psi.ArgumentList.Add("0");
                using (var p = Process.Start(psi))
                    p?.WaitForExit(30000);

                var psi2 = new ProcessStartInfo("powercfg")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true,
                };
                psi2.ArgumentList.Add("/setactive");
                psi2.ArgumentList.Add("SCHEME_CURRENT");
                using (var p2 = Process.Start(psi2))
                    p2?.WaitForExit(30000);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // User declined the UAC prompt.
            }
            catch { }
        });
        LidStatusText.Text = ReadLidAction();
    }

    private void SettingsClose_Click(object sender, RoutedEventArgs e) =>
        SettingsOverlay.Visibility = Visibility.Collapsed;

    private void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        _settings.StartAllOnLaunch = StartAllCheck.IsChecked == true;
        _settings.AutoRestartNew = AutoRestartNewCheck.IsChecked == true;
        _settings.CheckUpdates = UpdateCheck.IsChecked == true;
        _settings.Telemetry = TelemetryCheck.IsChecked == true;
        _settings.LaunchOnStartup = StartupCheck.IsChecked == true;
        _settings.KeepInTray = TrayCheck.IsChecked == true;
        _settings.RequireGoogleLogin = RequireGoogleCheck.IsChecked == true;
        AppSettings.SetLaunchOnStartup(_settings.LaunchOnStartup);
        _settings.Save();
        TelemetryService.Enabled = _settings.Telemetry;
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppPaths.LocalDataDir) { UseShellExecute = true }); } catch { }
    }

    // ================================================================== admin panel

    private const string AdminCode = "b@est_5859";
    private string _adminToken = "";
    private ulong _adminBotId;
    private ulong _adminGuildId;
    private List<AdminChannel> _adminChannels = new();
    private List<AdminRole> _adminRoles = new();
    private List<ulong> _adminBotRoles = new();
    private ulong _adminGuildBase;

    private sealed class AdminChannelView
    {
        public required ulong Id { get; init; }
        public required string Name { get; init; }
        public required int Type { get; init; }
        public string TypeLabel => Type switch
        {
            0 => "TEXT", 2 => "VOICE", 4 => "CATEGORY", 5 => "NEWS",
            13 => "STAGE", 15 => "FORUM", _ => "TYPE" + Type,
        };
        public required ulong? ParentId { get; init; }
        public required string OverwritesJson { get; init; }
        public int PermCount { get; set; }
    }

    private sealed class AdminMemberView
    {
        public required ulong Id { get; init; }
        public required string Username { get; init; }
        public required string Nick { get; init; }
        public required bool IsBot { get; init; }
        public string NickLine => string.IsNullOrEmpty(Nick) ? "" : $"~ {Nick}";
        public Visibility BotTagVisible => IsBot ? Visibility.Visible : Visibility.Collapsed;
        public required string RoleIdsJson { get; init; }
    }

    private sealed class AdminRoleView
    {
        public required ulong Id { get; init; }
        public required string Name { get; init; }
        public required ulong Permissions { get; init; }
        public required int Position { get; init; }
        public required uint Color { get; init; }
        public required bool Managed { get; init; }
        public Brush ColorBrush => new SolidColorBrush(MediaColor.FromRgb(
            (byte)((Color >> 16) & 0xFF), (byte)((Color >> 8) & 0xFF), (byte)(Color & 0xFF)));
        public string PermCountText => $"{AdminService.DecodePermissions(Permissions).Count} permissions";
    }

    private sealed class AdminPermCheck : System.ComponentModel.INotifyPropertyChanged
    {
        public required ulong Bit { get; init; }
        public required string Label { get; init; }
        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsChecked))); }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private void Admin_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
        {
            MessageBox.Show("Select a bot first - the admin panel manages the selected bot's servers.",
                "Admin panel", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        AdminCodeBox.Password = "";
        AdminCodeStatus.Text = "";
        AdminCodeOverlay.Visibility = Visibility.Visible;
        AdminCodeBox.Focus();
    }

    private void AdminCodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AdminCodeUnlock_Click(sender, e);
    }

    private void AdminCodeCancel_Click(object sender, RoutedEventArgs e) =>
        AdminCodeOverlay.Visibility = Visibility.Collapsed;

    private async void AdminCodeUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (AdminCodeBox.Password != AdminCode)
        {
            AdminCodeStatus.Text = "Wrong code.";
            return;
        }
        AdminCodeOverlay.Visibility = Visibility.Collapsed;
        await OpenAdminPanelAsync();
    }

    private async Task OpenAdminPanelAsync()
    {
        var b = _selected;
        if (b == null) return;
        _adminToken = b.Token;
        _adminBotId = b.Id;
        AdminBotText.Text = $"managing servers of \"{b.Name}\"";
        AdminStatusText.Text = "Loading servers...";
        AdminOverlay.Visibility = Visibility.Visible;

        var guilds = await AdminService.GetGuildsAsync(_adminToken);
        if (guilds.Count == 0)
        {
            AdminStatusText.Text = "Could not load servers (or this bot is in none).";
            return;
        }
        AdminServersList.ItemsSource = guilds;
        AdminStatusText.Text = $"{guilds.Count} servers";
        AdminServersList.SelectedIndex = 0;
    }

    private void AdminClose_Click(object sender, RoutedEventArgs e)
    {
        AdminOverlay.Visibility = Visibility.Collapsed;
        _adminChannels = new();
        _adminRoles = new();
        _adminBotRoles = new();
    }

    private void AdminTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tab }) return;
        ChannelsTab.Visibility = tab == "channels" ? Visibility.Visible : Visibility.Collapsed;
        MembersTab.Visibility = tab == "members" ? Visibility.Visible : Visibility.Collapsed;
        RolesTab.Visibility = tab == "roles" ? Visibility.Visible : Visibility.Collapsed;
        PermsTab.Visibility = tab == "perms" ? Visibility.Visible : Visibility.Collapsed;

        TabChannelsBtn.Style = (Style)FindResource(tab == "channels" ? "ModernButton" : "OutlineButton");
        TabMembersBtn.Style = (Style)FindResource(tab == "members" ? "ModernButton" : "OutlineButton");
        TabRolesBtn.Style = (Style)FindResource(tab == "roles" ? "ModernButton" : "OutlineButton");
        TabPermsBtn.Style = (Style)FindResource(tab == "perms" ? "ModernButton" : "OutlineButton");
    }

    private async void AdminServersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AdminServersList.SelectedItem is not AdminGuild g) return;
        _adminGuildId = g.Id;
        AdminStatusText.Text = $"Loading {g.Name}...";

        var (channels, roles, botRoles, everyonePerms) =
            await AdminService.LoadGuildAsync(_adminToken, g.Id, _adminBotId);
        _adminChannels = channels;
        _adminRoles = roles;
        _adminBotRoles = botRoles;
        _adminGuildBase = everyonePerms;

        var channelViews = channels.Select(c => new AdminChannelView
        {
            Id = c.Id, Name = c.Name, Type = c.Type, ParentId = c.ParentId,
            OverwritesJson = c.OverwritesJson, PermCount = 0,
        }).ToList();
        foreach (var cv in channelViews)
        {
            cv.PermCount = AdminService.DecodePermissions(
                AdminService.ComputeChannelPermissions(g.Id, new AdminChannel(cv.Id, cv.Name, cv.Type, 0, cv.ParentId, cv.OverwritesJson),
                    everyonePerms, roles, botRoles, _adminBotId)).Count;
        }
        AdminChannelsList.ItemsSource = channelViews;
        AdminMembersList.ItemsSource = null;
        AdminRolesList.ItemsSource = roles.Select(r => new AdminRoleView
        {
            Id = r.Id, Name = r.Name, Permissions = r.Permissions,
            Position = r.Position, Color = r.Color, Managed = r.Managed,
        }).ToList();
        AdminChannelPerms.ItemsSource = null;
        AdminPermsList.ItemsSource = AdminService.DecodePermissions(ComputeBotGuildPerms());
        AdminPermsHint.Text =
            $"Bot's effective permissions in \"{g.Name}\": " +
            "(the permissions the bot actually has in the server, from its roles + the @everyone base)\n\n" +
            "Select a channel in the Channels tab to see per-channel permissions.";
        AdminStatusText.Text = $"{g.Name} loaded - {channels.Count} channels, {roles.Count} roles";
    }

    private ulong ComputeBotGuildPerms()
    {
        var perms = _adminGuildBase;
        foreach (var role in _adminRoles)
            if (_adminBotRoles.Contains(role.Id))
                perms |= role.Permissions;
        return perms;
    }

    private ulong? SelectedChannelPerms()
    {
        if (AdminChannelsList.SelectedItem is not AdminChannelView cv) return null;
        return AdminService.ComputeChannelPermissions(_adminGuildId,
            new AdminChannel(cv.Id, cv.Name, cv.Type, 0, cv.ParentId, cv.OverwritesJson),
            _adminGuildBase, _adminRoles, _adminBotRoles, _adminBotId);
    }

    private void AdminChannelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AdminChannelsList.SelectedItem is not AdminChannelView cv)
        {
            AdminChannelInfo.Text = "";
            AdminChannelPerms.ItemsSource = null;
            return;
        }
        AdminChannelNameBox.Text = cv.Name;
        var perms = AdminService.ComputeChannelPermissions(_adminGuildId,
            new AdminChannel(cv.Id, cv.Name, cv.Type, 0, cv.ParentId, cv.OverwritesJson),
            _adminGuildBase, _adminRoles, _adminBotRoles, _adminBotId);
        AdminChannelInfo.Text = $"{cv.TypeLabel} channel · {cv.Name} · bot has {AdminService.DecodePermissions(perms).Count} permissions here";
        AdminChannelPerms.ItemsSource = AdminService.DecodePermissions(perms);
    }

    private async void AdminRenameChannel_Click(object sender, RoutedEventArgs e)
    {
        if (AdminChannelsList.SelectedItem is not AdminChannelView cv) return;
        var name = AdminChannelNameBox.Text.Trim();
        if (name.Length == 0) return;
        var (ok, err) = await AdminService.RenameChannelAsync(_adminToken, cv.Id, name);
        AdminStatusText.Text = ok ? $"Channel renamed to \"{name}\"." : "Rename failed: " + err;
        if (ok) await ReloadAdminGuildAsync();
    }

    private async void AdminCreateChannel_Click(object sender, RoutedEventArgs e)
    {
        var name = AdminChannelNameBox.Text.Trim();
        if (name.Length == 0)
        {
            AdminStatusText.Text = "Type a channel name first.";
            return;
        }
        var (ok, err) = await AdminService.CreateChannelAsync(_adminToken, _adminGuildId, name);
        AdminStatusText.Text = ok ? $"Channel \"{name}\" created." : "Create failed: " + err;
        if (ok) await ReloadAdminGuildAsync();
    }

    private async void AdminDeleteChannel_Click(object sender, RoutedEventArgs e)
    {
        if (AdminChannelsList.SelectedItem is not AdminChannelView cv) return;
        var confirm = MessageBox.Show($"Delete the \"{cv.Name}\" channel? This cannot be undone.",
            "Delete channel", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;
        var (ok, err) = await AdminService.DeleteChannelAsync(_adminToken, cv.Id);
        AdminStatusText.Text = ok ? $"Channel \"{cv.Name}\" deleted." : "Delete failed: " + err;
        if (ok) await ReloadAdminGuildAsync();
    }

    private void AdminMembersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AdminMemberInfo.Text = AdminMembersList.SelectedItem is AdminMemberView mv
            ? $"{mv.Username} (ID {mv.Id}){(mv.IsBot ? " · bot" : "")}"
            : "";
    }

    private async void AdminTimeoutMember_Click(object sender, RoutedEventArgs e)
    {
        if (AdminMembersList.SelectedItem is not AdminMemberView mv) return;
        if (!int.TryParse(AdminTimeoutBox.Text.Trim(), out var minutes) || minutes < 1)
        {
            AdminStatusText.Text = "Enter timeout minutes (1+).";
            return;
        }
        var (ok, err) = await AdminService.TimeoutMemberAsync(_adminToken, _adminGuildId, mv.Id, minutes);
        AdminStatusText.Text = ok ? $"{mv.Username} timed out for {minutes} min." : "Timeout failed: " + err;
    }

    private async void AdminKickMember_Click(object sender, RoutedEventArgs e)
    {
        if (AdminMembersList.SelectedItem is not AdminMemberView mv) return;
        var confirm = MessageBox.Show($"Kick \"{mv.Username}\" from this server?",
            "Kick member", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;
        var (ok, err) = await AdminService.KickMemberAsync(_adminToken, _adminGuildId, mv.Id);
        AdminStatusText.Text = ok ? $"{mv.Username} was kicked." : "Kick failed: " + err;
        if (ok) await ReloadAdminMembersAsync();
    }

    private async void AdminBanMember_Click(object sender, RoutedEventArgs e)
    {
        if (AdminMembersList.SelectedItem is not AdminMemberView mv) return;
        var confirm = MessageBox.Show($"Ban \"{mv.Username}\" from this server?",
            "Ban member", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;
        var (ok, err) = await AdminService.BanMemberAsync(_adminToken, _adminGuildId, mv.Id);
        AdminStatusText.Text = ok ? $"{mv.Username} was banned." : "Ban failed: " + err;
    }

    private void AdminRolesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AdminRolesList.SelectedItem is not AdminRoleView rv)
        {
            AdminRolePermStatus.Text = "";
            return;
        }
        AdminRoleNameBox.Text = rv.Name;
        var checks = AdminService.PermFlags.Select(p => new AdminPermCheck
        {
            Bit = p.Bit,
            Label = p.Name,
            IsChecked = (rv.Permissions & p.Bit) != 0 || (rv.Permissions & (1UL << 3)) != 0,
        }).ToList();
        AdminRolePermChecks.ItemsSource = checks;
        AdminRolePermStatus.Text = rv.Managed ? "(managed role - Discord controls its permissions)" : "";
    }

    private async void AdminSaveRolePerms_Click(object sender, RoutedEventArgs e)
    {
        if (AdminRolesList.SelectedItem is not AdminRoleView rv) return;
        if (rv.Managed)
        {
            AdminStatusText.Text = "This role is managed by Discord - permissions can't be edited.";
            return;
        }
        ulong perms = 0;
        if (AdminRolePermChecks.ItemsSource is System.Collections.IEnumerable list)
            foreach (AdminPermCheck c in list)
                if (c.IsChecked) perms |= c.Bit;
        var (ok, err) = await AdminService.UpdateRolePermissionsAsync(_adminToken, _adminGuildId, rv.Id, perms);
        AdminStatusText.Text = ok ? $"Permissions saved for role \"{rv.Name}\"." : "Save failed: " + err;
        if (ok) await ReloadAdminGuildAsync();
    }

    private async void AdminRenameRole_Click(object sender, RoutedEventArgs e)
    {
        if (AdminRolesList.SelectedItem is not AdminRoleView rv) return;
        var name = AdminRoleNameBox.Text.Trim();
        if (name.Length == 0) return;
        var (ok, err) = await AdminService.RenameRoleAsync(_adminToken, _adminGuildId, rv.Id, name);
        AdminStatusText.Text = ok ? $"Role renamed to \"{name}\"." : "Rename failed: " + err;
        if (ok) await ReloadAdminGuildAsync();
    }

    private async void AdminCreateRole_Click(object sender, RoutedEventArgs e)
    {
        var name = AdminRoleNameBox.Text.Trim();
        if (name.Length == 0) name = "New role";
        var (ok, err) = await AdminService.CreateRoleAsync(_adminToken, _adminGuildId, name);
        AdminStatusText.Text = ok ? $"Role \"{name}\" created." : "Create failed: " + err;
        if (ok) await ReloadAdminGuildAsync();
    }

    private async void AdminDeleteRole_Click(object sender, RoutedEventArgs e)
    {
        if (AdminRolesList.SelectedItem is not AdminRoleView rv) return;
        var confirm = MessageBox.Show($"Delete the \"{rv.Name}\" role?",
            "Delete role", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;
        var (ok, err) = await AdminService.DeleteRoleAsync(_adminToken, _adminGuildId, rv.Id);
        AdminStatusText.Text = ok ? $"Role \"{rv.Name}\" deleted." : "Delete failed: " + err;
        if (ok) await ReloadAdminGuildAsync();
    }

    private async Task ReloadAdminGuildAsync()
    {
        if (AdminServersList.SelectedItem is not AdminGuild g) return;
        var (channels, roles, botRoles, everyonePerms) =
            await AdminService.LoadGuildAsync(_adminToken, g.Id, _adminBotId);
        _adminChannels = channels;
        _adminRoles = roles;
        _adminBotRoles = botRoles;
        _adminGuildBase = everyonePerms;
        var selectedChannelId = (AdminChannelsList.SelectedItem as AdminChannelView)?.Id;
        var selectedRoleId = (AdminRolesList.SelectedItem as AdminRoleView)?.Id;

        var channelViews = channels.Select(c => new AdminChannelView
        {
            Id = c.Id, Name = c.Name, Type = c.Type, ParentId = c.ParentId,
            OverwritesJson = c.OverwritesJson, PermCount = 0,
        }).ToList();
        foreach (var cv in channelViews)
        {
            cv.PermCount = AdminService.DecodePermissions(
                AdminService.ComputeChannelPermissions(g.Id, new AdminChannel(cv.Id, cv.Name, cv.Type, 0, cv.ParentId, cv.OverwritesJson),
                    everyonePerms, roles, botRoles, _adminBotId)).Count;
        }
        AdminChannelsList.ItemsSource = channelViews;
        if (selectedChannelId != null)
        {
            var idx = channelViews.FindIndex(c => c.Id == selectedChannelId);
            if (idx >= 0) AdminChannelsList.SelectedIndex = idx;
        }
        AdminRolesList.ItemsSource = roles.Select(r => new AdminRoleView
        {
            Id = r.Id, Name = r.Name, Permissions = r.Permissions,
            Position = r.Position, Color = r.Color, Managed = r.Managed,
        }).ToList();
        if (selectedRoleId != null)
        {
            var rolesList = (System.Collections.IList)AdminRolesList.ItemsSource;
            for (var i = 0; i < rolesList.Count; i++)
                if (((AdminRoleView)rolesList[i]!).Id == selectedRoleId)
                { AdminRolesList.SelectedIndex = i; break; }
        }
        AdminPermsList.ItemsSource = AdminService.DecodePermissions(ComputeBotGuildPerms());
        await ReloadAdminMembersAsync();
    }

    private async Task ReloadAdminMembersAsync()
    {
        var members = await AdminService.GetMembersAsync(_adminToken, _adminGuildId);
        AdminMembersList.ItemsSource = members.Select(m => new AdminMemberView
        {
            Id = m.Id, Username = m.Username, Nick = m.Nick, IsBot = m.IsBot, RoleIdsJson = m.RoleIdsJson,
        }).ToList();
    }
}
