using System.Windows;
using System.Windows.Media;

namespace DpiBypass.App.Infrastructure;

/// <summary>An optional vector icon, separate from the button label and command data.</summary>
public static class ActionButton
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(ActionButton), new FrameworkPropertyMetadata(null));

    public static Geometry? GetIcon(DependencyObject element) => (Geometry?)element.GetValue(IconProperty);

    public static void SetIcon(DependencyObject element, Geometry? value) => element.SetValue(IconProperty, value);
}
