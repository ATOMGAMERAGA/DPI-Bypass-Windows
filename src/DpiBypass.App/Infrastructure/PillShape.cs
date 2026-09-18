using System.Windows;
using System.Windows.Controls;

namespace DpiBypass.App.Infrastructure;

/// <summary>
/// Keeps a <see cref="Border"/>'s corner radius at the one value WPF draws as a capsule:
/// half of what is left of its height once the stroke is taken off.
/// </summary>
/// <remarks>
/// <para>
/// This exists because WPF's rounding is not CSS's rounding, and the difference is not a
/// detail. In CSS, <c>border-radius: 999px</c> on a short wide box gives a stadium: the
/// browser clamps every radius to <c>min(width, height) / 2</c> and the ends come out as
/// semicircles. Writing the same 999 into <see cref="Border.CornerRadius"/> gives an
/// <em>ellipse</em>, and that is what the badges in this app were drawing.
/// </para>
/// <para>
/// The reason is in <c>Border.GenerateGeometry</c>. It first places eight key points from
/// the radii, then resolves any overlap along an edge by partitioning that edge between
/// the two radii that meet on it - so on a 100x24 badge asking for 999, the top edge
/// collapses to a single point at x=50 and the right edge to a single point at y=12.
/// It then derives each corner's arc from those already-clamped points, and the two
/// axes are measured independently:
/// </para>
/// <code>
///     double radiusX = rect.TopRight.X - topRight.X;   // 100 - 50 = 50
///     double radiusY = rightTop.Y - rect.TopRight.Y;   //  12 -  0 = 12
///     ctx.ArcTo(rightTop, new Size(radiusX, radiusY), ...);
/// </code>
/// <para>
/// Each quadrant is therefore an elliptical arc 50 wide and 12 tall, and the four of them
/// close into a full ellipse inscribed in the badge. Nothing is ever clamped to
/// <c>min(width, height) / 2</c>. That is why the ends of these badges looked wrong: they
/// were not blunt capsules, they were the left and right points of an oval, meeting the
/// top and bottom edges at the midpoints instead of running parallel to them.
/// </para>
/// <para>
/// A badge with uniform corners, a uniform thickness and a solid-colour brush does not
/// actually take that path - <c>Border.ArrangeOverride</c> sends it to the simple one,
/// which strokes and fills two rounded rectangles instead. That path degenerates in the
/// same way for the same reason: <c>RectangleGeometry</c> clamps each axis on its own,
/// <c>radiusX = Min(width / 2, |radiusX|)</c> and <c>radiusY = Min(height / 2,
/// |radiusY|)</c>, so an over-large radius again becomes an inscribed ellipse. Both
/// paths agree, which is why the badges looked the same wherever they were used.
/// </para>
/// <para>
/// The radius that draws a capsule on the simple path is <c>(height - thickness) / 2</c>,
/// not half the height. The two rings are inset differently: the stroke follows its pen's
/// centre line, a rect inset by half the thickness, and keeps the radius as written; the
/// fill sits inside the border, a rect inset by the whole thickness, with the radius
/// reduced by half of it. Solving both for "cap radius equals half my own height" gives
/// the same answer. On a 46x19 badge with a hairline that is 9: the stroke rounds a 45x18
/// rect by 9 and the fill rounds a 44x17 rect by 8.5, and every cap is circular. Half the
/// height - 9.5 here - clamps to 9 vertically and stays 9.5 horizontally, leaving both
/// rings slightly egg-shaped. The rings stay centred on the same point either way, so what
/// the radius decides is the shape of the caps, not their alignment.
/// </para>
/// <para>
/// The height has to be the measured one rather than a number typed into the style,
/// because these badges size themselves to their text and to the user's text-scaling
/// setting; a literal would be correct at 100% and an ellipse again at 150%.
/// </para>
/// <para>
/// Set on the style rather than per badge, so a capsule cannot be asked for by writing a
/// large number into a radius again. <see cref="Border.CornerRadius"/> is a local write
/// here, which beats a style setter, so a style must not also set the radius on the same
/// border.
/// </para>
/// </remarks>
public static class PillShape
{
    /// <summary>Makes this border a capsule and keeps it one as its height changes.</summary>
    public static readonly DependencyProperty IsPillProperty = DependencyProperty.RegisterAttached(
        "IsPill", typeof(bool), typeof(PillShape), new PropertyMetadata(false, OnIsPillChanged));

    public static bool GetIsPill(DependencyObject element) => (bool)element.GetValue(IsPillProperty);

    public static void SetIsPill(DependencyObject element, bool value) => element.SetValue(IsPillProperty, value);

    /// <summary>The radius a capsule of this height and stroke needs.</summary>
    /// <remarks>
    /// With layout rounding on - which this window sets - WPF rounds the thickness to a
    /// whole device pixel before stroking, so at 125% a hairline is 0.8 DIP rather than 1
    /// and the ideal radius is a tenth of a DIP off what this returns. That is a tenth of
    /// a pixel on a cap of nine, well inside what the rasteriser antialiases away, and it
    /// is not worth reading the DPI here to chase.
    /// </remarks>
    internal static CornerRadius RadiusFor(double height, double borderThickness = 0)
        => double.IsFinite(height) && height > 0
            ? new CornerRadius(Math.Max(0, height - Math.Max(0, borderThickness)) / 2)
            : new CornerRadius(0);

    private static void OnIsPillChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Border border)
        {
            return;
        }

        border.SizeChanged -= OnSizeChanged;
        border.Loaded -= OnLoaded;

        if (e.NewValue is not true)
        {
            return;
        }

        border.SizeChanged += OnSizeChanged;

        // SizeChanged covers every later measure pass, but a border that was already
        // laid out before the property was set - a style applied to a recycled item
        // container, say - has had its only size change and would stay square.
        border.Loaded += OnLoaded;
        Apply(border);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Apply((Border)sender);

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.HeightChanged || ((Border)sender).CornerRadius.TopLeft <= 0)
        {
            Apply((Border)sender);
        }
    }

    /// <summary>
    /// Writes the radius the current height needs.
    /// </summary>
    /// <remarks>
    /// Setting the radius cannot change the height, so this does not re-enter layout in a
    /// loop; the guard is only there to avoid dirtying the render on every width change.
    /// The thickness is read here rather than watched: these badges size themselves to
    /// their content, so a thickness change moves the height too and arrives as one of
    /// these size changes anyway.
    /// </remarks>
    private static void Apply(Border border)
    {
        var radius = RadiusFor(border.ActualHeight, border.BorderThickness.Top);
        if (border.CornerRadius != radius)
        {
            border.CornerRadius = radius;
        }
    }
}
