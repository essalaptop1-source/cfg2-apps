using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;

namespace FPSBoosterApp.Services;

/// <summary>
/// Screen recording engine built on ffmpeg (ships next to the exe).
///
/// Capture:
///   - gdigrab (monitor region or window) - always works
///   - app-fed rawvideo (e.g. DXGI desktop duplication) when enabled
/// Audio: system sound via the WASAPI loopback pipe and/or mic via dshow.
/// Encode: hardware (h264_qsv/nvenc/amf) when available, libx264 fallback.
/// Container: records to MPEG-TS first (crash-safe), then remuxes to a
///            fast-start MP4 on stop - so an interrupted recording is never lost.
/// </summary>
public sealed class RecorderService : IDisposable
{
    public bool IsRecording { get; private set; }
    public string? CurrentFile { get; private set; }

    /// <summary>fps, seconds, output file (null when finalize failed).</summary>
    public event Action<int, double, string?>? Progress;
    public event Action<string>? Log;

    private Process? _ffmpeg;
    private LoopbackCapture? _loopback;
    private AudioGate? _audioGate;
    private Thread? _audioThread;
    private NamedPipeServerStream? _audioPipe;
    private Thread? _pipeWaiter;
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();

    public string FfmpegPath { get; }
    public string? PendingFinalPath { get; private set; }

    public RecorderService()
    {
        FfmpegPath = AppPaths.Combine("ffmpeg.exe");
    }

    public bool IsFfmpegAvailable => File.Exists(FfmpegPath);

    /// <summary>Writes BGRA32 frames to ffmpeg's stdin when CaptureFrames is on.</summary>
    public Stream? FrameInput { get; private set; }

    public sealed class Options
    {
        public int X, Y, Width, Height;      // monitor region
        public string? WindowTitle;          // window capture wins over region
        public bool SystemAudio;
        public string? MicDevice;
        public int Fps = 30;
        public bool Watermark;
        public string OutputPath = "";       // final .mp4 path
        public string Encoder = "auto";      // auto / h264_qsv / libx264 / ...
        public int BitrateKbps = 8000;       // hw encoders
        public int Crf = 20;                 // libx264
        public bool CaptureFrames;           // app feeds rawvideo on stdin
        public int CropX, CropY, CropW, CropH; // 0,0,0,0 = no crop
        public int OutW, OutH;               // 0,0 = keep source size
        public bool Fit = true;              // false = stretch/distort
    }

    public bool Start(Options o)
    {
        lock (_lock)
        {
            if (IsRecording) return false;
            if (!IsFfmpegAvailable)
            {
                Log?.Invoke("ffmpeg.exe not found next to the app.");
                return false;
            }

            try
            {
                var tsPath = Path.ChangeExtension(o.OutputPath, ".ts");
                PendingFinalPath = o.OutputPath;
                CurrentFile = tsPath;

                // Named pipe for system audio: the app writes PCM to it and
                // ffmpeg reads it as a file - keeps stdin free for the
                // graceful 'q' quit (and rawvideo capture when used).
                var pipeName = "cfg2audio_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..8];
                _audioPipe = new NamedPipeServerStream(
                    pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                _pipeWaiter = new Thread(() =>
                {
                    try { _audioPipe!.WaitForConnection(); } catch { }
                }) { IsBackground = true, Name = "AudioPipeWaiter" };
                _pipeWaiter.Start();

                var args = BuildArgs(o, tsPath, pipeName);

                var psi = new ProcessStartInfo
                {
                    FileName = FfmpegPath,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                _ffmpeg = Process.Start(psi);
                if (_ffmpeg == null)
                {
                    Log?.Invoke("Failed to start ffmpeg.");
                    return false;
                }

                _cts = new CancellationTokenSource();

                // System audio -> named pipe (s16le 48k stereo). The gate
                // feeds silence if the loopback ever stalls.
                if (o.SystemAudio)
                {
                    _audioGate = new AudioGate(_audioPipe) { IsConnected = () => _audioPipe!.IsConnected };
                    _audioGate.Start();
                    _loopback = new LoopbackCapture(_audioGate, m => Log?.Invoke(m));
                    _audioThread = new Thread(() => _loopback.Start()) { IsBackground = true, Name = "AudioCapture" };
                    _audioThread.Start();
                }

                if (o.CaptureFrames)
                    FrameInput = _ffmpeg.StandardInput.BaseStream;

                // Progress + errors.
                _ = Task.Run(() => ReadProgress(_ffmpeg, _cts.Token));
                _ = Task.Run(() => ReadErrors(_ffmpeg, _cts.Token));

                IsRecording = true;
                Log?.Invoke($"recording started: {Path.GetFileName(tsPath)} (enc={o.Encoder})");
                return true;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"start failed: {ex.Message}");
                return false;
            }
        }
    }

    private List<string> BuildArgs(Options o, string tsPath, string pipeName)
    {
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "warning", "-nostats" };

        // ---- video input ----
        if (o.CaptureFrames)
        {
            args.AddRange(new[]
            {
                "-f", "rawvideo", "-pix_fmt", "bgra",
                "-video_size", $"{o.Width}x{o.Height}",
                "-framerate", o.Fps.ToString(),
                "-i", "pipe:0",
            });
        }
        else if (!string.IsNullOrEmpty(o.WindowTitle))
        {
            args.AddRange(new[] { "-f", "gdigrab", "-framerate", o.Fps.ToString(), "-i", "title=" + o.WindowTitle });
        }
        else
        {
            args.AddRange(new[]
            {
                "-f", "gdigrab", "-framerate", o.Fps.ToString(),
                "-offset_x", o.X.ToString(), "-offset_y", o.Y.ToString(),
                "-video_size", $"{o.Width}x{o.Height}",
                "-i", "desktop",
            });
        }

        // ---- audio inputs ----
        var audioTags = new List<string>();
        if (o.SystemAudio)
        {
            args.AddRange(new[] { "-f", "s16le", "-ar", "48000", "-ac", "2", "-i", @"\\.\pipe\" + pipeName });
            audioTags.Add("pipe");
        }
        if (!string.IsNullOrEmpty(o.MicDevice))
        {
            args.AddRange(new[] { "-f", "dshow", "-i", "audio=" + o.MicDevice });
            audioTags.Add("mic");
        }

        // ---- video filter chain: crop -> scale -> watermark -> format ----
        var chain = new List<string>();
        if (o.CropW > 0 && o.CropH > 0)
        {
            var cw = Math.Min(o.CropW, o.Width);
            var ch = Math.Min(o.CropH, o.Height);
            var cx = Math.Clamp(o.CropX, 0, Math.Max(0, o.Width - cw));
            var cy = Math.Clamp(o.CropY, 0, Math.Max(0, o.Height - ch));
            chain.Add($"crop={cw}:{ch}:{cx}:{cy}");
        }
        if (o.OutW > 0 && o.OutH > 0)
        {
            if (o.Fit)
            {
                chain.Add($"scale={o.OutW}:{o.OutH}:force_original_aspect_ratio=decrease:flags=lanczos");
                chain.Add($"pad={o.OutW}:{o.OutH}:(ow-iw)/2:(oh-ih)/2:color=black");
            }
            else
            {
                chain.Add($"scale={o.OutW}:{o.OutH}:flags=lanczos");
            }
        }
        if (o.Watermark)
        {
            chain.Add("drawtext=fontfile='C\\:/Windows/Fonts/segoeui.ttf':" +
                      "text='CFG2 Recorder':fontcolor=white@0.45:fontsize=22:" +
                      "x=w-tw-24:y=h-th-24");
        }
        chain.Add("format=" + (o.Encoder == "h264_qsv" ? "nv12" : "yuv420p"));
        // Force constant frame rate: capture (gdigrab/DXGI) can deliver frames
        // in bursts, which otherwise produces non-monotonic timestamps and
        // stuttery playback. The fps filter evens the timeline out.
        chain.Add($"fps={o.Fps}");

        var fc = new List<string> { $"[0:v]{string.Join(",", chain)}[vout]" };
        var hasAudio = audioTags.Count > 0;
        if (audioTags.Count == 1)
        {
            fc.Add($"[{audioTags.Count}]aresample=48000[aout]");
        }
        else if (audioTags.Count == 2)
        {
            fc.Add("[1:a]aresample=48000[sa];[2:a]aresample=48000[ma];" +
                   "[sa][ma]amix=inputs=2:normalize=0[aout]");
        }

        args.Add("-filter_complex");
        args.Add(string.Join(";", fc));
        args.AddRange(new[] { "-map", "[vout]" });
        if (hasAudio) args.AddRange(new[] { "-map", "[aout]" });

        // ---- encode ----
        AppendEncoder(args, o.Encoder, o.BitrateKbps, o.Crf);
        if (hasAudio) args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
        args.AddRange(new[] { "-f", "mpegts", "-flush_packets", "1", "-progress", "pipe:1", tsPath });

        return args;
    }

    private static void AppendEncoder(List<string> args, string encoder, int bitrateKbps, int crf)
    {
        switch (encoder)
        {
            case "h264_qsv":
                // ICQ mode = constant quality (like CRF): adaptive bitrate, so
                // static scenes stay small and game scenes keep their detail.
                args.AddRange(new[] { "-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", crf.ToString() });
                break;
            case "h264_nvenc":
                args.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", (crf + 5).ToString(), "-b:v", $"{bitrateKbps}k" });
                break;
            case "h264_amf":
                args.AddRange(new[] { "-c:v", "h264_amf", "-usage", "transcoding", "-quality", "quality", "-b:v", $"{bitrateKbps}k" });
                break;
            default:
                args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", crf.ToString() });
                break;
        }
    }

    /// <summary>Stops the capture and remuxes the TS into the final MP4.</summary>
    public async Task<string?> StopAsync()
    {
        Process? p;
        LoopbackCapture? lb;
        string? ts;
        lock (_lock)
        {
            if (!IsRecording) return null;
            p = _ffmpeg;
            lb = _loopback;
            ts = CurrentFile;
            IsRecording = false;
        }

        try
        {
            lb?.Stop();
            _cts?.Cancel();
            lb?.Dispose();
            _audioGate?.Stop();
            _audioPipe?.Dispose();
        }
        catch { }

        try
        {
            if (p != null && !p.HasExited)
            {
                try
                {
                    p.StandardInput.Write('q');
                    p.StandardInput.Flush();
                }
                catch { }
                if (!p.WaitForExit(8000))
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                }
            }
        }
        catch { }

        FrameInput = null;

        // Remux TS -> MP4 (fast, stream copy).
        var final = PendingFinalPath ?? "";
        if (string.IsNullOrEmpty(ts) || !File.Exists(ts))
        {
            Log?.Invoke("stop: no capture file to finalize");
            return null;
        }

        var ok = await Task.Run(() => Remux(ts, final));
        if (ok)
        {
            try { File.Delete(ts); } catch { }
            Log?.Invoke($"recording saved: {Path.GetFileName(final)}");
            return final;
        }

        // Remux failed - keep the TS so nothing is lost.
        Log?.Invoke($"finalize failed - kept {Path.GetFileName(ts)}");
        return null;
    }

    private bool Remux(string ts, string mp4)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = AppPaths.Combine("ffmpeg.exe"),
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-err_detect");
            psi.ArgumentList.Add("ignore_err");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(ts);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("copy");
            psi.ArgumentList.Add("-movflags");
            psi.ArgumentList.Add("+faststart");
            psi.ArgumentList.Add(mp4);

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(60_000);
            return proc.ExitCode == 0 && File.Exists(mp4) && new FileInfo(mp4).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static readonly Regex FpsRx = new(@"^fps=([\d.]+)$", RegexOptions.Compiled);
    private static readonly Regex TimeRx = new(@"^out_time_us=(\d+)$", RegexOptions.Compiled);

    private void ReadProgress(Process p, CancellationToken ct)
    {
        try
        {
            int fps = 0;
            double seconds = 0;
            using var reader = p.StandardOutput;
            while (!ct.IsCancellationRequested)
            {
                var line = reader.ReadLine();
                if (line == null) break;
                var m = FpsRx.Match(line);
                if (m.Success && double.TryParse(m.Groups[1].Value, out var f) && f > 0)
                    fps = (int)Math.Round(f);
                m = TimeRx.Match(line);
                if (m.Success && long.TryParse(m.Groups[1].Value, out var us))
                    seconds = us / 1_000_000.0;
                if (line == "progress=continue" || line == "progress=end")
                    Progress?.Invoke(fps, seconds, CurrentFile);
            }
        }
        catch { }
    }

    private void ReadErrors(Process p, CancellationToken ct)
    {
        try
        {
            using var reader = p.StandardError;
            var sb = new StringBuilder();
            while (!ct.IsCancellationRequested)
            {
                var line = reader.ReadLine();
                if (line == null) break;
                sb.AppendLine(line);
                if (sb.Length > 4000) sb.Clear();
            }
            if (sb.Length > 0) Log?.Invoke(sb.ToString().Trim());
        }
        catch { }
    }

    public void Dispose()
    {
        try
        {
            if (IsRecording) StopAsync().GetAwaiter().GetResult();
        }
        catch { }
    }
}

/// <summary>
/// Detects the best available H.264 encoder by asking ffmpeg.
/// </summary>
public static class EncoderDetect
{
    public static readonly string[] Preference = { "h264_qsv", "h264_nvenc", "h264_amf", "libx264" };

    /// <summary>Returns the first available encoder from Preference (cached).</summary>
    public static string BestEncoder()
    {
        var cached = _cached;
        if (cached != null) return cached;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = AppPaths.Combine("ffmpeg.exe"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-encoders");
            using var p = Process.Start(psi);
            if (p == null) return "libx264";
            var outText = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            foreach (var enc in Preference)
                if (outText.Contains(enc))
                {
                    _cached = enc;
                    return enc;
                }
        }
        catch { }
        return "libx264";
    }

    private static string? _cached;
}

/// <summary>
/// A Stream that queues PCM chunks for a pump thread writing to ffmpeg's
/// audio pipe. When the producer (WASAPI loopback) delivers nothing, the
/// pump writes digital silence instead - so ffmpeg always gets a steady
/// audio stream and never blocks waiting for frames.
/// </summary>
public sealed class AudioGate : Stream
{
    private readonly Stream _sink;
    private readonly Queue<byte[]> _q = new();
    private readonly object _lock = new();
    private bool _done;
    private Thread? _pump;

    public AudioGate(Stream sink) => _sink = sink;

    public override void Write(byte[] buffer, int offset, int count)
    {
        var copy = new byte[count];
        Buffer.BlockCopy(buffer, offset, copy, 0, count);
        lock (_lock)
        {
            if (_done) return;
            if (_q.Count > 16) _q.Clear();
            _q.Enqueue(copy);
        }
    }

    public void Start()
    {
        _pump = new Thread(Pump) { IsBackground = true, Name = "AudioGate" };
        _pump.Start();
    }

    /// <summary>Set when the underlying sink (named pipe) is connected.</summary>
    public Func<bool>? IsConnected { get; set; }

    public void Stop()
    {
        lock (_lock) _done = true;
        _pump?.Join(600);
        try { _sink.Flush(); } catch { }
    }

    private void Pump()
    {
        var silence = new byte[48000 * 2 * 2 / 10]; // 100ms of 48k stereo s16
        while (true)
        {
            byte[]? chunk;
            lock (_lock)
            {
                if (_done && _q.Count == 0) return;
                chunk = _q.Count > 0 ? _q.Dequeue() : null;
            }
            try
            {
                if (IsConnected != null && !IsConnected())
                {
                    Thread.Sleep(25);
                    continue;
                }
                if (chunk != null)
                {
                    _sink.Write(chunk, 0, chunk.Length);
                }
                else
                {
                    _sink.Write(silence, 0, silence.Length);
                    Thread.Sleep(100);
                }
            }
            catch
            {
                return; // pipe closed
            }
        }
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
