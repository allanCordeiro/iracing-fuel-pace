using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using FuelPace.App.Overlay;

namespace FuelPace.Settings;

/// <summary>
/// One line of text with letter spacing (WPF's <c>TextBlock</c> has none): the mocks' small-caps headings
/// (<c>CONSUMO MÉDIO</c>, <c>OVERLAY</c>, <c>PRÉVIA · 100%</c>), drawn with the embedded faces. The line box is the face's
/// natural line height, like a <c>TextBlock</c> beside it; screen readers read it as text.
/// </summary>
public sealed class SpacedText : FrameworkElement
{
    const FrameworkPropertyMetadataOptions Layout = FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string),
        typeof(SpacedText), new FrameworkPropertyMetadata("", Layout));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double),
        typeof(SpacedText), new FrameworkPropertyMetadata(12.0, Layout));
    public static readonly DependencyProperty WeightProperty = DependencyProperty.Register(nameof(Weight), typeof(int),
        typeof(SpacedText), new FrameworkPropertyMetadata(600, Layout));
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(nameof(Spacing), typeof(double),
        typeof(SpacedText), new FrameworkPropertyMetadata(0.06, Layout));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush),
        typeof(SpacedText), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>Font size in px.</summary>
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    /// <summary>CSS weight of the Inter face.</summary>
    public int Weight { get => (int)GetValue(WeightProperty); set => SetValue(WeightProperty, value); }
    /// <summary>Letter spacing in em, after every char (as CSS).</summary>
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    static EmbeddedFonts Fonts => EmbeddedFonts.Shared;

    protected override Size MeasureOverride(Size availableSize) =>
        new(Fonts.Width(Text ?? "", FontRole.Label, Weight, (float)Size, (float)Spacing), Fonts.Face(FontRole.Label, Weight).Height * Size);

    protected override void OnRender(DrawingContext dc)
    {
        var run = Fonts.Run(FontRole.Label, Weight, false, Text ?? "", Size, Spacing,
            new Point(0, Fonts.Face(FontRole.Label, Weight).Baseline * Size), VisualTreeHelper.GetDpi(this).PixelsPerDip, out _);
        if (run is not null) dc.DrawGlyphRun(Foreground, run);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    sealed class Peer(SpacedText owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override string GetClassNameCore() => nameof(SpacedText);
        protected override string GetNameCore() => owner.Text ?? "";
        protected override bool IsControlElementCore() => true;
    }
}
