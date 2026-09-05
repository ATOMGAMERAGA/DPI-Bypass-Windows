using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace DpiBypass.App.Infrastructure;

/// <summary>Creates a page on its first visit and retains its controls on later visits.</summary>
public static class DeferredTabContent
{
    public static readonly DependencyProperty TemplateProperty = DependencyProperty.RegisterAttached(
        "Template", typeof(DataTemplate), typeof(DeferredTabContent),
        new PropertyMetadata(null, OnTemplateChanged));

    public static DataTemplate? GetTemplate(DependencyObject element)
        => (DataTemplate?)element.GetValue(TemplateProperty);

    public static void SetTemplate(DependencyObject element, DataTemplate? value)
        => element.SetValue(TemplateProperty, value);

    private static void OnTemplateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TabItem tab) return;

        tab.RemoveHandler(Selector.SelectedEvent, new RoutedEventHandler(OnSelected));
        if (e.NewValue is DataTemplate)
        {
            tab.AddHandler(Selector.SelectedEvent, new RoutedEventHandler(OnSelected));
            if (tab.IsSelected) EnsureContent(tab);
        }
    }

    private static void OnSelected(object sender, RoutedEventArgs e)
    {
        // Selection events from lists inside a page also bubble through the tab.
        if (ReferenceEquals(sender, e.OriginalSource)) EnsureContent((TabItem)sender);
    }

    private static void EnsureContent(TabItem tab)
    {
        if (tab.Content is null && GetTemplate(tab) is { } template)
        {
            // Use the tab's inherited DataContext. Keeping the loaded tree as Content
            // also preserves scroll position, expanded details and unfinished edits.
            tab.Content = template.LoadContent();
        }
    }
}
