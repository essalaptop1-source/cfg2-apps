using System.Runtime.InteropServices;

namespace FPSBoosterApp.Services;

/// <summary>
/// Smooth screen capture via the DXGI Desktop Duplication API - the same
/// technique OBS uses. GPU-accelerated and independent of the desktop
/// composition rate, so games record fluidly (unlike GDI/gdigrab capture).
///
/// Captures one monitor; delivers BGRA32 frames on a background thread at
/// (up to) the requested frame rate, duplicating frames when nothing
/// changes so the output is constant frame rate.
/// </summary>
public sealed class DesktopDuplicator : IDisposable
{
    /// <summary>Called on the capture thread with a BGRA32 frame.</summary>
    public Action<byte[], int, int>? FrameReady;

    /// <summary>Monitor coordinates (physical pixels) being captured.</summary>
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public bool IsRunning { get; private set; }
    public long FramesCaptured { get; private set; }

    private readonly int _targetFps;
    private readonly Action<string> _log;
    private Thread? _thread;
    private volatile bool _stop;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private byte[] _lastFrame = Array.Empty<byte>();

    public DesktopDuplicator(int targetFps, Action<string> log)
    {
        _targetFps = Math.Clamp(targetFps, 1, 120);
        _log = log;
    }

    /// <summary>Starts capture of the monitor at the given physical coordinates.</summary>
    public bool Start(int x, int y, int width, int height)
    {
        if (IsRunning) return false;
        X = x; Y = y; Width = width; Height = height;
        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "DXGI-Capture" };
        _thread.Start();
        return true;
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(3000);
        Cleanup();
    }

    private void Loop()
    {
        while (!_stop)
        {
            try
            {
                if (InitDuplication())
                    CaptureLoop();
                else
                    return;
            }
            catch (Exception ex)
            {
                _log($"capture: {ex.Message}");
            }

            if (!_stop)
            {
                // DXGI_ERROR_ACCESS_LOST (mode change / GPU reset) - re-acquire.
                Cleanup();
                Thread.Sleep(500);
            }
        }
    }

    private bool InitDuplication()
    {
        try
        {
            if (!CreateD3D()) return false;

            var device = _device!;
            var dxgiDevice = (IDXGIDevice)device;
            var hrA = dxgiDevice.GetAdapter(out var adapter);
            if (hrA != 0)
            {
                _log($"capture: GetAdapter failed 0x{hrA:X8}");
                return false;
            }
            try
            {
                var descA = adapter.GetDesc(out var adapterDesc);
                _log($"capture: adapter desc hr=0x{descA:X8} desc0={adapterDesc.Description}");
                for (uint i = 0; ; i++)
                {
                    var hrE = adapter.EnumOutputs(i, out var outputPtr);
                    if (hrE != 0)
                    {
                        _log($"capture: EnumOutputs({i}) = 0x{hrE:X8}");
                        break;
                    }
                    try
                    {
                        // Wrap the raw pointer as IDXGIOutput by hand.
                        var iidOut = new Guid("ae02eedb-c735-4690-9697-52e5b64a2b0f");
                        var hrQI = Marshal.QueryInterface(outputPtr, ref iidOut, out var typedPtr);
                        if (hrQI != 0)
                        {
                            _log($"capture: QI IDXGIOutput on EnumOutputs result = 0x{hrQI:X8}");
                            return false;
                        }
                        var output = (IDXGIOutput)Marshal.GetObjectForIUnknown(typedPtr);
                        var hrD = output.GetDesc(out var desc);
                        if (hrD != 0) continue;
                        _log($"capture: output #{i} {desc.DeviceName} {desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left}x{desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top}");
                        if (desc.DesktopCoordinates.Left == X && desc.DesktopCoordinates.Top == Y)
                        {
                            var iidOut1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
                            var hrQI1 = Marshal.QueryInterface(typedPtr, ref iidOut1, out var out1Ptr);
                            if (hrQI1 != 0)
                            {
                                _log($"capture: QI IDXGIOutput1 failed 0x{hrQI1:X8}");
                                return false;
                            }
                            var output1 = (IDXGIOutput1)Marshal.GetObjectForIUnknown(out1Ptr);
                            var hr2 = output1.DuplicateOutput(device, out var dup);
                            if (hr2 != 0)
                            {
                                _log($"capture: DuplicateOutput failed 0x{hr2:X8}");
                                return false;
                            }
                            _duplication = dup;
                            return true;
                        }
                    }
                    finally
                    {
                        Marshal.Release(outputPtr);
                    }
                }
                _log("capture: no output matched the requested monitor");
                return false;
            }
            finally
            {
                Marshal.Release(Marshal.GetIUnknownForObject(adapter));
            }
        }
        catch (Exception ex)
        {
            _log($"capture: init failed: {ex.Message}");
            return false;
        }
    }

    private void CaptureLoop()
    {
        IsRunning = true;
        _log($"capture: started {Width}x{Height} @{_targetFps}fps");
        var timeout = (uint)Math.Max(4, 1000 / _targetFps);

        try
        {
            while (!_stop)
            {
                var hr = _duplication!.AcquireNextFrame(timeout, out var frameInfo, out var resource);
                if (hr == unchecked((int)0x887A0027)) // DXGI_ERROR_WAIT_TIMEOUT - nothing changed
                {
                    SendFrame(_lastFrame);
                    continue;
                }
                if (hr == unchecked((int)0x887A0026)) // DXGI_ERROR_ACCESS_LOST
                {
                    _log("capture: display changed - re-acquiring");
                    return;
                }
                if (hr != 0)
                {
                    _log($"capture: AcquireNextFrame failed 0x{hr:X8}");
                    return;
                }

                try
                {
                    if (CopySurface(resource, out var bgra))
                    {
                        _lastFrame = bgra;
                        SendFrame(bgra);
                    }
                }
                finally
                {
                    _duplication.ReleaseFrame();
                }
                _ = frameInfo;
            }
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void SendFrame(byte[] bgra)
    {
        if (_stop || bgra.Length == 0) return;
        FramesCaptured++;
        try
        {
            FrameReady?.Invoke(bgra, Width, Height);
        }
        catch { }
    }

    private bool CopySurface(IDXGIResource resource, out byte[] bgra)
    {
        bgra = Array.Empty<byte>();
        var surface = (IDXGISurface)resource;
        var hr = surface.Map(out var rect, 1 /*DXGI_MAP_READ*/);
        if (hr != 0)
        {
            _log($"capture: Map failed 0x{hr:X8}");
            return false;
        }
        try
        {
            var bytes = rect.Pitch * Height;
            var buf = new byte[bytes];
            if (rect.pBits != IntPtr.Zero)
                Marshal.Copy(rect.pBits, buf, 0, bytes);
            bgra = buf;
            return true;
        }
        finally
        {
            surface.Unmap();
        }
    }

    private bool CreateD3D()
    {
        var levels = new[] { 0xB000 /*11_0*/, 0xA100 /*10_1*/, 0xA000 /*10_0*/, 0x9300 /*9_3*/ };
        var hr = D3D11CreateDevice(IntPtr.Zero, 1 /*HARDWARE*/, IntPtr.Zero,
            0x20 /*BGRA_SUPPORT*/, levels, (uint)levels.Length, 7,
            out var devicePtr, out _, out var contextPtr);
        if (hr != 0)
        {
            _log($"capture: D3D11CreateDevice failed 0x{hr:X8}");
            return false;
        }
        _device = (ID3D11Device)Marshal.GetObjectForIUnknown(devicePtr);
        _context = (ID3D11DeviceContext)Marshal.GetObjectForIUnknown(contextPtr);
        Marshal.Release(devicePtr);
        Marshal.Release(contextPtr);
        return true;
    }

    private void Cleanup()
    {
        if (_duplication != null)
        {
            try { _duplication.ReleaseFrame(); } catch { }
            try { Marshal.Release(Marshal.GetIUnknownForObject(_duplication)); } catch { }
            _duplication = null;
        }
        if (_context != null)
        {
            try { Marshal.Release(Marshal.GetIUnknownForObject(_context)); } catch { }
            _context = null;
        }
        if (_device != null)
        {
            try { Marshal.Release(Marshal.GetIUnknownForObject(_device)); } catch { }
            _device = null;
        }
    }

    public void Dispose() => Stop();

    // ================================================================ native

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr pAdapter, int driverType, IntPtr software,
        uint flags, [In] int[] featureLevels, uint numLevels, uint sdkVersion,
        out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public IntPtr DedicatedVideoMemory;
        public IntPtr DedicatedSystemMemory;
        public IntPtr SharedSystemMemory;
        public long AdapterLuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public RECT DesktopCoordinates;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_MAPPED_RECT { public int Pitch; public IntPtr pBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public int PointerX, PointerY, PointerVisible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    // ---------------------------------------------------------------- COM interfaces

    [ComImport, Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIDevice
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid iid, out IntPtr parent);
        [PreserveSig] int GetAdapter(out IDXGIAdapter adapter);
        [PreserveSig] int CreateSurface(IntPtr desc, uint numSurfaces, uint usage, IntPtr sharedResource, out IntPtr surface);
        [PreserveSig] int QueryResourceResidency(IntPtr resources, out int residency, uint numResources);
        [PreserveSig] int SetGPUThreadPriority(int priority);
        [PreserveSig] int GetGPUThreadPriority(out int priority);
    }

    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid iid, out IntPtr parent);
        [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
        [PreserveSig] int GetDesc(out DXGI_ADAPTER_DESC desc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid iid, out long userModeDriverVersion);
    }

    [ComImport, Guid("ae02eedb-c735-4690-9697-52e5b64a2b0f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIOutput
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid iid, out IntPtr parent);
        [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
        [PreserveSig] int GetDisplayModeList(int format, uint flags, ref uint numModes, IntPtr modes);
        [PreserveSig] int FindClosestMatchingMode(IntPtr modeToMatch, out IntPtr closestMatch, IntPtr concernedDevice);
        [PreserveSig] int WaitForVBlank();
        [PreserveSig] int TakeOwnership(IntPtr device, int exclusive);
        [PreserveSig] void ReleaseOwnership();
        [PreserveSig] int GetGammaControlCapabilities(IntPtr caps);
        [PreserveSig] int SetGammaControl(IntPtr array);
        [PreserveSig] int GetGammaControl(IntPtr array);
        [PreserveSig] int SetDisplaySurface(IntPtr surface);
        [PreserveSig] int GetDisplaySurfaceData(IntPtr surface);
        [PreserveSig] int GetFrameStatistics(IntPtr stats);
    }

    [ComImport, Guid("00cddea8-939b-4b83-a340-a685226666cc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIOutput1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid iid, out IntPtr parent);
        [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
        [PreserveSig] int GetDisplayModeList(int format, uint flags, ref uint numModes, IntPtr modes);
        [PreserveSig] int FindClosestMatchingMode(IntPtr modeToMatch, out IntPtr closestMatch, IntPtr concernedDevice);
        [PreserveSig] int WaitForVBlank();
        [PreserveSig] int TakeOwnership(IntPtr device, int exclusive);
        [PreserveSig] void ReleaseOwnership();
        [PreserveSig] int GetGammaControlCapabilities(IntPtr caps);
        [PreserveSig] int SetGammaControl(IntPtr array);
        [PreserveSig] int GetGammaControl(IntPtr array);
        [PreserveSig] int SetDisplaySurface(IntPtr surface);
        [PreserveSig] int GetDisplaySurfaceData(IntPtr surface);
        [PreserveSig] int GetFrameStatistics(IntPtr stats);
        [PreserveSig] int GetDisplayModeList1(int format, uint flags, ref uint numModes, IntPtr modes);
        [PreserveSig] int FindClosestMatchingMode1(IntPtr modeToMatch, out IntPtr closestMatch, IntPtr concernedDevice);
        [PreserveSig] int GetDisplaySurfaceData1(IntPtr surface);
        [PreserveSig] int DuplicateOutput(ID3D11Device device, out IDXGIOutputDuplication outputDuplication);
    }

    [ComImport, Guid("191cfac3-a341-470d-b26e-a864f428319c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIOutputDuplication
    {
        [PreserveSig] int GetDesc(out IntPtr desc);
        [PreserveSig] int AcquireNextFrame(uint timeoutInMilliseconds, out DXGI_OUTDUPL_FRAME_INFO frameInfo, out IDXGIResource desktopResource);
        [PreserveSig] int GetFrameDirtyRects(uint dirtyRectsBufferSize, out RECT dirtyRectsBuffer, out uint dirtyRectsBufferSizeRequired);
        [PreserveSig] int GetFrameMoveRects(uint moveRectsBufferSize, IntPtr moveRectBuffer, out uint moveRectsBufferSizeRequired);
        [PreserveSig] int GetFramePointerShape(uint pointerShapeBufferSize, IntPtr pointerShapeBuffer, out uint pointerShapeBufferSizeRequired, out IntPtr pointerShapeInfo);
        [PreserveSig] int MapDesktopSurface(out DXGI_MAPPED_RECT lockedRect);
        [PreserveSig] int UnMapDesktopSurface();
        [PreserveSig] int ReleaseFrame();
    }

    [ComImport, Guid("0359e30e-03e4-4f5a-9b4d-17a0d677dc7e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIResource
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid iid, out IntPtr parent);
        [PreserveSig] int GetDevice(ref Guid iid, out IntPtr device);
        [PreserveSig] int GetSharedHandle(out IntPtr sharedHandle);
        [PreserveSig] int GetUsage(out uint usage);
        [PreserveSig] int SetEvictionPriority(uint evictionPriority);
        [PreserveSig] int GetEvictionPriority(out uint evictionPriority);
    }

    [ComImport, Guid("cafcb56c-6ef3-4701-aa10-93efbfca4b3a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGISurface
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid iid, out IntPtr parent);
        [PreserveSig] int GetDevice(ref Guid iid, out IntPtr device);
        [PreserveSig] int GetDesc(out IntPtr desc);
        [PreserveSig] int Map(out DXGI_MAPPED_RECT lockedRect, uint mapFlags);
        [PreserveSig] int Unmap();
    }

    // D3D11 device/context - we only hold IUnknown references.
    [ComImport, Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11Device { }
    [ComImport, Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11DeviceContext { }
}
