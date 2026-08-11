using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FPSBoosterApp.Services;

/// <summary>
/// Captures the system's output audio (what you hear - games, music, Discord)
/// via the WASAPI loopback API and streams it as 16-bit PCM to a Stream
/// (the ffmpeg input pipe). Purely native interop, no packages.
/// </summary>
public sealed class LoopbackCapture : IDisposable
{
    private readonly Stream _output;
    private readonly Action<string> _log;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _ready;
    public long FramesCaptured { get; private set; }
    public int SampleRate { get; private set; } = 48000;
    public int Channels { get; private set; } = 2;

    public LoopbackCapture(Stream output, Action<string> log)
    {
        _output = output;
        _log = log;
    }

    /// <summary>Starts capture and waits up to `timeoutMs` for the WASAPI stream to come up.</summary>
    /// <returns>true when the loopback stream is delivering; false if it failed (caller should fall back to video-only).</returns>
    public bool Start(int timeoutMs = 4000)
    {
        _running = true;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "WASAPI-Loopback" };
        _thread.Start();

        var deadline = Environment.TickCount64 + timeoutMs;
        while (!_ready && _thread.IsAlive && Environment.TickCount64 < deadline)
            Thread.Sleep(25);

        if (!_ready)
        {
            _log($"loopback: not ready after {timeoutMs}ms - recording without system audio");
            _running = false;
            _thread.Join(500);
            return false;
        }
        return true;
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(1500);
    }

    private void CaptureLoop()
    {
        // GetService can transiently fail with 0x88890003 on some Windows
        // builds - retry the whole init a few times before giving up.
        for (var attempt = 1; attempt <= 3 && _running; attempt++)
        {
            if (TryStartStream(out var client, out var capture, out var rate, out var ch, out var isFloat))
            {
                _ready = true;
                try
                {
                    var buf = new byte[rate * ch * 4]; // max float32 frame
                    while (_running)
                    {
                        uint packets;
                        while (_running && capture.GetNextPacketSize(out packets) == 0 && packets > 0)
                        {
                            uint frames;
                            int flags;
                            ulong devPos, qpc;
                            var hr = capture.GetBuffer(out var data, out frames, out flags, out devPos, out qpc);
                            if (hr != 0) break;
                            if (frames > 0)
                            {
                                var bytes = (int)(frames * ch * (isFloat ? 4 : 2));
                                Marshal.Copy(data, buf, 0, Math.Min(bytes, buf.Length));
                                if (isFloat)
                                    WritePcm16(buf, (int)frames, ch, _output);
                                else
                                    _output.Write(buf, 0, (int)frames * ch * 2);
                                FramesCaptured += frames;
                            }
                            capture.ReleaseBuffer(frames);
                        }
                        Thread.Sleep(10);
                    }
                    try { client.Stop(); client.Reset(); } catch { }
                }
                finally
                {
                    _ready = false;
                }
                return;
            }

            if (_running && attempt < 3)
            {
                _log($"loopback: retry {attempt + 1}/3");
                Thread.Sleep(400);
            }
        }
        _ready = false;
    }

    private bool TryStartStream(out IAudioClient? client, out IAudioCaptureClient? capture,
        out int rate, out int ch, out bool isFloat)
    {
        client = null;
        capture = null;
        rate = 48000;
        ch = 2;
        isFloat = false;
        try
        {
            _ = ComInitialize();
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            IMMDevice? device = null;
            var hr = enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out device);
            if (hr != 0 || device == null) { _log($"loopback: no render endpoint (0x{hr:X8})"); return false; }

            var iidClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
            hr = device.Activate(ref iidClient, 0x17 /*CLSCTX_ALL*/, IntPtr.Zero, out var clientPtr);
            if (hr != 0) { _log($"loopback: Activate failed 0x{hr:X8}"); return false; }
            client = (IAudioClient)Marshal.GetObjectForIUnknown(clientPtr);

            hr = client.GetMixFormat(out var fmtPtr);
            if (hr != 0) { _log($"loopback: GetMixFormat failed 0x{hr:X8}"); return false; }
            ParseFormat(fmtPtr, out rate, out ch, out isFloat);

            const int shared = 0;
            const int loopback = 0x80000;
            hr = client.Initialize(shared, loopback, 10000000 /*100ms*/, 0, fmtPtr, IntPtr.Zero);
            if (hr != 0) { _log($"loopback: Initialize failed 0x{hr:X8}"); return false; }

            var iidCapture = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
            hr = client.GetService(ref iidCapture, out var capturePtr);
            if (hr != 0) { _log($"loopback: GetService failed 0x{hr:X8}"); return false; }
            capture = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capturePtr);

            SampleRate = rate;
            Channels = ch;
            hr = client.Start();
            if (hr != 0) { _log($"loopback: Start failed 0x{hr:X8}"); return false; }
            _log($"loopback: started {rate}Hz {ch}ch float={isFloat}");
            return true;
        }
        catch (Exception ex)
        {
            _log($"loopback: error {ex.GetType().Name} 0x{ex.HResult:X8} at {ex.TargetSite?.Name} - {ex.Message}");
            return false;
        }
    }

    private static int ComInitialize()
    {
        try
        {
            return CoInitializeEx(IntPtr.Zero, 0x0 /*MTA*/);
        }
        catch
        {
            return -1;
        }
    }

    private static void ParseFormat(IntPtr fmt, out int rate, out int ch, out bool isFloat)
    {
        var tag = (ushort)Marshal.ReadInt16(fmt, 0);
        ch = Marshal.ReadInt16(fmt, 2);
        rate = Marshal.ReadInt32(fmt, 4);
        var bits = (ushort)Marshal.ReadInt16(fmt, 14);
        isFloat = tag == 0xFFFE /*EXTENSIBLE*/
            ? ReadSubFormatIsFloat(fmt)
            : tag == 3 /*IEEE_FLOAT*/ || (tag == 0 && bits == 32);
    }

    private static bool ReadSubFormatIsFloat(IntPtr fmt)
    {
        // WAVEFORMATEXTENSIBLE: cbSize @16, wValidBitsPerSample @18, dwChannelMask @20, SubFormat GUID @24
        var b0 = Marshal.ReadInt32(fmt, 24);
        var b1 = Marshal.ReadInt16(fmt, 28);
        var b2 = Marshal.ReadInt16(fmt, 30);
        var b3 = Marshal.ReadByte(fmt, 32);
        var b4 = Marshal.ReadByte(fmt, 33);
        var b5 = Marshal.ReadByte(fmt, 34);
        var b6 = Marshal.ReadByte(fmt, 35);
        var b7 = Marshal.ReadByte(fmt, 36);
        var b8 = Marshal.ReadByte(fmt, 37);
        var b9 = Marshal.ReadByte(fmt, 38);
        var b10 = Marshal.ReadByte(fmt, 39);
        var b11 = Marshal.ReadByte(fmt, 40);
        // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = 00000003-0000-0010-8000-00aa00389b71
        return b0 == 0x3 && b1 == 0 && b2 == 0x10 && b3 == 0x80 && b4 == 0x00 && b5 == 0x00 &&
               b6 == 0xaa && b7 == 0x00 && b8 == 0x38 && b9 == 0x9b && b10 == 0x71;
    }

    /// <summary>float32 interleaved -> int16 interleaved (little endian) to the stream.</summary>
    private static void WritePcm16(byte[] floatBuf, int frames, int ch, Stream output)
    {
        var pcm = new byte[frames * ch * 2];
        for (var i = 0; i < frames * ch; i++)
        {
            var f = BitConverter.ToSingle(floatBuf, i * 4);
            if (f > 1f) f = 1f;
            else if (f < -1f) f = -1f;
            var s = (short)(f * 32767f);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        output.Write(pcm, 0, pcm.Length);
    }

    public void Dispose()
    {
        Stop();
        try { _output.Flush(); } catch { }
    }

    [DllImport("ole32.dll", EntryPoint = "CoInitializeEx")]
    private static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

    // ================================================================ COM

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice(string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out int state);
    }

    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr waveFormat, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint numFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint numFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr waveFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, out IntPtr service);
    }

    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFrames, out int flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFrames);
        [PreserveSig] int GetNextPacketSize(out uint numFrames);
    }
}
