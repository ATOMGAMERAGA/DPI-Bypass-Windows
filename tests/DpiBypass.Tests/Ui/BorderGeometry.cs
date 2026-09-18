namespace DpiBypass.Tests.Ui;

/// <summary>The two radii one rounded-rectangle draw call ends up using.</summary>
internal readonly record struct ArcRadii(double X, double Y)
{
    /// <summary>A corner is circular when both axes agree; anything else is an ellipse.</summary>
    public bool IsCircular => Math.Abs(X - Y) < 0.001;
}

/// <summary>One rounded rectangle, as WPF would resolve it.</summary>
/// <param name="Width">The rect's width in DIP.</param>
/// <param name="Height">The rect's height in DIP.</param>
/// <param name="Inset">How far this ring sits inside the border's own bounds.</param>
/// <param name="Radii">The radii after clamping - what is actually drawn.</param>
internal readonly record struct RoundedRing(double Width, double Height, double Inset, ArcRadii Radii)
{
    /// <summary>How much straight edge survives along the top between the two caps.</summary>
    public double StraightTopEdge => Width - (2 * Radii.X);

    /// <summary>How much straight edge survives down the side between the two caps.</summary>
    public double StraightSideEdge => Height - (2 * Radii.Y);

    /// <summary>
    /// True when this ring is a capsule: circular caps of half its height, with straight
    /// top and bottom edges joining them.
    /// </summary>
    public bool IsCapsule
        => Radii.IsCircular
        && Math.Abs(Radii.Y - (Height / 2)) < 0.001
        && StraightTopEdge > 0.001;

    /// <summary>
    /// True when the ring has collapsed into an ellipse inscribed in its box: no straight
    /// edge left in either direction.
    /// </summary>
    public bool IsEllipse
        => Math.Abs(StraightTopEdge) < 0.001 && Math.Abs(StraightSideEdge) < 0.001;

    /// <summary>Where this ring's top-left cap is centred, in the border's own coordinates.</summary>
    /// <remarks>
    /// Stroke and fill always agree on this, whatever radius is asked for: below the clamp
    /// both are <c>thickness / 2 + radius</c>, and above it both land on the box's centre.
    /// Concentricity is therefore never the thing that is wrong with a badge - the shape of
    /// the caps is.
    /// </remarks>
    public (double X, double Y) CapCentre => (Inset + Radii.X, Inset + Radii.Y);
}

/// <summary>
/// What <c>System.Windows.Controls.Border</c> draws, as numbers.
/// </summary>
/// <remarks>
/// <para>
/// A port of the path the app's badges actually take. <c>Border.ArrangeOverride</c> sends
/// a border with uniform corners, a uniform thickness and a solid-colour brush down its
/// simple render path, which strokes one rounded rectangle along the pen's centre line and
/// fills another inside the border - rather than building the <c>GenerateGeometry</c>
/// outline used for gradients and mixed thicknesses.
/// </para>
/// <para>
/// The clamping that matters comes from <c>RectangleGeometry.GetPointList</c>, which is
/// what every rounded-rectangle draw resolves through:
/// </para>
/// <code>
///     radiusX = Math.Min(rect.Width  * (1.0 / 2.0), Math.Abs(radiusX));
///     radiusY = Math.Min(rect.Height * (1.0 / 2.0), Math.Abs(radiusY));
/// </code>
/// <para>
/// The two axes are clamped independently and neither is ever brought down to
/// <c>min(width, height) / 2</c>, so an over-large radius does not become a capsule the
/// way CSS's <c>border-radius</c> would - it becomes an ellipse inscribed in the rect.
/// <c>GenerateGeometry</c> reaches the same shape by a different route, so the conclusion
/// does not depend on which path a given border takes.
/// </para>
/// <para>
/// This models the algorithm, not the rasteriser: it proves which rounded rectangles WPF
/// is asked to draw, and therefore whether a badge is a capsule or an oval. It is not a
/// screenshot and does not stand in for one.
/// </para>
/// </remarks>
internal sealed record BorderRendering
{
    /// <summary>The stroke, along the centre line of the pen.</summary>
    public required RoundedRing Stroke { get; init; }

    /// <summary>The background fill, inside the stroke.</summary>
    public required RoundedRing Fill { get; init; }

    /// <summary>Both rings are true capsules.</summary>
    public bool IsCapsule => Stroke.IsCapsule && Fill.IsCapsule;

    /// <summary>
    /// True when every cap on both rings is a circular quadrant. This is what the radius
    /// actually controls, and what an over-large or merely approximate one gets wrong.
    /// </summary>
    public bool HasCircularCaps => Stroke.Radii.IsCircular && Fill.Radii.IsCircular;

    /// <summary>Both rings have collapsed to ovals.</summary>
    public bool IsEllipse => Stroke.IsEllipse && Fill.IsEllipse;

    /// <summary>
    /// Resolves what a border of this size, radius and uniform thickness draws.
    /// </summary>
    /// <param name="width">Border width in DIP.</param>
    /// <param name="height">Border height in DIP.</param>
    /// <param name="cornerRadius">The uniform <c>CornerRadius</c>.</param>
    /// <param name="borderThickness">The uniform <c>BorderThickness</c>.</param>
    public static BorderRendering Generate(
        double width,
        double height,
        double cornerRadius,
        double borderThickness = 0)
    {
        // Border.OnRender, uniform-thickness branch: the pen is stroked down a rect inset
        // by half the thickness, and the radius is passed through as written.
        var half = borderThickness / 2;
        var stroke = Ring(
            width - borderThickness,
            height - borderThickness,
            inset: half,
            radius: cornerRadius);

        // "Draw background in rectangle inside border": inset by the whole thickness, with
        // the inner radius from Border.Radii(radii, borders, outer: false), which is
        // Math.Max(0, radius - 0.5 * thickness).
        var fill = Ring(
            width - (2 * borderThickness),
            height - (2 * borderThickness),
            inset: borderThickness,
            radius: Math.Max(0, cornerRadius - half));

        return new BorderRendering { Stroke = stroke, Fill = fill };

        static RoundedRing Ring(double w, double h, double inset, double radius)
            => new(w, h, inset, new ArcRadii(
                Math.Min(w / 2, Math.Abs(radius)),
                Math.Min(h / 2, Math.Abs(radius))));
    }
}
