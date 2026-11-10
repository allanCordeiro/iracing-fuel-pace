using System.Numerics;
using System.Runtime.InteropServices;
using FuelPace.Agent.Overlay;
using FuelPace.App.Overlay;
using FuelPace.Core.Strategy;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using WicPixelFormat = Vortice.WIC.PixelFormat;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace FuelPace.Windows.Tests;

/// <summary>An offscreen Direct2D target on a WARP device: renders a <see cref="DrawList"/> to premultiplied BGRA bytes.</summary>
internal sealed class WarpTarget : IDisposable
{
    private readonly ID3D11Device _d3d;
    private readonly ID2D1Factory1 _factory;
    private readonly ID2D1Device _device;
    public readonly ID2D1DeviceContext Context;
    public readonly D2DDrawListRenderer Renderer;

    public WarpTarget(DWriteFonts fonts)
    {
        _d3d = D3D11.D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0]);
        using var dxgi = _d3d.QueryInterface<IDXGIDevice>();
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        _device = _factory.CreateDevice(dxgi);
        Context = _device.CreateDeviceContext(DeviceContextOptions.None);
        Renderer = new D2DDrawListRenderer(Context, fonts);
    }

    /// <summary>Clears to transparent, runs <paramref name="background"/>, then draws the list offset by <paramref name="offset"/>.</summary>
    public byte[] Render(DrawList list, int w, int h, Action<ID2D1DeviceContext>? background = null, Vector2 offset = default)
    {
        var format = new D2DPixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        using var target = Context.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0, new BitmapProperties1(format, 96, 96, BitmapOptions.Target));
        using var read = Context.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
            new BitmapProperties1(format, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        Context.Target = target;
        Context.BeginDraw();
        Context.Clear(new Color4(0, 0, 0, 0));
        background?.Invoke(Context);
        Context.Transform = Matrix3x2.CreateTranslation(offset);
        Renderer.Draw(list);
        Context.Transform = Matrix3x2.Identity;
        Context.EndDraw().CheckError();
        Context.Target = null;
        read.CopyFromBitmap(target).CheckError();
        var map = read.Map(MapOptions.Read);
        try
        {
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Marshal.Copy(map.Bits + y * (int)map.Pitch, px, y * w * 4, w * 4);
            return px;
        }
        finally { read.Unmap(); }
    }

    public void Dispose()
    {
        Renderer.Dispose();
        Context.Dispose();
        _device.Dispose();
        _factory.Dispose();
        _d3d.Dispose();
    }
}

internal static partial class Com
{
    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    /// <summary>WIC needs COM on this (test pool) thread; an apartment already set is fine.</summary>
    public static void EnsureInitialized() => CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */);
}

public sealed class RenderTests
{
    const int Padding = 40; // around the overlay in the PNG, as in docs/overlay-preview

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "FuelPace.sln"))) return d.FullName;
        throw new InvalidOperationException("FuelPace.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The mocks' stand-in for the game picture (sky, ground, dashboard), opaque.</summary>
    static void Scene(ID2D1DeviceContext dc, int w, int h)
    {
        using var b = dc.CreateSolidColorBrush(new Color4(0, 0, 0, 1));
        void Band(uint rgb, float top, float bottom)
        {
            b.Color = new Color4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);
            dc.FillRectangle(new Vortice.RawRectF(0, top, w, bottom), b);
        }
        Band(0x3A4552, 0, h * 0.43f);
        Band(0x262B31, h * 0.43f, h * 0.82f);
        Band(0x0E1013, h * 0.82f, h);
    }

    static void WritePng(string path, byte[] bgra, int w, int h)
    {
        Com.EnsureInitialized();
        using var wic = new IWICImagingFactory();
        using var bitmap = wic.CreateBitmapFromMemory((uint)w, (uint)h, WicPixelFormat.Format32bppPBGRA, bgra, (uint)(w * 4));
        using (var stream = File.Create(path))
        using (var encoder = wic.CreateEncoder(ContainerFormat.Png, stream))
        using (var frame = encoder.CreateNewFrame(out var options))
        {
            frame.Initialize(options);
            frame.SetSize((uint)w, (uint)h);
            var fmt = WicPixelFormat.Format32bppBGRA;
            frame.SetPixelFormat(ref fmt);
            frame.WriteSource(bitmap);
            frame.Commit();
            encoder.Commit();
            options?.Dispose();
        }
    }

    static (byte R, byte G, byte B) Pixel(byte[] px, int w, int x, int y)
    {
        int i = (y * w + x) * 4;
        return (px[i + 2], px[i + 1], px[i]);
    }

    /// <summary>Source-over on an opaque color, CSS straight alpha.</summary>
    static (double R, double G, double B) Over((double R, double G, double B) dst, Rgba src)
    {
        double a = src.A / 255.0;
        return (src.R * a + dst.R * (1 - a), src.G * a + dst.G * (1 - a), src.B * a + dst.B * (1 - a));
    }

    static DrawList Build(OverlayPreview p, double dpi, ITextMeasurer m)
    {
        var s = p.State;
        var d = new DrawList();
        OverlayLayout.Build(in s, OverlayPreviews.Car, OverlayPreviews.Track, p.Options with { DpiScale = dpi }, m, d);
        return d;
    }

    [Fact]
    public void Renders_all_states_to_png()
    {
        using var fonts = new DWriteFonts();
        var measurer = new DWriteTextMeasurer(fonts);
        using var warp = new WarpTarget(fonts);
        string dir = Path.Combine(RepoRoot(), "artifacts", "renders");
        Directory.CreateDirectory(dir);
        Assert.Equal(9, OverlayPreviews.All.Count);

        foreach (var p in OverlayPreviews.All)
            foreach (int dpi in new[] { 1, 2 })
            {
                var list = Build(p, dpi, measurer);
                int w = (int)Math.Ceiling(list.Size.W), h = (int)Math.Ceiling(list.Size.H);
                var px = warp.Render(list, w, h);
                int painted = 0;
                for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) painted++;
                Assert.True(painted > w * h / 4, $"{p.Name}@{dpi}x: only {painted} of {w * h} pixels painted");

                if (p.State.State != SignalState.Inactive)
                {
                    // The ALVO row is the second rounded fill (after the panel). Sample its vertical centre in the strip
                    // left of the "ALVO" label, composed over black (premultiplied bytes are exactly that).
                    var rows = new List<DrawCommand>();
                    foreach (var c in list.Commands) if (c.Kind == CommandKind.FillRoundedRect) rows.Add(c);
                    var row = rows[1].Rect;
                    float k = (float)(p.Options.Scale * dpi);
                    int sx = (int)(row.X + 3.5f * k), sy = (int)(row.Y + row.H / 2);
                    var expected = Over(Over((0, 0, 0), OverlayTheme.Panel(p.Options.BackgroundOpacity)), OverlayTheme.For(p.State.State).RowBg);
                    var got = Pixel(px, w, sx, sy);
                    string what = $"{p.Name}@{dpi}x ALVO row at ({sx},{sy}): got {got}, expected {expected}";
                    Assert.True(Math.Abs(got.R - expected.R) <= 8, what);
                    Assert.True(Math.Abs(got.G - expected.G) <= 8, what);
                    Assert.True(Math.Abs(got.B - expected.B) <= 8, what);
                }

                int pad = Padding * dpi, sw = w + 2 * pad, sh = h + 2 * pad;
                var scene = warp.Render(list, sw, sh, dc => Scene(dc, sw, sh), new Vector2(pad, pad));
                string path = Path.Combine(dir, $"{p.Name}@{dpi}x.png");
                WritePng(path, scene, sw, sh);
                Assert.True(new FileInfo(path).Length > 1000, path + " is empty");
            }
    }

    [Fact]
    public void Rendering_the_same_list_again_creates_no_text_layout()
    {
        using var fonts = new DWriteFonts();
        var measurer = new DWriteTextMeasurer(fonts);
        using var warp = new WarpTarget(fonts);
        var list = Build(OverlayPreviews.All[0], 1, measurer);
        int w = (int)Math.Ceiling(list.Size.W), h = (int)Math.Ceiling(list.Size.H);
        warp.Render(list, w, h);
        long created = warp.Renderer.LayoutsCreated;
        Assert.True(created > 0);
        warp.Render(list, w, h);
        Assert.Equal(created, warp.Renderer.LayoutsCreated);
    }

    [Fact]
    public void Fonts_come_from_the_embedded_files_with_the_exact_weights()
    {
        using var fonts = new DWriteFonts();
        foreach (int weight in new[] { 400, 600, 700, 800 })
        {
            var f = fonts.Face(FontRole.Label, weight);
            Assert.Equal(("Inter", weight), (f.Family, f.Weight));
        }
        foreach (int weight in new[] { 400, 500, 600, 700 })
        {
            var f = fonts.Face(FontRole.Value, weight);
            Assert.Equal(("JetBrains Mono", weight), (f.Family, f.Weight));
        }
        Assert.Equal(800, fonts.Face(FontRole.Badge, 900).Weight); // nearest available
        Assert.Equal(700, fonts.Face(FontRole.ValueBold, 800).Weight);
    }

    [Fact]
    public void Measurer_uses_the_rendered_faces()
    {
        using var fonts = new DWriteFonts();
        var m = new DWriteTextMeasurer(fonts);
        // JetBrains Mono: 600/1000 em per char at every weight
        Assert.Equal(4 * 0.6f * 16, m.Width("2.36", FontRole.Value, 400, 16, 0), 2);
        Assert.Equal(4 * 0.6f * 16, m.Width("2.36", FontRole.ValueBold, 700, 16, 0), 2);
        // Inter gets wider with the weight, and letter spacing adds after every char
        Assert.True(m.Width("NÃO FACTÍVEL", FontRole.Badge, 800, 12, 0) > m.Width("NÃO FACTÍVEL", FontRole.Badge, 700, 12, 0));
        float plain = m.Width("COMB", FontRole.Label, 600, 9, 0);
        Assert.Equal(plain + 4 * 0.1f * 9, m.Width("COMB", FontRole.Label, 600, 9, 0.1f), 2);
        // line height 1.0: baseline = size/2 + (ascent - descent)/2
        Assert.Equal(0.86f * 10, m.Ascent(FontRole.Value, 10), 2);
        Assert.Equal((0.5f + (1984 - 494) / 2048f / 2) * 10, m.Ascent(FontRole.Label, 10), 2);
    }
}
