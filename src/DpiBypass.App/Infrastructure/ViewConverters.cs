using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DpiBypass.Core.MobileHotspot;

namespace DpiBypass.App.Infrastructure;

/// <summary>Hides an element whose text has nothing to say, so the layout does not gap.</summary>
public sealed class EmptyStringToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Preserves the progress slot when work is idle, preventing layout jumps.</summary>
public sealed class BooleanToHiddenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Hidden;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>The inverse of <see cref="BooleanToVisibilityConverter"/>, for empty states.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// The colour for a status severity key.
/// </summary>
/// <remarks>
/// Colour only. Every place this is used also carries the meaning in words, because a
/// user who cannot distinguish the two greens still has to be able to tell a verified
/// improvement from a run that only watched.
/// </remarks>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "ok" => "AppSuccessBrush",
            "warn" => "AppDangerBrush",
            "attention" => "AppWarningBrush",
            "off" => "AppTextTertiaryBrush",
            _ => "AppTextSecondaryBrush",
        };

        return Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// The colour for one diagnostic check's outcome.
/// </summary>
/// <remarks>
/// "Not supported", "not used", "not measured" and "failed" are four different answers
/// and only the last is a fault, so only the last is red. Each card also states its
/// outcome in words next to the colour.
/// </remarks>
public sealed class HotspotCheckStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is HotspotCheckState state
            ? state switch
            {
                HotspotCheckState.Ok => "AppSuccessBrush",
                HotspotCheckState.Warning => "AppWarningBrush",
                HotspotCheckState.Failed => "AppDangerBrush",
                _ => "AppTextTertiaryBrush",
            }
            : "AppTextSecondaryBrush";

        return Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Dims a result card that has no number in it, without hiding the label.</summary>
public sealed class MeasuredToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.55;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
