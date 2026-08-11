using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;
using FPSBoosterApp.Services;
using Microsoft.Win32;

namespace FPSBoosterApp;

public partial class MainWindow : Window
{
    private readonly RecorderService _recorder = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly List<MonitorInfo> _monitors = new();
    private DateTime _recStarted;
    private TimeSpan _elapsed;
    private bool _premium;
    private HwndSource? _hwndSource;
    private const int HotkeyId = 0x4346; // "CF"
    private const uint MOD_NOREPEAT = 0x4000;

    private sealed class MonitorInfo
    {
        public required string Name;
        public required int X, Y, Width, Height;
        public bool Primary;
        public override string ToString() => $"{Name}  ·  {Width}×{Height}" + (Primary ? "  ·  primary" : "");
    }

    private sealed class WindowEntry
    {
        public required IntPtr Hwnd;
        public required string Title;
        public required int X, Y, Width, Height;
        public override string ToString() => Title;
    }

    private sealed class Settings
    {
        public string Folder { get; set; } = "";
        public bool SystemAudio { get; set; } = true;
        public bool Mic { get; set; }
        public string MicDevice { get; set; } = "";
        public int Fps { get; set; } = 30;
        public string SourceMode { get; set; } = "monitor";
        public int MonitorIndex { get; set; }
    }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => InitAsync();
        _recorder.Log += msg => WriteLog(msg);
        Closed += (_, _) =>
        {
            UnregisterHotKey();
            _recorder.Dispose();
            SaveSettings();
        };
        _uiTimer.Tick += UiTimer_Tick;
        _ = TelemetryService.ReportLaunchAsync();
    }

    private async void InitAsync()
    {
        var cfg = LoadSettings();

        // Output folder
        if (string.IsNullOrWhiteSpace(cfg.Folder))
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            cfg.Folder = Path.Combine(videos, "CFG2 Recorder");
        }
        FolderBox.Text = cfg.Folder;
        Directory.CreateDirectory(cfg.Folder);
        UpdateFileNamePreview();

        SystemAudioCheck.IsChecked = cfg.SystemAudio;
        MicCheck.IsChecked = cfg.Mic;
        SelectFps(cfg.Fps == 60 ? 1 : 0);

        // Premium
        LicenseService.RefreshStatus();
        ApplyPremiumState();
        RefreshFpsLock();

        // Sources
        EnumMonitors();
        if (MonitorCombo.Items.Count > 0)
            MonitorCombo.SelectedIndex = Math.Clamp(cfg.MonitorIndex, 0, MonitorCombo.Items.Count - 1);
        EnumWindows();
        RefreshMicList();

        if (cfg.SourceMode == "window")
        {
            SrcWindowBtn.IsChecked = true;
            SrcMonitorBtn.IsChecked = false;
            UpdateSourcePanels();
        }

        RegisterHotKey();

        // Prime ffmpeg check
        if (!_recorder.IsFfmpegAvailable)
        {
            StatusDetail.Text = "ffmpeg.exe is missing next to the app - recording unavailable.";
        }

        VersionText.Text = "v" + GetVersion();
    }

    private static void WriteLog(string msg)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kicia");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "recorder.log"),
                $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    // ================================================================== version

    private static string GetVersion()
    {
        try
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        }
        catch { return "1.0.0"; }
    }

    // ================================================================== settings

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kicia", "recorder_settings.json");

    private Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    private void SaveSettings()
    {
        try
        {
            var s = new Settings
            {
                Folder = FolderBox.Text,
                SystemAudio = SystemAudioCheck.IsChecked == true,
                Mic = MicCheck.IsChecked == true,
                MicDevice = MicCombo.SelectedItem?.ToString() ?? "",
                Fps = FpsCombo.SelectedIndex == 1 ? 60 : 30,
                SourceMode = SrcWindowBtn.IsChecked == true ? "window" : "monitor",
                MonitorIndex = MonitorCombo.SelectedIndex,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s));
        }
        catch { }
    }

    // ================================================================== monitors & windows

    private void EnumMonitors()
    {
        _monitors.Clear();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _2) =>
        {
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref info))
            {
                _monitors.Add(new MonitorInfo
                {
                    Name = new string(info.szDevice).TrimEnd('\0'),
                    X = info.rcMonitor.Left, Y = info.rcMonitor.Top,
                    Width = info.rcMonitor.Right - info.rcMonitor.Left,
                    Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                    Primary = (info.dwFlags & 1) != 0,
                });
            }
            return true;
        }, IntPtr.Zero);

        MonitorCombo.Items.Clear();
        foreach (var m in _monitors) MonitorCombo.Items.Add(m);
    }

    private void EnumWindows()
    {
        var entries = new List<WindowEntry>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var len = GetWindowTextLength(hwnd);
            if (len <= 0 || len > 120) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();
            GetWindowRect(hwnd, out var r);
            var w = r.Right - r.Left;
            var h = r.Bottom - r.Top;
            if (w < 200 || h < 150) return true; // skip tiny/odd windows
            entries.Add(new WindowEntry { Hwnd = hwnd, Title = title, X = r.Left, Y = r.Top, Width = w, Height = h });
            return true;
        }, IntPtr.Zero);

        entries.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        var sel = WindowCombo.SelectedItem?.ToString();
        WindowCombo.Items.Clear();
        foreach (var e in entries) WindowCombo.Items.Add(e);
        if (sel != null)
        {
            foreach (var item in WindowCombo.Items)
                if (item.ToString() == sel) { WindowCombo.SelectedItem = item; break; }
        }
        else if (WindowCombo.Items.Count > 0)
        {
            WindowCombo.SelectedIndex = 0;
        }
    }

    private async void RefreshMicList()
    {
        var prev = MicCombo.SelectedItem?.ToString();
        MicCombo.Items.Clear();
        MicCombo.Items.Add("(no microphone)");
        MicCombo.SelectedIndex = 0;
        MicCombo.IsEnabled = MicCheck.IsChecked == true;

        if (!_recorder.IsFfmpegAvailable) return;
        try
        {
            var devices = await Task.Run(() => ListMicDevices());
            foreach (var d in devices) MicCombo.Items.Add(d);
            if (prev != null)
            {
                foreach (var item in MicCombo.Items)
                    if (item.ToString() == prev) { MicCombo.SelectedItem = item; break; }
            }
        }
        catch { }
    }

    private static List<string> ListMicDevices()
    {
        var result = new List<string>();
        var psi = new ProcessStartInfo
        {
            FileName = AppPaths.Combine("ffmpeg.exe"),
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-list_devices");
        psi.ArgumentList.Add("true");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("dshow");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("dummy");
        using var p = Process.Start(psi);
        if (p == null) return result;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(5000);
        foreach (Match m in Regex.Matches(err, "\"([^\"]+)\"\\s+\\(audio\\)"))
            result.Add(m.Groups[1].Value);
        return result;
    }

    // ================================================================== record

    private MonitorInfo? SelectedMonitor =>
        MonitorCombo.SelectedItem as MonitorInfo;

    private WindowEntry? SelectedWindow =>
        WindowCombo.SelectedItem as WindowEntry;

    private bool IsWindowMode => SrcWindowBtn.IsChecked == true;

    private void UpdateSourcePanels()
    {
        if (MonitorPanel == null || WindowPanel == null) return; // fired mid-XAML-load
        MonitorPanel.Visibility = IsWindowMode ? Visibility.Collapsed : Visibility.Visible;
        WindowPanel.Visibility = IsWindowMode ? Visibility.Visible : Visibility.Collapsed;
        UpdateFileNamePreview();
    }

    private void RecBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording)
            _ = StopRecordingAsync();
        else
            StartRecording();
    }

    private void StartRecording()
    {
        if (_recorder.IsRecording) return;

        var outDir = FolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outDir))
        {
            KeyStatusText.Text = "Choose an output folder first.";
            return;
        }
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex)
        {
            KeyStatusText.Text = "Cannot create output folder: " + ex.Message;
            return;
        }

        var opts = new RecorderService.Options
        {
            Fps = FpsCombo.SelectedIndex == 1 ? 60 : 30,
            SystemAudio = SystemAudioCheck.IsChecked == true,
            Watermark = !_premium,
            Crf = _premium ? "18" : "22",
            OutputPath = Path.Combine(outDir, $"CFG2_{DateTime.Now:yyyyMMdd_HHmmss}.mp4"),
        };

        if (IsWindowMode)
        {
            if (SelectedWindow == null)
            {
                KeyStatusText.Text = "Pick a window to capture.";
                return;
            }
            opts.WindowTitle = SelectedWindow.Title;
        }
        else
        {
            if (SelectedMonitor == null)
            {
                KeyStatusText.Text = "Pick a display to capture.";
                return;
            }
            opts.X = SelectedMonitor.X;
            opts.Y = SelectedMonitor.Y;
            opts.Width = SelectedMonitor.Width;
            opts.Height = SelectedMonitor.Height;
        }

        if (MicCheck.IsChecked == true && MicCombo.SelectedIndex > 0)
            opts.MicDevice = MicCombo.SelectedItem?.ToString();

        if (!_recorder.Start(opts))
        {
            KeyStatusText.Text = "Failed to start recording - is ffmpeg.exe next to the app?";
            return;
        }

        _recStarted = DateTime.Now;
        _elapsed = TimeSpan.Zero;
        TimerText.Text = "00:00:00";
        FpsText.Text = "0 FPS";
        SizeText.Text = "0 MB";

        RecStatusText.Text = "Recording";
        RecDot.Fill = (Brush)FindResource("AccentBrush");
        RecDot.Visibility = Visibility.Visible;
        if (RecBtn.Template?.FindName("RecLabel", RecBtn) is TextBlock lbl) lbl.Text = "STOP";
        if (RecBtn.Template?.FindName("RecIcon", RecBtn) is Shapes.Ellipse ic) ic.Visibility = Visibility.Visible;
        SetRecBtnStyle(true);
        StatusDetail.Text = "Recording to " + Path.GetFileName(opts.OutputPath) + "  ·  F9 to stop";
        OpenFolderBtn.Visibility = Visibility.Collapsed;
        _uiTimer.Start();
    }

    private async Task StopRecordingAsync()
    {
        _uiTimer.Stop();
        RecStatusText.Text = "Finalizing…";
        TimerText.Text = FormatTime(_elapsed);
        var final = await _recorder.StopAsync();
        if (final != null)
        {
            TimerText.Text = "00:00:00";
            RecStatusText.Text = "Saved";
            StatusDetail.Text = "Saved: " + final;
            OpenFolderBtn.Visibility = Visibility.Visible;
        }
        else
        {
            RecStatusText.Text = "Ready to record";
            StatusDetail.Text = "Recording was too short to keep, or finalizing failed.";
        }
        if (RecBtn.Template?.FindName("RecLabel", RecBtn) is TextBlock lbl) lbl.Text = "START RECORDING";
        SetRecBtnStyle(false);
        RecDot.Fill = (Brush)FindResource("TextTertiaryBrush");
        _elapsed = TimeSpan.Zero;
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (!_recorder.IsRecording)
        {
            if (_elapsed == TimeSpan.Zero) _uiTimer.Stop();
            return;
        }
        _elapsed = DateTime.Now - _recStarted;
        TimerText.Text = FormatTime(_elapsed);

        // file size
        try
        {
            var f = _recorder.CurrentFile;
            if (f != null && File.Exists(f))
                SizeText.Text = $"{new FileInfo(f).Length / 1024.0 / 1024.0:0.0} MB";
        }
        catch { }
    }

    private static string FormatTime(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    private void SetRecBtnStyle(bool recording)
    {
        var res = FindResource(recording ? "SurfaceActiveBrush" : "AccentGradientBrush");
        var hover = FindResource(recording ? "SurfaceHoverBrush" : "AccentGradientHoverBrush");
        if (RecBtn.Template?.FindName("Bg", RecBtn) is Border bg)
        {
            bg.Background = res as Brush;
            if (!recording)
            {
                bg.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24, ShadowDepth = 0, Opacity = 0.6, Color = Color.FromArgb(0x99, 0xEF, 0x44, 0x44),
                };
            }
            else
            {
                bg.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 18, ShadowDepth = 0, Opacity = 0.7, Color = Color.FromArgb(0xCC, 0xEF, 0x44, 0x44),
                };
            }
        }
        _ = hover;
    }

    // ================================================================== premium

    private void ApplyPremiumState()
    {
        _premium = LicenseService.IsPremiumActive;
        if (_premium)
        {
            PremiumBadge.Visibility = Visibility.Visible;
            PremiumBadgeText.Text = "ACTIVE";
            PremiumHint.Text = "Premium is active - 60 FPS, no watermark, best quality.";
            KeyBox.IsEnabled = false;
            ActivateBtn.IsEnabled = false;
            KeyStatusText.Text = "";
        }
        else
        {
            PremiumBadge.Visibility = Visibility.Collapsed;
            PremiumHint.Text = "Unlock 60 FPS and watermark-free recordings with a license key.";
            KeyBox.IsEnabled = true;
            ActivateBtn.IsEnabled = true;
            KeyStatusText.Text = "";
        }
    }

    private void RefreshFpsLock()
    {
        var premium = LicenseService.IsPremiumActive;
        FpsLockHint.Visibility = premium ? Visibility.Collapsed : Visibility.Visible;
        var item = FpsCombo.Items.Count > 1 ? (ComboBoxItem)FpsCombo.Items[1] : null;
        if (item != null) item.IsEnabled = premium;
        if (!premium && FpsCombo.SelectedIndex == 1) FpsCombo.SelectedIndex = 0;
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        ActivateBtn.IsEnabled = false;
        KeyStatusText.Text = "Checking key…";
        KeyStatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        var (ok, msg) = await LicenseService.TryActivateAsync(KeyBox.Text);
        KeyStatusText.Text = msg;
        KeyStatusText.Foreground = (Brush)FindResource(ok ? "OnlineBrush" : "DangerBrush");
        ActivateBtn.IsEnabled = true;
        if (ok)
        {
            ApplyPremiumState();
            RefreshFpsLock();
        }
    }

    // ================================================================== hotkey (F9)

    private void RegisterHotKey()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(handle);
            _hwndSource?.AddHook(WndProc);
            RegisterHotKey(handle, HotkeyId, MOD_NOREPEAT, 0x78 /*F9*/);
        }
        catch { }
    }

    private void UnregisterHotKey()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero) UnregisterHotKey(handle, HotkeyId);
        }
        catch { }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Dispatcher.BeginInvoke(() =>
            {
                if (_recorder.IsRecording) _ = StopRecordingAsync();
                else StartRecording();
            });
        }
        return IntPtr.Zero;
    }

    // ================================================================== ui events

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

    private void SrcMode_Changed(object sender, RoutedEventArgs e)
    {
        if ((sender as ToggleButton)?.IsChecked != true) return;
        if (SrcMonitorBtn == null || SrcWindowBtn == null) return; // fired mid-XAML-load
        if (sender == SrcMonitorBtn) SrcWindowBtn.IsChecked = false;
        else SrcMonitorBtn.IsChecked = false;
        UpdateSourcePanels();
    }

    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFileNamePreview();

    private void RefreshWindows_Click(object sender, RoutedEventArgs e) => EnumWindows();

    private void MicCheck_Changed(object sender, RoutedEventArgs e) => MicCombo.IsEnabled = MicCheck.IsChecked == true;

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose recordings folder",
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        if (dlg.ShowDialog(this) == true)
        {
            FolderBox.Text = dlg.FolderName;
            UpdateFileNamePreview();
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = FolderBox.Text.Trim();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch { }
    }

    private void SelectFps(int index)
    {
        if (FpsCombo.Items.Count > index) FpsCombo.SelectedIndex = index;
    }

    private void UpdateFileNamePreview()
    {
        if (FileNamePreview == null) return;
        var name = $"CFG2_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
        FileNamePreview.Text = "Will save as:  " + name;
    }

    // ================================================================== native

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
}
