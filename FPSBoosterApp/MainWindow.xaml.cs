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
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FPSBoosterApp.Services;
using Microsoft.Win32;
using Shapes = System.Windows.Shapes;

namespace FPSBoosterApp;

public partial class MainWindow : Window
{
    private readonly RecorderService _recorder = new();
    private readonly PreviewService _preview = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly List<MonitorInfo> _monitors = new();
    private DesktopDuplicator? _duplicator;
    private DateTime _recStarted;
    private TimeSpan _elapsed;
    private bool _premium;
    private HwndSource? _hwndSource;
    private int _hotkeyId;
    private string _bestEncoder = "libx264";

    // preview geometry
    private WriteableBitmap? _previewBitmap;
    private int _previewSrcW, _previewSrcH;   // source monitor size
    private int _previewDispW, _previewDispH; // displayed image rect on screen
    private Rect _previewImageRect;           // where the image is drawn inside the area

    // crop drag state
    private bool _draggingCrop;
    private bool _resizingCrop;
    private Point _dragStart;
    private int _cropStartX, _cropStartY, _cropStartW, _cropStartH;
    private bool _suppressCropUi;

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
        public string Encoder { get; set; } = "auto";
        public int Quality { get; set; } = 1;
        public int Resolution { get; set; }
        public bool Fit { get; set; } = true;
        public int Format { get; set; }
        public int Hotkey { get; set; }
        public int CropX { get; set; }
        public int CropY { get; set; }
        public int CropW { get; set; }
        public int CropH { get; set; }
        public bool CropEnabled { get; set; }
    }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => InitAsync();
        Closed += (_, _) =>
        {
            UnregisterHotKey();
            _recorder.Dispose();
            _preview.Stop();
            SaveSettings();
        };
        _uiTimer.Tick += UiTimer_Tick;
        _recorder.Log += msg => WriteLog(msg);
        _recorder.Progress += (fps, _, _) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                FpsText.Text = $"{fps} FPS";
            });
        };
        _ = TelemetryService.ReportLaunchAsync();
    }

    private async void InitAsync()
    {
        var cfg = LoadSettings();
        _bestEncoder = EncoderDetect.BestEncoder();
        PopulateEncoders();

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
        SelectCombo(QualityCombo, cfg.Quality);
        SelectCombo(ResCombo, cfg.Resolution);
        FitCheck.IsChecked = cfg.Fit;
        SelectCombo(FormatCombo, cfg.Format);
        SelectCombo(HotkeyCombo, cfg.Hotkey);
        CropCheck.IsChecked = cfg.CropEnabled;
        if (cfg.CropW > 0) CropWBox.Text = cfg.CropW.ToString();
        if (cfg.CropH > 0) CropHBox.Text = cfg.CropH.ToString();
        CropXBox.Text = cfg.CropX.ToString();
        CropYBox.Text = cfg.CropY.ToString();

        LicenseService.RefreshStatus();
        ApplyPremiumState();
        RefreshFpsLock();

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
        PreviewArea_SizeChanged(null!, null!);

        if (!_recorder.IsFfmpegAvailable)
            StatusDetail.Text = "ffmpeg.exe is missing next to the app - recording unavailable.";
        VersionText.Text = "v" + GetVersion();
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
                Encoder = EncoderCombo.SelectedItem?.ToString()?.ToLowerInvariant() ?? "auto",
                Quality = QualityCombo.SelectedIndex,
                Resolution = ResCombo.SelectedIndex,
                Fit = FitCheck.IsChecked == true,
                Format = FormatCombo.SelectedIndex,
                Hotkey = HotkeyCombo.SelectedIndex,
                CropEnabled = CropCheck.IsChecked == true,
                CropX = ParseInt(CropXBox.Text),
                CropY = ParseInt(CropYBox.Text),
                CropW = ParseInt(CropWBox.Text),
                CropH = ParseInt(CropHBox.Text),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s));
        }
        catch { }
    }

    private static int ParseInt(string s) => int.TryParse(s, out var v) ? Math.Max(0, v) : 0;

    // ================================================================== encoders

    private void PopulateEncoders()
    {
        EncoderCombo.Items.Clear();
        EncoderCombo.Items.Add("Auto (best available)");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _bestEncoder };
        EncoderCombo.Items.Add(_bestEncoder);
        foreach (var enc in EncoderDetect.Preference)
            if (!seen.Contains(enc))
            {
                seen.Add(enc);
                EncoderCombo.Items.Add(enc);
            }
        EncoderCombo.SelectedIndex = 0;
        UpdateEncoderHint();
    }

    private void EncoderCombo_Changed(object sender, SelectionChangedEventArgs e) => UpdateEncoderHint();

    private void UpdateEncoderHint()
    {
        var sel = EncoderCombo.SelectedItem?.ToString() ?? "";
        if (sel.StartsWith("Auto"))
        {
            var hw = _bestEncoder != "libx264";
            EncoderHint.Text = hw
                ? $"Hardware encoding ({_bestEncoder}) is available - smooth recordings with low CPU."
                : "No hardware encoder found - using x264. 60 FPS may use more CPU.";
        }
        else
        {
            EncoderHint.Text = sel == "libx264"
                ? "Software encoding (CPU). Most compatible, uses CPU power."
                : "Hardware encoding - smooth recordings with low CPU usage.";
        }
    }

    private string SelectedEncoder()
    {
        var sel = EncoderCombo.SelectedItem?.ToString() ?? "Auto (best available)";
        if (sel.StartsWith("Auto")) return _bestEncoder;
        return sel;
    }

    // ================================================================== preview

    private void StartPreview()
    {
        _preview.Stop();
        if (IsWindowMode || SelectedMonitor == null) return;

        var m = SelectedMonitor;
        _previewSrcW = m.Width;
        _previewSrcH = m.Height;
        _preview.Frame += OnPreviewFrame;
        _preview.Log += m2 => WriteLog(m2);
        if (_preview.Start(m.X, m.Y, m.Width, m.Height))
        {
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            StatusDetail.Text = "Live preview active - F9 to start / stop";
        }
        else
        {
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewHintText.Text = "Preview unavailable - recording will still work.";
        }
    }

    private void StopPreview()
    {
        _preview.Frame -= OnPreviewFrame;
        _preview.Stop();
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewHintText.Text = "Select a display and the preview appears here";
    }

    private void OnPreviewFrame(byte[] bgra, int w, int h)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_previewBitmap == null || _previewBitmap.PixelWidth != w || _previewBitmap.PixelHeight != h)
                {
                    _previewBitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                    PreviewImage.Source = _previewBitmap;
                }
                _previewBitmap.WritePixels(new Int32Rect(0, 0, w, h), bgra, w * 4, 0);
            }
            catch { }
        }, DispatcherPriority.Render);
    }

    // ================================================================== crop overlay

    private void PreviewArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PreviewImage == null) return;
        // Compute the Uniform-fit rect of the source image inside the preview area.
        double areaW = PreviewImage.ActualWidth > 0 ? PreviewImage.ActualWidth : 400;
        double areaH = PreviewImage.ActualHeight > 0 ? PreviewImage.ActualHeight : 240;
        if (_previewSrcW <= 0 || _previewSrcH <= 0)
        {
            _previewImageRect = new Rect(0, 0, areaW, areaH);
            return;
        }
        double scale = Math.Min(areaW / _previewSrcW, areaH / _previewSrcH);
        _previewDispW = (int)(_previewSrcW * scale);
        _previewDispH = (int)(_previewSrcH * scale);
        _previewImageRect = new Rect((areaW - _previewDispW) / 2, (areaH - _previewDispH) / 2, _previewDispW, _previewDispH);
        UpdateCropOverlay();
    }

    private void UpdateCropOverlay()
    {
        if (CropOverlay == null || !CropCheck.IsChecked == true || _previewSrcW <= 0)
        {
            if (CropOverlay != null) CropOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        CropOverlay.Visibility = Visibility.Visible;
        var area = _previewImageRect;
        CropOverlay.Width = Math.Max(area.Width, 1);
        CropOverlay.Height = Math.Max(area.Height, 1);
        Canvas.SetLeft(CropOverlay, 0);
        Canvas.SetTop(CropOverlay, 0);

        var cw = ParseInt(CropWBox.Text);
        var ch = ParseInt(CropHBox.Text);
        var cx = ParseInt(CropXBox.Text);
        var cy = ParseInt(CropYBox.Text);
        if (cw <= 0) { cw = _previewSrcW; CropWBox.Text = cw.ToString(); }
        if (ch <= 0) { ch = _previewSrcH; CropHBox.Text = ch.ToString(); }

        double sx = _previewDispW / (double)_previewSrcW;
        double sy = _previewDispH / (double)_previewSrcH;
        double px = area.X + cx * sx;
        double py = area.Y + cy * sy;
        double pw = cw * sx;
        double ph = ch * sy;
        double aw = area.Width;
        double ah = area.Height;

        if (DimTop != null)
        {
            DimTop.Width = aw; DimTop.Height = Math.Max(0, py); Canvas.SetLeft(DimTop, 0); Canvas.SetTop(DimTop, 0);
            DimBottom.Width = aw; DimBottom.Height = Math.Max(0, ah - py - ph); Canvas.SetLeft(DimBottom, 0); Canvas.SetTop(DimBottom, Math.Min(ah, py + ph));
            DimLeft.Width = Math.Max(0, px); DimLeft.Height = Math.Max(0, ph); Canvas.SetLeft(DimLeft, 0); Canvas.SetTop(DimLeft, py);
            DimRight.Width = Math.Max(0, aw - px - pw); DimRight.Height = Math.Max(0, ph); Canvas.SetLeft(DimRight, Math.Min(aw, px + pw)); Canvas.SetTop(DimRight, py);
            CropBorder.Width = Math.Max(0, pw); CropBorder.Height = Math.Max(0, ph); Canvas.SetLeft(CropBorder, px); Canvas.SetTop(CropBorder, py);
        }
    }

    private void CropCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (CropOverlay == null) return;
        if (CropCheck.IsChecked == true)
        {
            var w = ParseInt(CropWBox.Text);
            var h = ParseInt(CropHBox.Text);
            if (w <= 0) CropWBox.Text = _previewSrcW > 0 ? _previewSrcW.ToString() : "1280";
            if (h <= 0) CropHBox.Text = _previewSrcH > 0 ? _previewSrcH.ToString() : "720";
        }
        UpdateCropOverlay();
    }

    private void CropField_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppressCropUi) return;
        UpdateCropOverlay();
    }

    private void CropReset_Click(object sender, RoutedEventArgs e)
    {
        _suppressCropUi = true;
        CropXBox.Text = "0";
        CropYBox.Text = "0";
        if (_previewSrcW > 0) CropWBox.Text = _previewSrcW.ToString();
        if (_previewSrcH > 0) CropHBox.Text = _previewSrcH.ToString();
        _suppressCropUi = false;
        UpdateCropOverlay();
    }

    private void PreviewImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!CropCheck.IsChecked == true || _previewSrcW <= 0) return;
        var pt = e.GetPosition(PreviewImage);
        // convert to source coords
        var area = _previewImageRect;
        double sx = _previewSrcW / (double)Math.Max(1, _previewDispW);
        double sy = _previewSrcH / (double)Math.Max(1, _previewDispH);
        int srcX = (int)((pt.X - area.X) * sx);
        int srcY = (int)((pt.Y - area.Y) * sy);

        var cw = ParseInt(CropWBox.Text);
        var ch = ParseInt(CropHBox.Text);
        int inCrop = (srcX >= ParseInt(CropXBox.Text) && srcX <= ParseInt(CropXBox.Text) + cw &&
                      srcY >= ParseInt(CropYBox.Text) && srcY <= ParseInt(CropYBox.Text) + ch) ? 1 : 0;

        // corner (bottom-right) resize
        int nearCorner = (Math.Abs(srcX - (ParseInt(CropXBox.Text) + cw)) < 40 && Math.Abs(srcY - (ParseInt(CropYBox.Text) + ch)) < 40) ? 1 : 0;

        _dragStart = e.GetPosition(PreviewImage);
        _cropStartX = ParseInt(CropXBox.Text);
        _cropStartY = ParseInt(CropYBox.Text);
        _cropStartW = cw;
        _cropStartH = ch;

        if (nearCorner == 1)
        {
            _resizingCrop = true;
            PreviewImage.Cursor = Cursors.SizeNWSE;
        }
        else if (inCrop == 1)
        {
            _draggingCrop = true;
            PreviewImage.Cursor = Cursors.SizeAll;
        }
        PreviewImage.CaptureMouse();
    }

    private void PreviewImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingCrop && !_resizingCrop) return;
        if (_previewSrcW <= 0) return;
        var pt = e.GetPosition(PreviewImage);
        var area = _previewImageRect;
        double sx = _previewSrcW / (double)Math.Max(1, _previewDispW);
        double sy = _previewSrcH / (double)Math.Max(1, _previewDispH);
        int dx = (int)((pt.X - _dragStart.X) * sx);
        int dy = (int)((pt.Y - _dragStart.Y) * sy);

        _suppressCropUi = true;
        if (_draggingCrop)
        {
            int nx = Math.Clamp(_cropStartX + dx, 0, Math.Max(0, _previewSrcW - _cropStartW));
            int ny = Math.Clamp(_cropStartY + dy, 0, Math.Max(0, _previewSrcH - _cropStartH));
            CropXBox.Text = nx.ToString();
            CropYBox.Text = ny.ToString();
        }
        else if (_resizingCrop)
        {
            int nw = Math.Clamp(_cropStartW + dx, 80, _previewSrcW - _cropStartX);
            int nh = Math.Clamp(_cropStartH + dy, 80, _previewSrcH - _cropStartY);
            CropWBox.Text = nw.ToString();
            CropHBox.Text = nh.ToString();
        }
        _suppressCropUi = false;
        UpdateCropOverlay();
    }

    private void PreviewImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _draggingCrop = false;
        _resizingCrop = false;
        PreviewImage.Cursor = null;
        PreviewImage.ReleaseMouseCapture();
    }

    // ================================================================== record

    private MonitorInfo? SelectedMonitor => MonitorCombo.SelectedItem as MonitorInfo;
    private WindowEntry? SelectedWindow => WindowCombo.SelectedItem as WindowEntry;
    private bool IsWindowMode => SrcWindowBtn.IsChecked == true;

    private void UpdateSourcePanels()
    {
        if (MonitorPanel == null || WindowPanel == null) return; // mid-XAML-load
        MonitorPanel.Visibility = IsWindowMode ? Visibility.Collapsed : Visibility.Visible;
        WindowPanel.Visibility = IsWindowMode ? Visibility.Visible : Visibility.Collapsed;
        UpdateFileNamePreview();
        if (IsWindowMode)
            StopPreview();
        else
            StartPreview();
    }

    private void RecBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording)
            _ = StopRecordingAsync();
        else
            StartRecording();
    }

    private (int W, int H) OutputSize()
    {
        var src = SelectedMonitor;
        int sw = src?.Width ?? _previewSrcW;
        int sh = src?.Height ?? _previewSrcH;
        switch (ResCombo.SelectedIndex)
        {
            case 1: return (1920, 1080);
            case 2: return (1280, 720);
            case 3: return (854, 480);
            default: return (0, 0);
        }
    }

    private (int Quality, int Bitrate, int Crf) QualitySettings()
    {
        var hw = SelectedEncoder() != "libx264";
        return QualityCombo.SelectedIndex switch
        {
            0 => hw ? (0, 5000, 26) : (0, 0, 26),
            2 => hw ? (2, 12000, 20) : (2, 0, 20),
            3 => hw ? (3, 18000, 17) : (3, 0, 17),
            _ => hw ? (1, 8000, 23) : (1, 0, 23),
        };
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

        var (_, bitrate, crf) = QualitySettings();
        var fps = FpsCombo.SelectedIndex == 1 ? 60 : 30;
        var (outW, outH) = OutputSize();
        var ext = FormatCombo.SelectedIndex == 1 ? ".mkv" : ".mp4";
        var crop = CropCheck.IsChecked == true
            ? (ParseInt(CropWBox.Text), ParseInt(CropHBox.Text), ParseInt(CropXBox.Text), ParseInt(CropYBox.Text))
            : (0, 0, 0, 0);
        var (cropW, cropH, cropX, cropY) = crop;

        var opts = new RecorderService.Options
        {
            Fps = fps,
            SystemAudio = SystemAudioCheck.IsChecked == true,
            MicDevice = MicCheck.IsChecked == true && MicCombo.SelectedIndex > 0 ? MicCombo.SelectedItem?.ToString() : null,
            Watermark = !_premium,
            Encoder = SelectedEncoder(),
            BitrateKbps = bitrate,
            Crf = crf,
            OutputPath = Path.Combine(outDir, $"CFG2_{DateTime.Now:yyyyMMdd_HHmmss}{ext}"),
            CropX = cropW > 0 ? cropX : 0,
            CropY = cropW > 0 ? cropY : 0,
            CropW = cropW,
            CropH = cropH,
            OutW = outW,
            OutH = outH,
            Fit = FitCheck.IsChecked == true,
        };

        if (IsWindowMode)
        {
            if (SelectedWindow == null) { KeyStatusText.Text = "Pick a window to capture."; return; }
            opts.WindowTitle = SelectedWindow.Title;
        }
        else
        {
            if (SelectedMonitor == null) { KeyStatusText.Text = "Pick a display to capture."; return; }
            opts.X = SelectedMonitor.X;
            opts.Y = SelectedMonitor.Y;
            opts.Width = SelectedMonitor.Width;
            opts.Height = SelectedMonitor.Height;

            // Try DXGI capture first (smooth, OBS-style). Fall back to gdigrab.
            var dup = new DesktopDuplicator(fps, m => WriteLog(m));
            dup.Start(SelectedMonitor.X, SelectedMonitor.Y, SelectedMonitor.Width, SelectedMonitor.Height);
            var deadline = Environment.TickCount64 + 2000;
            while (!dup.IsRunning && Environment.TickCount64 < deadline) Thread.Sleep(25);
            if (dup.IsRunning)
            {
                opts.CaptureFrames = true;
                _duplicator = dup;
            }
            else
            {
                dup.Stop();
                WriteLog("DXGI capture unavailable - using gdigrab");
            }
        }

        if (!_recorder.Start(opts))
        {
            _duplicator?.Stop();
            _duplicator = null;
            KeyStatusText.Text = "Failed to start recording - is ffmpeg.exe next to the app?";
            return;
        }

        if (opts.CaptureFrames && _duplicator != null)
        {
            var stream = _recorder.FrameInput;
            _duplicator.FrameReady = (bgra, _, _) =>
            {
                if (stream != null)
                {
                    try { stream.Write(bgra, 0, bgra.Length); } catch { }
                }
            };
        }

        _recStarted = DateTime.Now;
        _elapsed = TimeSpan.Zero;
        RecTimeText.Text = "00:00:00";
        FpsText.Text = "0 FPS";
        SizeText.Text = "0 MB";
        ResText.Text = $"{(opts.OutW > 0 ? opts.OutW : opts.Width)}×{(opts.OutH > 0 ? opts.OutH : opts.Height)}";

        if (RecBtn.Template?.FindName("RecLabel", RecBtn) is TextBlock lbl) lbl.Text = "STOP";
        if (RecBtn.Template?.FindName("RecIcon", RecBtn) is Shapes.Ellipse ic) ic.Visibility = Visibility.Visible;
        RecDot.Visibility = Visibility.Visible;
        RecBadge.Visibility = Visibility.Visible;
        RecTimeText.Text = "00:00:00";
        StatusDetail.Text = "Recording - F9 to stop";
        _uiTimer.Start();
    }

    private async Task StopRecordingAsync()
    {
        _uiTimer.Stop();
        _duplicator?.Stop();
        _duplicator = null;
        var final = await _recorder.StopAsync();
        if (final != null)
        {
            StatusDetail.Text = "Saved: " + final;
        }
        else
        {
            StatusDetail.Text = "Recording was too short to keep, or finalizing failed.";
        }
        if (RecBtn.Template?.FindName("RecLabel", RecBtn) is TextBlock lbl) lbl.Text = "START RECORDING";
        RecBadge.Visibility = Visibility.Collapsed;
        RecDot.Visibility = Visibility.Collapsed;
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
        RecTimeText.Text = FormatTime(_elapsed);

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

    // ================================================================== hotkey

    private void RegisterHotKey()
    {
        UnregisterHotKey();
        var vk = HotkeyCombo.SelectedIndex switch
        {
            1 => 0x77, // F8
            2 => 0x76, // F7
            _ => 0x78, // F9 (default / off uses F9 but not registered)
        };
        if (HotkeyCombo.SelectedIndex == 3) return; // off

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(handle);
            _hwndSource?.AddHook(WndProc);
            RegisterHotKey(handle, ++_hotkeyId, MOD_NOREPEAT, (uint)vk);
        }
        catch { }
    }

    private void UnregisterHotKey()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero && _hotkeyId > 0) UnregisterHotKey(handle, _hotkeyId);
        }
        catch { }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && wParam.ToInt32() == _hotkeyId)
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
        if (SrcMonitorBtn == null || SrcWindowBtn == null) return; // mid-XAML-load
        if (sender == SrcMonitorBtn) SrcWindowBtn.IsChecked = false;
        else SrcMonitorBtn.IsChecked = false;
        UpdateSourcePanels();
    }

    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFileNamePreview();
        StartPreview();
    }

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

    private void SelectFps(int index)
    {
        if (FpsCombo.Items.Count > index) FpsCombo.SelectedIndex = index;
    }

    private static void SelectCombo(ComboBox combo, int index)
    {
        if (combo.Items.Count > index && index >= 0) combo.SelectedIndex = index;
    }

    private void UpdateFileNamePreview()
    {
        if (FileNamePreview == null) return;
        var ext = FormatCombo?.SelectedIndex == 1 ? ".mkv" : ".mp4";
        FileNamePreview.Text = "Will save as:  CFG2_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ext;
    }

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
            if (r.Right - r.Left < 200 || r.Bottom - r.Top < 150) return true;
            entries.Add(new WindowEntry { Hwnd = hwnd, Title = title });
            return true;
        }, IntPtr.Zero);

        entries.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        var sel = WindowCombo.SelectedItem?.ToString();
        WindowCombo.Items.Clear();
        foreach (var e2 in entries) WindowCombo.Items.Add(e2);
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

    private static string GetVersion()
    {
        try
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.1.0";
        }
        catch { return "1.1.0"; }
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

    private const uint MOD_NOREPEAT = 0x4000;

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
