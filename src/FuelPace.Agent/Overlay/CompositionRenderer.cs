using System.Numerics;
using FuelPace.App.Diagnostics;
using FuelPace.App.Overlay;
using FuelPace.Core.Diagnostics;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace FuelPace.Agent.Overlay;

/// <summary>
/// The main backend (decision 7): a D3D11 device (hardware with BGRA support, else WARP), a composition swap chain
/// (B8G8R8A8, premultiplied alpha, 2 buffers, flip sequential) shown by a DirectComposition visual on a topmost target of the
/// window, and Direct2D drawing into the back buffer. <see cref="Render"/> draws, then <c>Present(0, 0)</c> and
/// <c>Commit</c>. A removed or reset device (or <c>D2DERR_RECREATE_TARGET</c>) logs <c>DeviceLost</c>, rebuilds everything
/// and draws again. UI thread.
/// </summary>
internal sealed class CompositionRenderer : IOverlayRenderer
{
    private const int DxgiDeviceRemoved = unchecked((int)0x887A0005), DxgiDeviceReset = unchecked((int)0x887A0007),
        D2DRecreateTarget = unchecked((int)0x8899000C);
    private const Format BufferFormat = Format.B8G8R8A8_UNorm;

    private static readonly Vortice.Direct3D.FeatureLevel[] Levels =
    [
        Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_1,
        Vortice.Direct3D.FeatureLevel.Level_10_0, Vortice.Direct3D.FeatureLevel.Level_9_3,
    ];

    private readonly nint _hwnd;
    private readonly DWriteFonts _fonts;
    private readonly Func<DiagLog>? _log;
    private ID3D11Device? _d3d;
    private IDXGISwapChain1? _swap;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _dc;
    private ID2D1Bitmap1? _bitmap;
    private IDCompositionDevice? _dcomp;
    private IDCompositionTarget? _target;
    private IDCompositionVisual? _visual;
    private D2DDrawListRenderer? _drawer;
    private int _w = 1, _h = 1;
    private bool _broken;

    public CompositionRenderer(nint hwnd, DWriteFonts fonts, Func<DiagLog>? log = null)
    {
        _hwnd = hwnd;
        _fonts = fonts;
        _log = log;
        try { Create(); }
        catch
        {
            Release();
            throw;
        }
    }

    /// <summary>False when the device is WARP (no usable GPU driver).</summary>
    public bool Hardware { get; private set; }

    private DiagLog Log => _log?.Invoke() ?? DiagLog.Null;

    private void Create()
    {
        _d3d = CreateDevice(out bool hardware);
        Hardware = hardware;
        using var dxgiDevice = _d3d.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        _swap = factory.CreateSwapChainForComposition(_d3d, new SwapChainDescription1
        {
            Width = (uint)_w, Height = (uint)_h, Format = BufferFormat,
            SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput, BufferCount = 2,
            Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential, AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
        });
        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(D2DFactoryType.SingleThreaded);
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _dc = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        BindTarget();
        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _dcomp.CreateTargetForHwnd(_hwnd, true, out _target).CheckError();
        _dcomp.CreateVisual(out _visual).CheckError();
        _visual.SetContent(_swap).CheckError();
        _target.SetRoot(_visual).CheckError();
        _drawer = new D2DDrawListRenderer(_dc, _fonts);
        // A first transparent frame, so the window never shows an undefined buffer.
        _dc.BeginDraw();
        _dc.Clear(new Color4(0, 0, 0, 0));
        _dc.EndDraw().CheckError();
        _swap.Present(0, PresentFlags.None).CheckError();
        _dcomp.Commit().CheckError();
    }

    private static ID3D11Device CreateDevice(out bool hardware)
    {
        try
        {
            hardware = true;
            return D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, Levels);
        }
        catch (SharpGenException)
        {
            hardware = false;
            return D3D11.D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport, Levels);
        }
    }

    private void BindTarget()
    {
        using var surface = _swap!.GetBuffer<IDXGISurface>(0);
        _bitmap = _dc!.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new D2DPixelFormat(BufferFormat, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
            BitmapOptions.Target | BitmapOptions.CannotDraw));
        _dc.Target = _bitmap;
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _w && height == _h) return;
        _w = width;
        _h = height;
        if (_swap is null) return; // broken: the next render rebuilds at this size
        _dc!.Target = null;
        _bitmap?.Dispose();
        _bitmap = null;
        var r = _swap.ResizeBuffers(2, (uint)width, (uint)height, BufferFormat, SwapChainFlags.None);
        if (IsLost(r.Code))
        {
            Recover(r.Code);
            return;
        }
        r.CheckError();
        BindTarget();
    }

    public void Render(DrawList list, float scale, bool hitTestable)
    {
        if (_drawer is null && !TryCreate()) return;
        int hr = Draw(list, scale);
        if (!IsLost(hr))
        {
            if (hr < 0) Log.Emit(DiagLevel.Error, AppDiagEvents.DeviceLost, 0, 0, hr); // not a device loss: logged, kept
            return;
        }
        if (!Recover(hr)) return;
        hr = Draw(list, scale);
        if (hr < 0) Log.Emit(DiagLevel.Error, AppDiagEvents.DeviceLost, 0, 0, hr);
    }

    private int Draw(DrawList list, float scale)
    {
        var dc = _dc!;
        dc.BeginDraw();
        dc.Clear(new Color4(0, 0, 0, 0));
        dc.Transform = scale == 1 ? Matrix3x2.Identity : Matrix3x2.CreateScale(scale);
        _drawer!.Draw(list);
        dc.Transform = Matrix3x2.Identity;
        var r = dc.EndDraw();
        if (r.Failure) return r.Code;
        r = _swap!.Present(0, PresentFlags.None);
        if (r.Failure) return r.Code;
        return _dcomp!.Commit().Code;
    }

    private static bool IsLost(int hr) => hr is DxgiDeviceRemoved or DxgiDeviceReset or D2DRecreateTarget;

    private bool Recover(int hr)
    {
        Log.Emit(DiagLevel.Warn, AppDiagEvents.DeviceLost, 0, 0, hr);
        Release();
        return TryCreate();
    }

    /// <summary>Rebuilds the device chain; while that fails (driver update, GPU reset in progress) every render retries
    /// quietly: the first failure is logged, the overlay stays blank until a rebuild succeeds.</summary>
    private bool TryCreate()
    {
        try
        {
            Create();
            if (_broken) Log.Text(DiagLevel.Info, "overlay", "composition device rebuilt");
            _broken = false;
            return true;
        }
        catch (Exception e)
        {
            Release();
            if (!_broken)
            {
                Log.Emit(DiagLevel.Error, AppDiagEvents.DeviceLost, 0, 0, e.HResult);
                Log.Text(DiagLevel.Error, "overlay", "composition device could not be rebuilt: " + e.Message);
            }
            _broken = true;
            return false;
        }
    }

    private void Release()
    {
        _drawer?.Dispose(); _drawer = null;
        if (_dc is not null) _dc.Target = null;
        _bitmap?.Dispose(); _bitmap = null;
        _visual?.Dispose(); _visual = null;
        _target?.Dispose(); _target = null; // before a new target can be created for the same window
        _dcomp?.Dispose(); _dcomp = null;
        _dc?.Dispose(); _dc = null;
        _d2dDevice?.Dispose(); _d2dDevice = null;
        _d2dFactory?.Dispose(); _d2dFactory = null;
        _swap?.Dispose(); _swap = null;
        _d3d?.Dispose(); _d3d = null;
    }

    public void Dispose() => Release();
}
