using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DpiBypass.App.Infrastructure;

/// <summary>
/// Makes the mouse wheel scroll the page the pointer is over, rather than whatever
/// control happens to be under it.
/// </summary>
/// <remarks>
/// <para>
/// WPF's default is wrong for a settings page and always has been. Every list on these
/// pages sits inside the page's own <see cref="ScrollViewer"/>, and a list handles the
/// wheel unconditionally: it scrolls if it can, and marks the event handled either way.
/// So the wheel stops working the moment the pointer crosses a list - the page freezes
/// under the cursor with no indication why, and moving the mouse a few pixels sideways
/// makes it work again. That is what "scrolling is buggy" means in practice, and it
/// affects every page here because every page has a list on it.
/// </para>
/// <para>
/// A combo box is worse than unhelpful: it answers the wheel by changing the selection.
/// Scrolling past the operator or the strategy picker silently reconfigures the engine,
/// and nothing on screen says a setting was changed by a scroll gesture that was meant
/// to move the page.
/// </para>
/// <para>
/// Both are fixed the same way: intercept the wheel before the control sees it, let the
/// control keep it only when it can genuinely use it, and otherwise hand it to the
/// parent so the page moves.
/// </para>
/// </remarks>
public static class NestedScrolling
{
    /// <summary>
    /// Lets the wheel through to the page once this control has scrolled as far as it
    /// can in that direction.
    /// </summary>
    public static readonly DependencyProperty BubbleWheelProperty =
        DependencyProperty.RegisterAttached(
            "BubbleWheel",
            typeof(bool),
            typeof(NestedScrolling),
            new PropertyMetadata(false, OnBubbleWheelChanged));

    /// <summary>
    /// Keeps the wheel away from this control entirely and gives it to the page.
    /// </summary>
    /// <remarks>
    /// For controls whose answer to the wheel is to change a value rather than to
    /// scroll. An open drop-down is the one case where the wheel does belong to the
    /// control, and it is left alone there.
    /// </remarks>
    public static readonly DependencyProperty IgnoreWheelProperty =
        DependencyProperty.RegisterAttached(
            "IgnoreWheel",
            typeof(bool),
            typeof(NestedScrolling),
            new PropertyMetadata(false, OnIgnoreWheelChanged));

    public static void SetBubbleWheel(DependencyObject element, bool value)
        => element.SetValue(BubbleWheelProperty, value);

    public static bool GetBubbleWheel(DependencyObject element)
        => (bool)element.GetValue(BubbleWheelProperty);

    public static void SetIgnoreWheel(DependencyObject element, bool value)
        => element.SetValue(IgnoreWheelProperty, value);

    public static bool GetIgnoreWheel(DependencyObject element)
        => (bool)element.GetValue(IgnoreWheelProperty);

    private static void OnBubbleWheelChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement target)
        {
            return;
        }

        target.PreviewMouseWheel -= OnBubbleWheel;

        if (e.NewValue is true)
        {
            target.PreviewMouseWheel += OnBubbleWheel;
        }
    }

    private static void OnIgnoreWheelChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement target)
        {
            return;
        }

        target.PreviewMouseWheel -= OnIgnoreWheel;

        if (e.NewValue is true)
        {
            target.PreviewMouseWheel += OnIgnoreWheel;
        }
    }

    private static void OnBubbleWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement source)
        {
            return;
        }

        // Content that fits, or that is already against the end the wheel is pushing
        // towards, has nothing to do with this gesture.
        if (FindScrollViewer(source) is { } viewer && CanScroll(viewer, e.Delta))
        {
            return;
        }

        Forward(source, e);
    }

    private static void OnIgnoreWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement source)
        {
            return;
        }

        // While the list is open the wheel is genuinely the control's: the user is
        // looking at the drop-down and scrolling through it.
        if (source is System.Windows.Controls.ComboBox { IsDropDownOpen: true })
        {
            return;
        }

        Forward(source, e);
    }

    /// <summary>
    /// Takes the wheel away from the control and raises it again on its parent, so the
    /// nearest ancestor that can scroll gets it.
    /// </summary>
    private static void Forward(UIElement source, MouseWheelEventArgs e)
    {
        e.Handled = true;

        if (VisualTreeHelper.GetParent(source) is not UIElement parent)
        {
            return;
        }

        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = source,
        });
    }

    /// <summary>Whether the viewer can still move in the wheel's direction.</summary>
    private static bool CanScroll(ScrollViewer viewer, int delta)
    {
        if (viewer.ExtentHeight <= viewer.ViewportHeight)
        {
            return false;
        }

        // A wheel notch away from the user (positive delta) scrolls up.
        return delta < 0
            ? viewer.VerticalOffset < viewer.ScrollableHeight
            : viewer.VerticalOffset > 0;
    }

    /// <summary>
    /// The scroll viewer inside a templated control, found on demand.
    /// </summary>
    /// <remarks>
    /// Looked up per gesture rather than cached: these controls live on tab pages whose
    /// templates are only realised the first time the page is visited, and a rebuilt
    /// template would strand a reference taken earlier.
    /// </remarks>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer self)
        {
            return self;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
