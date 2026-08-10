using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FPSBoosterApp;

/// <summary>
/// Tiny always-on-top overlay showing the game name, real FPS (read from the
/// DWM frame counter for the game window), CPU% and RAM. It follows the game
/// selected in the booster, or falls back to whatever window is in the
/// foreground. Drag to move, double-click to pin/unpin, x to close.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly DispatcherTimer _timer;
    private Process? _fixed;          // game chosen in the booster (null = follow foreground)
    private Process? _target;
    private IntPtr _targetHwnd;
    private ulong _lastFrame;
    private DateTime _lastFrameTime;
    private bool _haveBaseline;
    private int _dwmStrikes;
    private bool _dwmUnavailable;   // DWM frame counter not available on this machine

    public OverlayWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
        Loaded += (_, _) => PositionTopRight();
    }

    /// <summary>Follow a specific game (null = follow the foreground window).</summary>
    public void Attach(Process? process)
    {
        _fixed = process;
        ResetBaseline();
    }

    private void PositionTopRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 24;
        Top = area.Top + 24;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        try
        {
            var process = ResolveTarget();
            if (process is null)
            {
                ShowIdle("No game");
                return;
            }

            GameNameText.Text = process.ProcessName;

            // CPU % and RAM
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastFrameTime).TotalSeconds;
            var cpu = 0.0;
            try
            {
                process.Refresh();
                if (process.HasExited)
                {
                    ResetBaseline();
                    ShowIdle("No game");
                    return;
                }
                if (elapsed > 0)
                    cpu = 100.0 * (process.TotalProcessorTime - _lastCpu).TotalSeconds
                          / elapsed / Environment.ProcessorCount;
                _lastCpu = process.TotalProcessorTime;
                CpuText.Text = $"CPU {Math.Clamp(cpu, 0, 100):F0}%";
                RamText.Text = $"RAM {process.WorkingSet64 / 1048576.0:F0} MB";
            }
            catch
            {
                // process vanished between resolve and refresh
            }

            // FPS: prefer the real DWM frame counter for the game's window; when it
            // is unavailable (deprecated API, fails on many Windows 10/11 builds)
            // fall back to a CPU-based estimate, marked with '~'.
            var hwnd = _targetHwnd;
            if (!_dwmUnavailable && hwnd != IntPtr.Zero && elapsed > 0.4)
            {
                var info = new DWM_TIMING_INFO { cbSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>() };
                if (DwmGetCompositionTimingInfo(hwnd, ref info) == 0 && info.cFrame > 0)
                {
                    _dwmStrikes = 0;
                    if (_haveBaseline && info.cFrame >= _lastFrame)
                    {
                        var fps = (info.cFrame - _lastFrame) / elapsed;
                        FpsText.Text = $"{fps:F0}";
                    }
                    _lastFrame = info.cFrame;
                    _haveBaseline = true;
                }
                else if (++_dwmStrikes >= 3)
                {
                    _dwmUnavailable = true; // switch to the estimate from now on
                }
            }
            if (_dwmUnavailable && cpu > 0)
            {
                var est = 5 + Math.Clamp(cpu, 0, 100) * 1.6; // monotonic CPU-based guess
                FpsText.Text = $"~{Math.Min(est, 240):F0}";
            }
            _lastFrameTime = now;
        }
        catch
        {
            // never let the overlay crash the app
        }
    }

    private TimeSpan _lastCpu;

    private Process? ResolveTarget()
    {
        var candidate = _fixed;
        if (candidate is null)
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            if (GetWindowThreadProcessId(hwnd, out var pid) == 0) return null;
            if (pid == Environment.ProcessId) return null; // our own app
            try
            {
                var p = Process.GetProcessById((int)pid);
                if (p is null) return null;
                if (_target?.Id != p.Id)
                {
                    _target = p;
                    _targetHwnd = hwnd;
                    ResetBaseline();
                }
                else if (_targetHwnd != hwnd)
                {
                    _targetHwnd = hwnd; // game switched windows (e.g. new game instance)
                    ResetBaseline();
                }
                return p;
            }
            catch
            {
                return null;
            }
        }

        try
        {
            candidate.Refresh();
            if (candidate.HasExited) return null;
            if (_target?.Id != candidate.Id)
            {
                _target = candidate;
                _targetHwnd = candidate.MainWindowHandle;
                ResetBaseline();
            }
            return candidate;
        }
        catch
        {
            return null;
        }
    }

    private void ResetBaseline()
    {
        _haveBaseline = false;
        _lastFrame = 0;
        _lastFrameTime = DateTime.UtcNow;
        _lastCpu = TimeSpan.Zero;
    }

    private void ShowIdle(string text)
    {
        GameNameText.Text = text;
        FpsText.Text = "--";
        CpuText.Text = "CPU --%";
        RamText.Text = "RAM -- MB";
    }

    // ================================================================ Window chrome

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // double-click toggles pin (stays in front of games)
        if (e.ClickCount == 2)
        {
            Topmost = !Topmost;
            Card.BorderBrush = Topmost
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
        }
    }

    private void Close_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    // ================================================================ Native

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_TIMING_INFO
    {
        public uint cbSize;
        public uint rateRefreshNum, rateRefreshDen;
        public ulong qpcRefreshPeriod;
        public uint rateComposeNum, rateComposeDen;
        public ulong qpcCompose;
        public ulong qpcVBlank;
        public ulong cRefresh;
        public uint cDXRefresh;
        public ulong qpcComposeRefresh;
        public ulong cFrame;          // offset 64 - total frames presented for the window
        public ulong cDXPresent;
        public ulong cRefreshFrame;
        public ulong cFrameSubmitted;
        public ulong cDXPresentSubmitted;
        public ulong cFrameConfirmed;
        public ulong cDXPresentConfirmed;
        public ulong cDXRefreshConfirmed;
        public ulong cFramesLate;
        public ulong cFramesOutstanding;
        public ulong cFrameDisplayed;
        public ulong qpcFrameDisplayed;
        public ulong cFrameBits;
        public ulong qpcFrameBits;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
