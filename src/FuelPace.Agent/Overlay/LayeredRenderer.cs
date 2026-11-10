using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using FuelPace.App.Diagnostics;
using FuelPace.App.Overlay;
using FuelPace.Core.Diagnostics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace FuelPace.Agent.Overlay;

/// <summary>
/// The fallback backend (decision 7): Direct2D (software) draws into a premultiplied 32-bit top-down DIB section through a DC
/// render target, and <c>UpdateLayeredWindow</c> (<c>ULW_ALPHA</c>) shows it. At ≤ 10 redraws/s the CPU copy of a
/// 300 × 110 px overlay costs next to nothing. A per-pixel-alpha window lets the mouse through where alpha is 0, so in edit
/// mode the background is cleared to alpha 1/255 (invisible) and the whole window takes the mouse. UI thread.
/// </summary>
internal sealed unsafe class LayeredRenderer : IOverlayRenderer
{
    private const int D2DRecreateTarget = unchecked((int)0x8899000C);

    private readonly HWND _hwnd;
    private readonly DWriteFonts _fonts;
    private readonly Func<DiagLog>? _log;
    private readonly ID2D1Factory _factory;
    private ID2D1DCRenderTarget? _rt;
    private D2DDrawListRenderer? _drawer;
    private readonly HDC _memDc;
    private HGDIOBJ _dib, _original;
    private int _w, _h;
    private RenderFailures _failures;

    public LayeredRenderer(nint hwnd, DWriteFonts fonts, Func<DiagLog>? log = null)
    {
        _hwnd = (HWND)hwnd;
        _fonts = fonts;
        _log = log;
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory>(D2DFactoryType.SingleThreaded);
        _memDc = PInvoke.CreateCompatibleDC(HDC.Null);
        if (_memDc.IsNull)
        {
            _factory.Dispose();
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateCompatibleDC failed");
        }
        try
        {
            CreateTarget();
            Resize(1, 1);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private DiagLog Log => _log?.Invoke() ?? DiagLog.Null;

    /// <summary>The DC render target and its drawer, or both null (nothing half-built is left behind on failure).</summary>
    private void CreateTarget()
    {
        ReleaseTarget();
        var rt = _factory.CreateDCRenderTarget(new RenderTargetProperties(RenderTargetType.Software,
            new D2DPixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 0, 0, RenderTargetUsage.None,
            Vortice.Direct2D1.FeatureLevel.Default));
        try { _drawer = new D2DDrawListRenderer(rt, _fonts); }
        catch
        {
            rt.Dispose();
            throw;
        }
        _rt = rt;
    }

    private void ReleaseTarget()
    {
        _drawer?.Dispose(); _drawer = null;
        _rt?.Dispose(); _rt = null;
    }

    /// <summary>At most once a second; a failure is logged once per HRESULT and the overlay stays blank meanwhile.</summary>
    private bool TryCreateTarget()
    {
        if (!_failures.MayRetry()) return false;
        try
        {
            CreateTarget();
            return true;
        }
        catch (Exception e)
        {
            ReleaseTarget();
            _failures.Report(Log, RenderFailures.StageRebuild, e.HResult, "layered render target could not be created: " + e.Message);
            return false;
        }
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _w && height == _h) return;
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = width;
        bmi.bmiHeader.biHeight = -height; // top-down, like the D2D target
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0; // BI_RGB
        void* bits;
        var dib = PInvoke.CreateDIBSection(_memDc, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        if (dib.IsNull) throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateDIBSection failed");
        var previous = PInvoke.SelectObject(_memDc, (HGDIOBJ)dib.Value);
        if (_dib.IsNull) _original = previous;
        else PInvoke.DeleteObject(_dib);
        _dib = (HGDIOBJ)dib.Value;
        _w = width;
        _h = height;
    }

    public void Render(DrawList list, float scale, bool hitTestable)
    {
        if (_rt is null && !TryCreateTarget()) return;
        int hr = Draw(list, scale, hitTestable);
        if (hr == D2DRecreateTarget)
        {
            Log.Emit(DiagLevel.Warn, AppDiagEvents.DeviceLost, 0, 0, hr);
            ReleaseTarget();
            if (!TryCreateTarget()) return;
            hr = Draw(list, scale, hitTestable);
        }
        if (hr < 0)
        {
            _failures.Report(Log, RenderFailures.StageDraw, hr);
            return;
        }
        var size = new SIZE(_w, _h);
        var src = new System.Drawing.Point(0, 0);
        var blend = new BLENDFUNCTION
        {
            BlendOp = (byte)PInvoke.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = (byte)PInvoke.AC_SRC_ALPHA,
        };
        if (!PInvoke.UpdateLayeredWindow(_hwnd, HDC.Null, null, &size, _memDc, &src, default, &blend, UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA))
            _failures.Report(Log, RenderFailures.StageShow, Marshal.GetHRForLastWin32Error());
        else _failures.Succeeded();
    }

    private int Draw(DrawList list, float scale, bool hitTestable)
    {
        var rt = _rt!;
        rt.BindDC((nint)_memDc.Value, new RawRect(0, 0, _w, _h));
        rt.BeginDraw();
        rt.Clear(hitTestable ? new Color4(0, 0, 0, 1 / 255f) : new Color4(0, 0, 0, 0));
        rt.Transform = scale == 1 ? Matrix3x2.Identity : Matrix3x2.CreateScale(scale);
        _drawer!.Draw(list);
        rt.Transform = Matrix3x2.Identity;
        return rt.EndDraw().Code;
    }

    public void Dispose()
    {
        ReleaseTarget();
        _factory.Dispose();
        if (!_dib.IsNull)
        {
            PInvoke.SelectObject(_memDc, _original);
            PInvoke.DeleteObject(_dib);
            _dib = default;
        }
        if (!_memDc.IsNull) PInvoke.DeleteDC(_memDc);
    }
}
