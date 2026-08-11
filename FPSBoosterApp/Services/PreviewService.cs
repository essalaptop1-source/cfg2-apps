using System.Diagnostics;
using System.IO;

namespace FPSBoosterApp.Services;

/// <summary>
/// Lightweight live preview: runs a second small ffmpeg that captures the
/// selected monitor with gdigrab, scales it down, and streams BGRA frames
/// on stdout which the app reads into a WriteableBitmap.
/// </summary>
public sealed class PreviewService : IDisposable
{
    public event Action<byte[], int, int>? Frame; // BGRA, width, height (reader thread)
    public event Action<string>? Log;

    public bool IsRunning { get; private set; }
    public int TargetWidth { get; private set; } = 480;
    public int Fps { get; private set; } = 15;

    private Process? _proc;
    private Thread? _reader;
    private volatile bool _stop;
    private int _x, _y, _w, _h;
    private int _scaledW, _scaledH;

    public bool Start(int x, int y, int width, int height)
    {
        Stop();
        _x = x; _y = y; _w = width; _h = height;

        // Even target height so yuv->bgra rows stay aligned.
        var th = (int)Math.Round(height * TargetWidth / (double)width);
        if (th % 2 != 0) th++;
        _scaledW = TargetWidth;
        _scaledH = th;

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
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("gdigrab");
            psi.ArgumentList.Add("-framerate");
            psi.ArgumentList.Add(Fps.ToString());
            psi.ArgumentList.Add("-offset_x");
            psi.ArgumentList.Add(_x.ToString());
            psi.ArgumentList.Add("-offset_y");
            psi.ArgumentList.Add(_y.ToString());
            psi.ArgumentList.Add("-video_size");
            psi.ArgumentList.Add($"{_w}x{_h}");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("desktop");
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add($"scale={_scaledW}:{_scaledH},fps={Fps},format=bgra");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("rawvideo");
            psi.ArgumentList.Add("pipe:1");

            _proc = Process.Start(psi);
            if (_proc == null) return false;
            _stop = false;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "PreviewReader" };
            _reader.Start();
            IsRunning = true;
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"preview: {ex.Message}");
            return false;
        }
    }

    private void ReadLoop()
    {
        var frameBytes = _scaledW * _scaledH * 4;
        var buf = new byte[frameBytes];
        try
        {
            using var stdout = _proc!.StandardOutput.BaseStream;
            while (!_stop)
            {
                var read = 0;
                while (read < frameBytes)
                {
                    var n = stdout.Read(buf, read, frameBytes - read);
                    if (n <= 0) return;
                    read += n;
                }
                var copy = new byte[frameBytes];
                Buffer.BlockCopy(buf, 0, copy, 0, frameBytes);
                try
                {
                    Frame?.Invoke(copy, _scaledW, _scaledH);
                }
                catch { }
            }
        }
        catch { }
    }

    public void Stop()
    {
        _stop = true;
        try
        {
            if (_proc != null && !_proc.HasExited)
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(2000);
            }
        }
        catch { }
        _reader?.Join(500);
        _proc?.Dispose();
        _proc = null;
        IsRunning = false;
    }

    public void Dispose() => Stop();
}
