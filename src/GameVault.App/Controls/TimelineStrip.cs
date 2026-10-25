using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameVault.App.Controls;

/// <summary>
/// The save-state timeline (SPEC §7.1, §9.1): thumbnails oldest to newest in a horizontal strip. Click to
/// load; right-click or the item's "…" button to rename, pin or delete. Items are <c>TimelineItemViewModel</c>s;
/// the control template is in Themes/Generic.xaml and the item template in Themes/TimelineTemplates.xaml.
/// </summary>
public sealed partial class TimelineStrip : Control
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(object), typeof(TimelineStrip), new PropertyMetadata(null));

    public static readonly DependencyProperty ItemTemplateProperty = DependencyProperty.Register(
        nameof(ItemTemplate), typeof(DataTemplate), typeof(TimelineStrip), new PropertyMetadata(null));

    public TimelineStrip()
    {
        DefaultStyleKey = typeof(TimelineStrip);
    }

    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>Usually <c>{StaticResource TimelineItemTemplate}</c> from Themes/TimelineTemplates.xaml.</summary>
    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }
}
