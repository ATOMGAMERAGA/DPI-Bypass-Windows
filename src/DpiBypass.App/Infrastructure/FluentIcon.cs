using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DpiBypass.App.Infrastructure;

/// <summary>
/// One glyph from Microsoft Fluent System Icons, drawn at the size it was designed for.
/// </summary>
/// <remarks>
/// <para>
/// Markup names a symbol and a size - <c>&lt;infra:FluentIcon Symbol="Shield" Size="20" /&gt;</c>
/// - and this resolves the geometry out of <c>Theme/Icons.xaml</c>. Two things are
/// deliberate about that indirection.
/// </para>
/// <para>
/// The first is that Fluent draws every icon once per size, not once and then scaled:
/// the 20px shield has a different stroke weight and different rounding from the 24px
/// one, because a glyph shrunk from 24 to 16 goes thin and muddy. So a size of 16 or 20
/// picks the 20px drawing and anything larger picks the 24px drawing, and the selection
/// lives here rather than in the hands of whoever writes the next page.
/// </para>
/// <para>
/// The second is <see cref="Stretch.None"/>. Stretching each path to fill its box is the
/// obvious thing to do and it is wrong: the path bounds of a glyph are not the icon's
/// design box - the shield fills its 24-unit grid almost edge to edge while the chevron
/// occupies barely a third of it - so stretching makes every icon the same physical size
/// as the largest and destroys the optical rhythm the family was drawn with. The grid is
/// scaled instead, uniformly, so a 20 DIP chevron stays a 20 DIP chevron's worth of ink.
/// </para>
/// </remarks>
public sealed class FluentIcon : System.Windows.Controls.Control
{
    /// <summary>The largest requested size still served by Fluent's 20px drawing.</summary>
    private const double SmallGridCeiling = 20d;

    static FluentIcon()
    {
        // No DefaultStyleKey override and so no Themes/Generic.xaml: the template lives
        // in Theme/Shared.xaml as an implicit style, next to every other control's, which
        // is where somebody changing how icons look would look for it.

        // Icons are decoration for the control they sit in; their meaning is carried by
        // that control's own name. A rail item that announced "shield" before its label
        // is noise to a screen reader, so they are invisible to automation unless the
        // caller deliberately gives one a name.
        FocusableProperty.OverrideMetadata(typeof(FluentIcon), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(FluentIcon), new FrameworkPropertyMetadata(false));
    }

    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol),
        typeof(string),
        typeof(FluentIcon),
        new FrameworkPropertyMetadata(string.Empty, OnGlyphInputChanged));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled),
        typeof(bool),
        typeof(FluentIcon),
        new FrameworkPropertyMetadata(false, OnGlyphInputChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size),
        typeof(double),
        typeof(FluentIcon),
        new FrameworkPropertyMetadata(20d, OnGlyphInputChanged));

    private static readonly DependencyPropertyKey DataPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Data),
        typeof(Geometry),
        typeof(FluentIcon),
        new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty DataProperty = DataPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey GlyphScalePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(GlyphScale),
        typeof(Transform),
        typeof(FluentIcon),
        new FrameworkPropertyMetadata(Transform.Identity));

    public static readonly DependencyProperty GlyphScaleProperty = GlyphScalePropertyKey.DependencyProperty;

    /// <summary>The Fluent symbol name, e.g. <c>Shield</c> or <c>ArrowClockwise</c>.</summary>
    public string Symbol
    {
        get => (string)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    /// <summary>
    /// Whether to use the filled variant, which Fluent reserves for a selected or
    /// active item. Falls back to the regular drawing for symbols that have no filled
    /// variant rather than rendering nothing.
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

    /// <summary>The resolved path data. Set by this control; the template binds to it.</summary>
    public Geometry? Data => (Geometry?)GetValue(DataProperty);

    /// <summary>Scales the icon's design grid onto <see cref="Size"/>. Never scales the path bounds.</summary>
    public Transform GlyphScale => (Transform)GetValue(GlyphScaleProperty);

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // The template is what draws it, so a control templated after its properties
        // were set still needs the geometry resolving once.
        Resolve();
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Resolve();
    }

    private static void OnGlyphInputChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        => ((FluentIcon)sender).Resolve();

    private void Resolve()
    {
        var size = Size;
        if (double.IsNaN(size) || size <= 0)
        {
            size = 20d;
        }

        var preferred = size <= SmallGridCeiling ? 20 : 24;
        var other = preferred == 20 ? 24 : 20;

        var symbol = Symbol;
        if (string.IsNullOrEmpty(symbol))
        {
            SetValue(GlyphScalePropertyKey, FrozenScale(size / preferred));
            SetValue(DataPropertyKey, null);
            return;
        }

        // The preferred grid first, filled before regular when a filled glyph was asked
        // for; the other grid only when this symbol was not drawn on the preferred one.
        // A symbol Fluent draws only one way must not vanish because a selected state
        // asked for the variant it does not have.
        var variant = Filled ? "Filled" : "Regular";
        var (geometry, grid) =
            Lookup(symbol, preferred, variant)
            ?? Lookup(symbol, preferred, "Regular")
            ?? Lookup(symbol, other, variant)
            ?? Lookup(symbol, other, "Regular")
            ?? (null, preferred);

        // The scale follows the drawing that was actually found, not the one asked for:
        // a 24-unit glyph scaled as though it were 20 units renders a fifth too large.
        SetValue(GlyphScalePropertyKey, FrozenScale(size / grid));
        SetValue(DataPropertyKey, geometry);
    }

    private (Geometry Geometry, int Grid)? Lookup(string symbol, int grid, string variant)
        => Find($"Icon.{symbol}.{grid}.{variant}") is { } geometry ? (geometry, grid) : null;

    private static Transform FrozenScale(double factor)
    {
        if (Math.Abs(factor - 1d) < 0.0001)
        {
            return Transform.Identity;
        }

        var transform = new ScaleTransform(factor, factor);
        transform.Freeze();
        return transform;
    }

    /// <summary>
    /// Looks a geometry up through the element tree and then the application.
    /// </summary>
    /// <remarks>
    /// <see cref="FrameworkElement.TryFindResource"/> alone is not enough: an icon
    /// inside a control template that has not been connected to a tree yet finds
    /// nothing, and an icon in a window whose application never loaded is the unit-test
    /// case. Both fall through to the application dictionary, and a missing key is null
    /// rather than an exception - a page with one glyph missing is a bad page; a page
    /// that throws while being built is no page at all.
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
