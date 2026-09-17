using System.Windows;

namespace DpiBypass.App.Infrastructure;

/// <summary>An optional Fluent icon on a button, separate from its label and command.</summary>
/// <remarks>
/// A symbol name rather than a <see cref="System.Windows.Media.Geometry"/>, because the
/// geometry a button needs depends on the size it draws at, and only
/// <see cref="FluentIcon"/> knows which of Fluent's per-size drawings that is. Handing
/// the template a path would freeze that decision at the call site.
/// </remarks>
public static class ActionButton
{
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.RegisterAttached(
        "Symbol", typeof(string), typeof(ActionButton), new FrameworkPropertyMetadata(null));

    public static string? GetSymbol(DependencyObject element) => (string?)element.GetValue(SymbolProperty);

    public static void SetSymbol(DependencyObject element, string? value) => element.SetValue(SymbolProperty, value);
}
