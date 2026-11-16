using System.Runtime.InteropServices;
using TylerCast.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TylerCast.Windows;

/// <summary>One captured screen image: BGRA, top-down, <see cref="Stride"/> bytes per row.</summary>
public sealed class ScreenImage(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride => Width * 4;
    public byte[] Pixels { get; } = new byte[width * height * 4];
}

/// <summary>A way of capturing the main display. Implementations aren't thread-safe; one capture thread calls them.</summary>
public interface IScreenCapture : IDisposable
{
    string Name { get; }
    int Width { get; }
    int Height { get; }
    /// <summary>
    /// Updates <paramref name="image"/> with the screen as it is now (pointer included). Returns false if
    /// nothing changed since the last call (the image is left as it was). Throws if capture is lost for good.
    /// </summary>
    bool Capture(ScreenImage image, int timeoutMs);
}

/// <summary>
/// DXGI Desktop Duplication of the primary display: fast, works with any GPU, but the
/// device must be created on the adapter that drives that display (laptops with two
/// GPUs fail with DXGI_ERROR_UNSUPPORTED otherwise), and it ends on desktop switches
/// (UAC, lock screen) with ACCESS_LOST, after which it is recreated.
/// </summary>
public sealed unsafe class DuplicationCapture : IScreenCapture
{
    public string Name { get; private set; } = "Desktop Duplication";
    public int Width { get; private set; }
    public int Height { get; private set; }

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutput1? _output;
    private IDXGIOutputDuplication? _dup;
    private ID3D11Texture2D? _staging;
    private byte[] _pointerShape = [];
    private DXGI_OUTDUPL_POINTER_SHAPE_INFO _pointerInfo;
    private DXGI_OUTDUPL_POINTER_POSITION _pointerPos;
    private ScreenImage? _frame;

    public DuplicationCapture() => Open();

    /// <summary>The primary display's output and the adapter it belongs to.</summary>
    private static (IDXGIAdapter1 Adapter, IDXGIOutput Output, DXGI_OUTPUT_DESC Desc, string AdapterName) FindPrimary()
    {
        var iid = typeof(IDXGIFactory1).GUID;
        PInvoke.CreateDXGIFactory1(&iid, out var f).ThrowOnFailure();
        var factory = (IDXGIFactory1)f;
        var primary = PInvoke.MonitorFromPoint(new System.Drawing.Point(0, 0), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Succeeded; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Succeeded; o++)
            {
                var desc = output.GetDesc();
                if (desc.Monitor == primary) return (adapter, output, desc, adapter.GetDesc1().Description.ToString());
            }
        }
        throw new InvalidOperationException("no DXGI output drives the primary display");
    }

    private void Open()
    {
        Close();
        var (adapter, output, desc, adapterName) = FindPrimary();
        PInvoke.D3D11CreateDevice(adapter, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN, HMODULE.Null,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, null, PInvoke.D3D11_SDK_VERSION,
            out _device, out _context).ThrowOnFailure();
        _output = (IDXGIOutput1)output;
        _output.DuplicateOutput(_device, out _dup);
        DXGI_OUTDUPL_DESC dd;
        _dup.GetDesc(&dd);
        Width = (int)dd.ModeDesc.Width;
        Height = (int)dd.ModeDesc.Height;
        Name = $"Desktop Duplication on {adapterName.Trim()} ({Width}x{Height}, {dd.ModeDesc.Format})";
        if (desc.Rotation is not (DXGI_MODE_ROTATION.DXGI_MODE_ROTATION_IDENTITY or DXGI_MODE_ROTATION.DXGI_MODE_ROTATION_UNSPECIFIED))
            Log.Warn($"Screen rotation {desc.Rotation} is not handled; the picture will be sideways");
        if (dd.ModeDesc.Format != DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM)
            throw new NotSupportedException($"screen format {dd.ModeDesc.Format} (HDR?) is not BGRA8; use GDI capture");

        var td = new D3D11_TEXTURE2D_DESC
        {
            Width = dd.ModeDesc.Width,
            Height = dd.ModeDesc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
        };
        ID3D11Texture2D_unmanaged* tex;
        _device.CreateTexture2D(&td, null, &tex);
        _staging = (ID3D11Texture2D)Marshal.GetObjectForIUnknown((nint)tex);
        Marshal.Release((nint)tex);
        Log.Info($"Capture: {Name}");
    }

    public bool Capture(ScreenImage image, int timeoutMs)
    {
        for (var attempt = 0; ; attempt++)
        {
            DXGI_OUTDUPL_FRAME_INFO info;
            var hr = _dup!.AcquireNextFrame((uint)timeoutMs, &info, out var resource);
            if (hr == HRESULT.DXGI_ERROR_WAIT_TIMEOUT) return false;
            if (hr == HRESULT.DXGI_ERROR_ACCESS_LOST && attempt < 3)
            {
                // Desktop switch (UAC prompt, lock screen, mode change): start again.
                Log.Info("Desktop Duplication: access lost (desktop switch or display change); reopening");
                Thread.Sleep(200);
                Open();
                continue;
            }
            hr.ThrowOnFailure();
            var changed = false;
            try
            {
                if (info.LastMouseUpdateTime != 0)
                {
                    _pointerPos = info.PointerPosition;
                    changed = true;
                }
                if (info.PointerShapeBufferSize > 0)
                {
                    if (_pointerShape.Length < info.PointerShapeBufferSize) _pointerShape = new byte[info.PointerShapeBufferSize];
                    fixed (byte* p = _pointerShape)
                    fixed (DXGI_OUTDUPL_POINTER_SHAPE_INFO* pi = &_pointerInfo)
                        _dup.GetFramePointerShape((uint)_pointerShape.Length, p, out _, pi);
                }
                if (info.LastPresentTime != 0 || _frame is null)
                {
                    var texture = (ID3D11Texture2D)resource;
                    _context!.CopyResource(_staging!, texture);
                    D3D11_MAPPED_SUBRESOURCE map;
                    _context.Map(_staging!, 0, D3D11_MAP.D3D11_MAP_READ, 0, &map);
                    try
                    {
                        if (_frame is null || _frame.Width != Width || _frame.Height != Height) _frame = new ScreenImage(Width, Height);
                        fixed (byte* dst = _frame.Pixels)
                        {
                            for (var y = 0; y < Height; y++)
                                Buffer.MemoryCopy((byte*)map.pData + (long)y * map.RowPitch, dst + (long)y * _frame.Stride, _frame.Stride, _frame.Stride);
                        }
                    }
                    finally { _context.Unmap(_staging!, 0); }
                    changed = true;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(resource);
                _dup.ReleaseFrame();
            }
            if (!changed || _frame is null) return false;
            // The pointer is drawn on a copy, so it never sticks to the cached desktop image.
            Buffer.BlockCopy(_frame.Pixels, 0, image.Pixels, 0, Math.Min(image.Pixels.Length, _frame.Pixels.Length));
            if (_pointerPos.Visible && _pointerInfo.Width > 0)
            {
                CursorPainter.Draw(image.Pixels, image.Width, image.Height, image.Stride, (CursorPainter.ShapeType)_pointerInfo.Type,
                    _pointerShape, (int)_pointerInfo.Width, (int)_pointerInfo.Height, (int)_pointerInfo.Pitch,
                    _pointerPos.Position.X, _pointerPos.Position.Y);
            }
            return true;
        }
    }

    private void Close()
    {
        foreach (var o in new object?[] { _staging, _dup, _output, _context, _device })
            if (o is not null) Marshal.ReleaseComObject(o);
        _staging = null; _dup = null; _output = null; _context = null; _device = null;
        _frame = null;
    }

    public void Dispose() => Close();
}

/// <summary>
/// GDI BitBlt of the whole primary screen, plus the pointer drawn with DrawIconEx.
/// Slower than Desktop Duplication but works where it doesn't (remote sessions,
/// some virtual GPUs, HDR displays: Windows tone-maps GDI captures to SDR).
/// Needs per-monitor DPI awareness (set at start-up) to see physical pixels.
/// </summary>
public sealed unsafe class GdiCapture : IScreenCapture
{
    public string Name => $"GDI BitBlt ({Width}x{Height})";
    public int Width { get; }
    public int Height { get; }

    private readonly HDC _screen;
    private readonly HDC _mem;
    private readonly HBITMAP _bitmap;
    private readonly HGDIOBJ _old;
    private readonly byte* _bits;

    public GdiCapture()
    {
        Width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN);
        Height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN);
        _screen = PInvoke.GetDC(HWND.Null);
        _mem = PInvoke.CreateCompatibleDC(_screen);
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = Width;
        bmi.bmiHeader.biHeight = -Height; // top-down
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0; // BI_RGB
        void* bits;
        _bitmap = PInvoke.CreateDIBSection(_mem, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        if (_bitmap.IsNull) throw new InvalidOperationException("CreateDIBSection failed");
        _bits = (byte*)bits;
        _old = PInvoke.SelectObject(_mem, (HGDIOBJ)_bitmap.Value);
        Log.Info($"Capture: {Name}");
    }

    public bool Capture(ScreenImage image, int timeoutMs)
    {
        if (!PInvoke.BitBlt(_mem, 0, 0, Width, Height, _screen, 0, 0, ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT))
            throw new InvalidOperationException($"BitBlt failed ({Marshal.GetLastPInvokeError()})");
        var ci = new CURSORINFO { cbSize = (uint)sizeof(CURSORINFO) };
        if (PInvoke.GetCursorInfo(&ci) && (ci.flags & CURSORINFO_FLAGS.CURSOR_SHOWING) != 0)
        {
            var icon = (HICON)ci.hCursor.Value;
            ICONINFO ii;
            if (PInvoke.GetIconInfo(icon, &ii))
            {
                PInvoke.DrawIconEx(_mem, ci.ptScreenPos.X - (int)ii.xHotspot, ci.ptScreenPos.Y - (int)ii.yHotspot,
                    icon, 0, 0, 0, HBRUSH.Null, DI_FLAGS.DI_NORMAL);
                if (!ii.hbmMask.IsNull) PInvoke.DeleteObject((HGDIOBJ)ii.hbmMask.Value);
                if (!ii.hbmColor.IsNull) PInvoke.DeleteObject((HGDIOBJ)ii.hbmColor.Value);
            }
        }
        fixed (byte* dst = image.Pixels)
            Buffer.MemoryCopy(_bits, dst, image.Pixels.Length, Math.Min((long)Width * Height * 4, image.Pixels.Length));
        return true;
    }

    public void Dispose()
    {
        PInvoke.SelectObject(_mem, _old);
        PInvoke.DeleteObject((HGDIOBJ)_bitmap.Value);
        PInvoke.DeleteDC(_mem);
        _ = PInvoke.ReleaseDC(HWND.Null, _screen);
    }
}

public static class Captures
{
    /// <summary>
    /// The best capture that works here: Desktop Duplication, else GDI. <paramref name="force"/>
    /// ("dxgi" or "gdi") picks one for tests. Each failure is logged with its reason.
    /// </summary>
    public static IScreenCapture Open(string? force = null)
    {
        if (force is null or "dxgi")
        {
            try
            {
                return new DuplicationCapture();
            }
            catch (Exception e)
            {
                Log.Warn($"Desktop Duplication unavailable: {Describe(e)}");
                if (force == "dxgi") throw;
            }
        }
        return new GdiCapture();
    }

    public static string Describe(Exception e) =>
        e is COMException c ? $"{c.Message.Trim()} (0x{c.HResult:X8})" : $"{e.GetType().Name}: {e.Message}";
}
