using System.Windows;
using System.Windows.Media;

// Windows Forms is in this project's implicit usings for the tray icon, so the names it
// shares with WPF are ambiguous here. See GlobalUsings.cs.
using Brush = System.Windows.Media.Brush;

namespace DpiBypass.App.Infrastructure;

/// <summary>
/// One glyph from Microsoft Fluent System Icons, drawn at the size it was designed for.
/// </summary>
/// <remarks>
/// <para>
/// Markup names a symbol and a size - <c>&lt;infra:FluentIcon Symbol="Shield" Size="20" /&gt;</c>
/// - and this resolves the geometry out of <c>Theme/Icons.xaml</c> and draws it. Three
/// things about that are deliberate.
/// </para>
/// <para>
/// <b>The size picks the drawing.</b> Fluent draws every icon once per size rather than
/// once and then scaled: the 20px shield has a different stroke weight and different
/// rounding from the 24px one, because a glyph shrunk from 24 to 16 goes thin and muddy.
/// So a size of 16 or 20 uses the 20px drawing and anything larger uses the 24px drawing,
/// and that choice lives here rather than in the hands of whoever writes the next page.
/// </para>
/// <para>
/// <b>The grid is scaled, not the path.</b> Stretching each path to fill its box is the
/// obvious thing to do and it is wrong: the path bounds of a glyph are not the icon's
/// design box - the shield fills its 24-unit grid almost edge to edge while the chevron
/// occupies barely a third of it - so stretching makes every icon the same physical size
/// as the largest and destroys the optical rhythm the family was drawn with. The glyph's
/// design grid is scaled onto the requested size instead, uniformly, so a 20 DIP chevron
/// stays a 20 DIP chevron's worth of ink.
/// </para>
/// <para>
/// <b>It renders itself rather than templating a Path.</b> That is not a performance
/// preference, it is the only way to draw a 20-unit glyph inside a 16 DIP box. A
/// <see cref="System.Windows.Shapes.Path"/> with <c>Stretch="None"</c> reports its
/// geometry's bounds as its desired size, and WPF applies a layout clip to any element
/// arranged into less than it asked for - so every 16 DIP icon in the window would have
/// had its right and bottom fifth sliced off, before the render transform that would have
/// made it fit ever ran. Measuring as the requested size and scaling inside
/// <see cref="OnRender"/> has no layout to be clipped by, and costs one geometry draw.
/// </para>
/// </remarks>
public sealed class FluentIcon : FrameworkElement
{
    /// <summary>The largest requested size still served by Fluent's 20px drawing.</summary>
    private const double SmallGridCeiling = 20d;

    private const double DefaultSize = 20d;

    static FluentIcon()
    {
        // Icons are decoration for the control they sit in; their meaning is carried by
        // that control's own name. A rail item that announced "shield" before its label is
        // noise to a screen reader, so they stay out of automation and out of hit testing
        // unless the caller deliberately says otherwise.
        FocusableProperty.OverrideMetadata(typeof(FluentIcon), new FrameworkPropertyMetadata(false));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(FluentIcon), new FrameworkPropertyMetadata(false));
    }

    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol),
        typeof(string),
        typeof(FluentIcon),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender, OnGlyphInputChanged));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled),
        typeof(bool),
        typeof(FluentIcon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnGlyphInputChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size),
        typeof(double),
        typeof(FluentIcon),
        new FrameworkPropertyMetadata(
            DefaultSize,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnGlyphInputChanged));

    /// <summary>The ink colour. Named to match every other control so a style can set both at once.</summary>
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground),
        typeof(Brush),
        typeof(FluentIcon),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The Fluent symbol name, e.g. <c>Shield</c> or <c>ArrowClockwise</c>.</summary>
    public string Symbol
    {
        get => (string)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    /// <summary>
    /// Whether to use the filled variant, which Fluent reserves for a selected or active
    /// item. Falls back to the regular drawing for symbols that have no filled variant
    /// rather than rendering nothing.
    /// </summary>
    public bool Filled
    {
        get => (bool)GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    /// <summary>The drawn size in DIP. 16 and 20 use Fluent's 20px asset; larger uses 24px.</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>The resolved path data, or null when the symbol is unknown. For tests.</summary>
    public Geometry? Data { get; private set; }

    /// <summary>Which of Fluent's per-size drawings <see cref="Data"/> came from.</summary>
    public int Grid { get; private set; } = 20;

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
    {
        var size = Normalised(Size);
        return new System.Windows.Size(size, size);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Data is null)
        {
            EnsureResolved();
        }

        if (Data is not { } geometry || Foreground is not { } brush)
        {
            return;
        }

        var scale = Normalised(Size) / Grid;

        // Scaled about the origin, because the glyph's position inside its design grid is
        // part of the drawing: the family is aligned as a family, not glyph by glyph.
        if (Math.Abs(scale - 1d) > 0.0001)
        {
            drawingContext.PushTransform(new ScaleTransform(scale, scale));
            drawingContext.DrawGeometry(brush, null, geometry);
            drawingContext.Pop();
            return;
        }

        drawingContext.DrawGeometry(brush, null, geometry);
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        EnsureResolved();
    }

    private static double Normalised(double size)
        => double.IsNaN(size) || size <= 0 ? DefaultSize : size;

    private static void OnGlyphInputChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        => ((FluentIcon)sender).EnsureResolved();

    private void EnsureResolved()
    {
        var preferred = Normalised(Size) <= SmallGridCeiling ? 20 : 24;
        var other = preferred == 20 ? 24 : 20;

        var symbol = Symbol;
        if (string.IsNullOrEmpty(symbol))
        {
            Data = null;
            Grid = preferred;
            return;
        }

        // The preferred grid first, filled before regular when a filled glyph was asked
        // for; the other grid only when this symbol was not drawn on the preferred one. A
        // symbol Fluent draws only one way must not vanish because a selected state asked
        // for the variant it does not have.
        var variant = Filled ? "Filled" : "Regular";
        var found =
            Lookup(symbol, preferred, variant)
            ?? Lookup(symbol, preferred, "Regular")
            ?? Lookup(symbol, other, variant)
            ?? Lookup(symbol, other, "Regular");

        // The scale follows the drawing that was actually found, not the one asked for: a
        // 24-unit glyph scaled as though it were 20 units renders a fifth too large.
        Data = found?.Geometry;
        Grid = found?.Grid ?? preferred;
    }

    private (Geometry Geometry, int Grid)? Lookup(string symbol, int grid, string variant)
        => Find($"Icon.{symbol}.{grid}.{variant}") is { } geometry ? (geometry, grid) : null;

    /// <summary>
    /// Looks a geometry up through the element tree and then the application.
    /// </summary>
    /// <remarks>
    /// <see cref="FrameworkElement.TryFindResource"/> alone is not enough: an icon inside
    /// a control template that has not been connected to a tree yet finds nothing, and an
    /// icon in a window whose application never loaded is the unit-test case. Both fall
    /// through to the application dictionary, and a missing key is null rather than an
    /// exception - a page with one glyph missing is a bad page; a page that throws while
    /// being built is no page at all.
    /// </remarks>
    private Geometry? Find(string key)
    {
        try
        {
            if (TryFindResource(key) is Geometry local)
            {
                return local;
            }

            return Application.Current?.TryFindResource(key) as Geometry;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
