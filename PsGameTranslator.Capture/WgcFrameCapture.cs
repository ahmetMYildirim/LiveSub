using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace PsGameTranslator.Capture;

/// <summary>
/// Captures window frames using Windows.Graphics.Capture (WGC) instead of the
/// older PrintWindow/GDI approach. PrintWindow relies on the target application
/// cooperating with WM_PRINT, which most DirectX-rendered fullscreen/borderless
/// games do not do correctly — the result is a stale or heavily blurred cached
/// frame (a well documented PrintWindow limitation). WGC instead reads the real
/// DWM-composited surface for the window, which is always correct regardless of
/// the app's rendering backend.
///
/// A capture session (device + item + frame pool) is expensive to set up and is
/// meant to be reused across many frames, not recreated per call — this class
/// caches one per window handle and keeps pulling frames from the same pool.
/// </summary>
internal static class WgcFrameCapture
{
    private static readonly Guid GraphicsCaptureItemInteropGuid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid DxgiInterfaceAccessGuid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IInspectableGuid = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
    private static readonly Guid D3D11Texture2DGuid = typeof(ID3D11Texture2D).GUID;

    private static readonly ConcurrentDictionary<nint, CaptureSession> Sessions = new();

    public static bool IsSupported() => GraphicsCaptureSession.IsSupported();

    /// <summary>Captures the latest frame of the given window and returns it as PNG bytes.
    /// Throws if the window cannot be captured (closed, minimized, WGC unavailable, etc.) —
    /// callers should fall back to PrintWindow on failure.</summary>
    public static byte[] CaptureWindowJpeg(nint hwnd)
    {
        var session = Sessions.GetOrAdd(hwnd, static h => CaptureSession.Create(h));
        try
        {
            return session.CaptureFrameJpeg();
        }
        catch
        {
            // The session may be tied to a now-closed/invalid window (e.g. the
            // captured process exited) — drop it so the next call builds a
            // fresh one instead of repeatedly failing against a dead session.
            if (Sessions.TryRemove(hwnd, out var stale)) stale.Dispose();
            throw;
        }
    }

    /// <summary>Releases every cached capture session. Call on app shutdown.</summary>
    public static void ReleaseAll()
    {
        foreach (var key in Sessions.Keys.ToArray())
            if (Sessions.TryRemove(key, out var session)) session.Dispose();
    }

    private sealed class CaptureSession : IDisposable
    {
        private readonly GraphicsCaptureItem _item;
        private readonly ID3D11Device _d3dDevice;
        private readonly IDXGIDevice _dxgiDevice;
        private readonly IDirect3DDevice _winrtDevice;
        private readonly Direct3D11CaptureFramePool _framePool;
        private readonly GraphicsCaptureSession _session;
        // The immediate context is a single persistent object per device, not
        // something to fetch fresh each frame — re-acquiring and disposing it
        // on every capture caused Vortice's cached wrapper for that same native
        // pointer to come back already-disposed on the next call, surfacing as
        // an intermittent NullReferenceException inside CopyResource.
        private readonly ID3D11DeviceContext _context;
        private volatile bool _closed;
        // A session is cached per window handle and reused across every caller —
        // the main OCR polling loop *and* one-off callers (e.g. the vision-based
        // game identifier grabbing its own screenshot of the same window) can
        // both land on the same session concurrently. TryGetNextFrame()/the
        // shared ID3D11DeviceContext aren't safe for concurrent access from two
        // threads, so a race here intermittently failed WGC capture entirely
        // (falling back to the slower/blurrier PrintWindow) right at the moment
        // a second caller's capture overlapped the polling loop's. Serializing
        // access here costs nothing when there's no contention.
        private readonly object _captureLock = new();
        // The size the frame pool's buffers were allocated at. The pool does NOT
        // follow the window: once the captured window resizes (Remote Play going
        // fullscreen, a resolution/scale change), a pool still sized for the old
        // window keeps handing back old-sized buffers and the new, larger content
        // arrives cropped to its top-left corner — which is why capture "only
        // grabbed the top-left" and the subtitle region fell outside the frame.
        // Tracked here so CaptureFrameJpeg can recreate the pool on a size change.
        private SizeInt32 _poolSize;

        private CaptureSession(
            GraphicsCaptureItem item, ID3D11Device d3dDevice, IDXGIDevice dxgiDevice,
            IDirect3DDevice winrtDevice, Direct3D11CaptureFramePool framePool, GraphicsCaptureSession session,
            SizeInt32 poolSize)
        {
            _item = item;
            _d3dDevice = d3dDevice;
            _dxgiDevice = dxgiDevice;
            _winrtDevice = winrtDevice;
            _framePool = framePool;
            _session = session;
            _poolSize = poolSize;
            _context = d3dDevice.ImmediateContext;
            _item.Closed += (_, _) => _closed = true;
        }

        public static CaptureSession Create(nint hwnd)
        {
            var item = CreateItemForWindow(hwnd);
            var d3dDevice = CreateD3D11Device(out var dxgiDevice);
            var winrtDevice = CreateWinRtDevice(dxgiDevice);
            // More than the minimum buffer count so the pool always has slack:
            // with too few buffers, a new frame arriving while we're still
            // reading the previous one's texture can recycle it out from under
            // us, surfacing as an intermittent NullReferenceException/
            // AccessViolationException deep in the D3D11 copy call.
            var poolSize = item.Size;
            var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 4, poolSize);
            var session = framePool.CreateCaptureSession(item);
            try { session.IsCursorCaptureEnabled = false; } catch { /* not supported on older builds */ }
            session.StartCapture();
            return new CaptureSession(item, d3dDevice, dxgiDevice, winrtDevice, framePool, session, poolSize);
        }

        public byte[] CaptureFrameJpeg()
        {
            lock (_captureLock)
            {
                if (_closed) throw new InvalidOperationException("WGC: the captured window has closed.");

                // Two passes at most: if the first frame shows the window has
                // resized since the pool was built, recreate the pool at the new
                // content size and take a second, correctly-sized frame.
                for (var attempt = 0; ; attempt++)
                {
                    using var frame = WaitForFrame();
                    var contentSize = frame.ContentSize;

                    if (attempt == 0 &&
                        (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height) &&
                        contentSize.Width > 0 && contentSize.Height > 0)
                    {
                        _poolSize = contentSize;
                        _framePool.Recreate(
                            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 4, contentSize);
                        continue;
                    }

                    using var sourceTexture = WgcFrameCapture.GetTextureForSurface(frame.Surface);
                    return TextureToJpeg(_d3dDevice, _context, sourceTexture, contentSize);
                }
            }
        }

        private Direct3D11CaptureFrame WaitForFrame()
        {
            Direct3D11CaptureFrame? frame = null;
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (frame is null && DateTime.UtcNow < deadline)
            {
                frame = _framePool.TryGetNextFrame();
                if (frame is null) Thread.Sleep(15);
            }

            return frame ?? throw new InvalidOperationException(
                "WGC: no frame arrived from the capture session in time.");
        }

        public void Dispose()
        {
            try { _session.Dispose(); } catch { /* ignore */ }
            try { _framePool.Dispose(); } catch { /* ignore */ }
            try { _context.Dispose(); } catch { /* ignore */ }
            try { _winrtDevice.Dispose(); } catch { /* ignore */ }
            try { _dxgiDevice.Dispose(); } catch { /* ignore */ }
            try { _d3dDevice.Dispose(); } catch { /* ignore */ }
        }
    }

    // vtable slot 3 on IGraphicsCaptureItemInterop: HRESULT CreateForWindow(HWND, REFIID, void**).
    // Dispatched as a raw unmanaged function pointer instead of a [ComImport]
    // RCW — GetTypedObjectForIUnknown's classic-COM-interop wrapper does its
    // own QueryInterface under the hood and threw InvalidCastException against
    // this particular WinRT activation factory, so this bypasses that layer
    // entirely and calls the vtable slot directly.
    private static unsafe GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        const string runtimeClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(runtimeClassName, runtimeClassName.Length, out var classNameHandle);
        try
        {
            var interopIid = GraphicsCaptureItemInteropGuid;
            var hr = RoGetActivationFactory(classNameHandle, ref interopIid, out var factoryPointer);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            try
            {
                var vtable = *(nint**)factoryPointer;
                var createForWindow = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtable[3];

                // IInspectable is the universal WinRT base interface every runtime
                // class supports, so this request can never fail with
                // E_NOINTERFACE. MarshalInterface<T>.FromAbi below does its own
                // QueryInterface for the actual GraphicsCaptureItem vtable as
                // needed — it isn't relying on this pointer already having that
                // exact shape.
                var itemIid = IInspectableGuid;
                nint itemPointer;
                hr = createForWindow(factoryPointer, hwnd, &itemIid, &itemPointer);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);

                // FromAbi takes ownership of itemPointer (consumes the reference
                // the CreateForWindow out-param handed us) — releasing it here on
                // top of that would double-release the underlying COM object and
                // leave the returned wrapper pointing at freed memory, which
                // eventually surfaces as an AccessViolationException a few
                // captures later once the freed block gets reused.
                return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
            }
            finally
            {
                Marshal.Release(factoryPointer);
            }
        }
        finally
        {
            WindowsDeleteString(classNameHandle);
        }
    }

    private static ID3D11Device CreateD3D11Device(out IDXGIDevice dxgiDevice)
    {
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null!,
            out ID3D11Device? device).CheckError();
        dxgiDevice = device!.QueryInterface<IDXGIDevice>();
        return device;
    }

    private static IDirect3DDevice CreateWinRtDevice(IDXGIDevice dxgiDevice)
    {
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var graphicsDevicePointer);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);

        // Same ownership-transfer contract as CreateItemForWindow above — do not
        // also Marshal.Release(graphicsDevicePointer) here.
        return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevicePointer);
    }

    // IDirect3DSurface always implements IDirect3DDxgiInterfaceAccess (native
    // WinRT/DXGI interop bridge) — same raw-vtable dispatch as CreateForWindow,
    // for the same reliability reasons.
    private static unsafe ID3D11Texture2D GetTextureForSurface(IDirect3DSurface surface)
    {
        var unknownPointer = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var accessIid = DxgiInterfaceAccessGuid;
            Marshal.ThrowExceptionForHR(
                Marshal.QueryInterface(unknownPointer, ref accessIid, out var accessPointer));
            try
            {
                var vtable = *(nint**)accessPointer;
                var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[3];

                var textureIid = D3D11Texture2DGuid;
                nint texturePointer;
                var hr = getInterface(accessPointer, &textureIid, &texturePointer);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);

                // Same ownership-transfer contract as CreateItemForWindow: the
                // ID3D11Texture2D constructor takes ownership of texturePointer
                // (no internal AddRef of its own), so releasing it here on top of
                // that double-frees the texture — it looks fine immediately since
                // the freed block isn't reused right away, then intermittently
                // surfaces as a NullReferenceException/AccessViolationException a
                // number of captures later once it is.
                return new ID3D11Texture2D(texturePointer);
            }
            finally
            {
                Marshal.Release(accessPointer);
            }
        }
        finally
        {
            Marshal.Release(unknownPointer);
        }
    }

    private static byte[] TextureToJpeg(
        ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D sourceTexture, SizeInt32 contentSize)
    {
        var description = sourceTexture.Description;
        var stagingDescription = description;
        stagingDescription.Usage = ResourceUsage.Staging;
        stagingDescription.CPUAccessFlags = CpuAccessFlags.Read;
        stagingDescription.BindFlags = BindFlags.None;
        stagingDescription.MiscFlags = ResourceOptionFlags.None;

        using var staging = device.CreateTexture2D(stagingDescription);
        context.CopyResource(staging, sourceTexture);

        // The pool's buffers can be larger than the live content (the window
        // shrank since the pool was sized). Emit only the content region so the
        // frame never carries stale padding along the right/bottom edges.
        var width = Math.Max(1, Math.Min((int)description.Width, contentSize.Width));
        var height = Math.Max(1, Math.Min((int)description.Height, contentSize.Height));

        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                var rowBytes = bitmap.Width * 4;
                for (var y = 0; y < bitmap.Height; y++)
                {
                    var sourceRow = mapped.DataPointer + y * mapped.RowPitch;
                    var destRow = bitmapData.Scan0 + y * bitmapData.Stride;
                    unsafe { Buffer.MemoryCopy((void*)sourceRow, (void*)destRow, rowBytes, rowBytes); }
                }
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }

            using var stream = new MemoryStream();

            var jpegEncoder = ImageCodecInfo.GetImageEncoders()
                .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
            using var encodersParams = new EncoderParameters(1);
            encodersParams.Param[0] = new EncoderParameter(Encoder.Quality, 88L);
            bitmap.Save(stream, jpegEncoder, encodersParams);

            return stream.ToArray();
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int RoGetActivationFactory(
        nint activatableClassId,
        [In] ref Guid iid,
        out nint factory);

    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out nint hstring);

    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
