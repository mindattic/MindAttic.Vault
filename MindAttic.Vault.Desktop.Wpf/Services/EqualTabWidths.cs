using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace MindAttic.Vault.Desktop.Services;

/// <summary>
/// Makes every tab of a <see cref="TabControl"/> as wide as its widest one, so the strip reads as equal-width buttons
/// (M.VALT-T64). The width is measured from the tabs themselves (their real font, padding, status light and trailing text),
/// on load, whenever the control is resized (window resize, DPI change) and whenever tabs are added, removed or retitled.
/// <code>&lt;mc:MaTabControl services:EqualTabWidths.IsEnabled="True" ... /&gt;</code>
/// </summary>
public static class EqualTabWidths
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(EqualTabWidths), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static readonly DependencyProperty PendingProperty = DependencyProperty.RegisterAttached(
        "Pending", typeof(bool), typeof(EqualTabWidths), new PropertyMetadata(false));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TabControl tabs) return;
        if ((bool)e.NewValue)
        {
            tabs.Loaded += OnChanged;
            tabs.SizeChanged += OnChanged;
            tabs.ItemContainerGenerator.StatusChanged += (_, _) => Schedule(tabs);
            ((System.Collections.Specialized.INotifyCollectionChanged)tabs.Items).CollectionChanged += (_, _) => Schedule(tabs);
            Schedule(tabs);
        }
        else
        {
            tabs.Loaded -= OnChanged;
            tabs.SizeChanged -= OnChanged;
        }
    }

    private static void OnChanged(object sender, RoutedEventArgs e) => Schedule((TabControl)sender);

    /// <summary>Coalesces bursts (a resize drag, a reload of every tab) into one pass after layout.</summary>
    private static void Schedule(TabControl tabs)
    {
        if ((bool)tabs.GetValue(PendingProperty)) return;
        tabs.SetValue(PendingProperty, true);
        tabs.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            tabs.SetValue(PendingProperty, false);
            if (GetIsEnabled(tabs)) Apply(tabs);
        });
    }

    /// <summary>The tab containers that exist right now.</summary>
    public static IReadOnlyList<TabItem> Containers(TabControl tabs)
    {
        var list = new List<TabItem>();
        for (var i = 0; i < tabs.Items.Count; i++)
            if ((tabs.Items[i] as TabItem ?? tabs.ItemContainerGenerator.ContainerFromIndex(i)) is TabItem t) list.Add(t);
        return list;
    }

    /// <summary>
    /// Sets every tab's <see cref="FrameworkElement.MinWidth"/> to the widest tab's natural width and returns that width
    /// (0 when there are no tabs). Natural widths are measured with no minimum, so a tab that got shorter shrinks the strip.
    /// </summary>
    public static double Apply(TabControl tabs)
    {
        var items = Containers(tabs);
        if (items.Count == 0) return 0;
        var widest = WidestNaturalWidth(items);
        foreach (var t in items)
            if (Math.Abs(t.MinWidth - widest) > 0.5) t.MinWidth = widest;
        return widest;
    }

    /// <summary>The widest natural (unconstrained) width among <paramref name="items"/>, rounded up to whole pixels.</summary>
    public static double WidestNaturalWidth(IEnumerable<FrameworkElement> items)
    {
        var widest = 0.0;
        foreach (var t in items)
        {
            var min = t.MinWidth;
            t.MinWidth = 0;
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widest = Math.Max(widest, t.DesiredSize.Width);
            t.MinWidth = min;
        }
        return Math.Ceiling(widest);
    }
}
