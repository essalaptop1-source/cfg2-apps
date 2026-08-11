using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace FPSBoosterApp.Services;

/// <summary>
/// Real per-process FPS from the graphics driver's ETW events. Every time a
/// game presents a frame (Present/Blit/flip), the DxgKrnl provider emits an
/// event - so counting those events gives the actual rendered frame rate
/// without injecting anything into the game. Requires administrator rights
/// (the booster always runs elevated).
///
/// A 1s flush loop forces the real-time buffers out - ETW only delivers
/// real-time events when a buffer fills or is flushed, and frame events alone
/// would never fill the default 64 MB buffers.
///
/// Note: some "debloated"/tweaked Windows installs block user-mode ETW
/// delivery entirely; GetFps then returns -1 and the overlay falls back to
/// the DWM counter, then a CPU estimate.
/// </summary>
public static class FrameCounterService
{
    private static readonly Guid DxgKrnlProvider = new("802ec45a-1e99-4b83-9925-ee788d1c91ef");

    // DXGK_ETW_KEYWORD_LOGGING_* (dxgkrnl.h)
    private const ulong KeywordBlit = 0x2;
    private const ulong KeywordPresent = 0x4;
    private const ulong KeywordFlip = 0x8;
    private const ulong KeywordMmioFlip = 0x10;

    // Microsoft-Windows-DxgKrnl event ids
    private const ushort EventPresent = 42;  // opcode Stop = a frame was presented
    private const ushort EventBlit = 52;     // opcode Stop = a frame was blitted
    private const ushort EventMmioFlip = 66; // fullscreen-exclusive flips
    private const ushort EventMmioFlipImmediate = 67;

    private static readonly object Lock = new();
    private static readonly Dictionary<int, Queue<long>> Frames = new(); // pid -> frame timestamps (ticks)
    private static readonly Dictionary<int, long> LastCounted = new();   // dedupe across event types
    private static TraceEventSession? _session;

    public static bool IsRunning { get; private set; }

    /// <summary>Start the real-time ETW session (no-op if already running or not elevated).</summary>
    public static void Start()
    {
        if (IsRunning) return;
        try
        {
            if (TraceEventSession.IsElevated() != true)
            {
                Log("frame counter: not elevated, real FPS unavailable");
                return;
            }

            // clean up any session orphaned by a previous hard kill
            try { TraceEventSession.GetActiveSession("FPSBoosterFrameCounter")?.Stop(); } catch { }

            _session = new TraceEventSession("FPSBoosterFrameCounter");
            _session.EnableProvider(
                DxgKrnlProvider,
                TraceEventLevel.Informational,
                KeywordBlit | KeywordPresent | KeywordFlip | KeywordMmioFlip);

            _session.Source.Dynamic.AddCallbackForProviderEvent("Microsoft-Windows-DxgKrnl", null, OnEvent);
            _session.Source.AllEvents += _ => { }; // keep the source flowing

            _ = Task.Run(() =>
            {
                try
                {
                    _session.Source.Process();
                }
                catch (Exception ex)
                {
                    IsRunning = false;
                    Log($"frame counter stopped: {ex.Message}");
                }
            });

            // force the real-time buffers out every second
            _ = Task.Run(async () =>
            {
                while (IsRunning)
                {
                    await Task.Delay(1000);
                    try { _session?.Flush(); } catch { }
                }
            });

            IsRunning = true;
            Log("frame counter: ETW session started");
        }
        catch (Exception ex)
        {
            IsRunning = false;
            try { _session?.Dispose(); } catch { }
            _session = null;
            Log($"frame counter failed: {ex.Message}");
        }
    }

    private static void OnEvent(TraceEvent evt)
    {
        var id = (ushort)evt.ID;
        var isFrame = (id == EventPresent && evt.Opcode == TraceEventOpcode.Stop) ||
                      (id == EventBlit && evt.Opcode == TraceEventOpcode.Stop) ||
                      id == EventMmioFlip ||
                      id == EventMmioFlipImmediate;
        if (!isFrame) return;

        var pid = evt.ProcessID;
        if (pid <= 0) return;

        var now = evt.TimeStamp.Ticks;
        lock (Lock)
        {
            // dedupe: the Present_Stop and flip event for the same frame fire within
            // microseconds - count at most one frame per 1 ms per process.
            if (LastCounted.TryGetValue(pid, out var last) && now - last < TimeSpan.TicksPerMillisecond)
                return;
            LastCounted[pid] = now;

            if (!Frames.TryGetValue(pid, out var q))
            {
                q = new Queue<long>();
                Frames[pid] = q;
            }
            q.Enqueue(now);
            var cutoff = now - TimeSpan.TicksPerSecond * 2;
            while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
            if (q.Count > 1000)
            {
                while (q.Count > 500) q.Dequeue();
            }
        }
    }

    /// <summary>Frames presented by the process in the last second, or -1 when unknown.</summary>
    public static int GetFps(int processId)
    {
        if (!IsRunning || processId <= 0) return -1;
        lock (Lock)
        {
            if (!Frames.TryGetValue(processId, out var q) || q.Count == 0) return -1;
            var cutoff = DateTime.UtcNow.Ticks - TimeSpan.TicksPerSecond;
            var frames = 0;
            foreach (var t in q)
            {
                if (t >= cutoff) frames++;
            }
            while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
            return frames;
        }
    }

    private static void Log(string msg)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kicia");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(dir, "debug_state.log"),
                $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
        }
        catch
        {
            // never let logging break anything
        }
    }
}
