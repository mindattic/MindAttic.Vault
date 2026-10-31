using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MindAttic.Vault.Desktop.Services;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Tests;

/// <summary>M.VALT-T64: every Vault tab is as wide as the longest name, recomputed as tabs change and on resize.</summary>
[TestFixture, Apartment(ApartmentState.STA)]
public sealed class EqualTabWidthsTests
{
    private static TabControl Strip(params string[] names)
    {
        var tabs = new TabControl();
        foreach (var n in names) tabs.Items.Add(new TabItem { Header = n });
        return tabs;
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    [Test]
    public void Apply_GivesEveryTabTheWidestTabsWidth()
    {
        var tabs = Strip("Keys", "MindAttic Orchestrator", "Tutor");
        var widest = EqualTabWidths.Apply(tabs);

        var items = EqualTabWidths.Containers(tabs);
        Assert.That(widest, Is.GreaterThan(0));
        Assert.That(items.Select(t => t.MinWidth), Is.All.EqualTo(widest));
        // The longest name sets it: it is wider than the short name's own natural width.
        Assert.That(widest, Is.GreaterThan(EqualTabWidths.WidestNaturalWidth(new[] { new TabItem { Header = "Keys" } })));
    }

    [Test]
    public void Apply_ShrinksWhenTheLongestTabGoesAway()
    {
        var tabs = Strip("Keys", "A very long application display name", "Tutor");
        var before = EqualTabWidths.Apply(tabs);
        tabs.Items.RemoveAt(1);
        var after = EqualTabWidths.Apply(tabs);

        Assert.That(after, Is.LessThan(before));
        Assert.That(EqualTabWidths.Containers(tabs).Select(t => t.MinWidth), Is.All.EqualTo(after));
    }

    [Test]
    public void Apply_GrowsWhenALongerTabIsAdded()
    {
        var tabs = Strip("Keys", "Tutor");
        var before = EqualTabWidths.Apply(tabs);
        tabs.Items.Add(new TabItem { Header = "MindAttic Orchestrator settings" });
        var after = EqualTabWidths.Apply(tabs);

        Assert.That(after, Is.GreaterThan(before));
        Assert.That(EqualTabWidths.Containers(tabs).Select(t => t.MinWidth), Is.All.EqualTo(after));
    }

    [Test]
    public void Apply_NoTabs_IsZero() => Assert.That(EqualTabWidths.Apply(new TabControl()), Is.Zero);

    [Test]
    public void Enabled_AppliesOnLoad_AndAgainWhenTabsChange()
    {
        var tabs = Strip("Keys", "Tutor");
        var window = new Window { Content = tabs, Width = 600, Height = 300, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        EqualTabWidths.SetIsEnabled(tabs, true);
        window.Show();
        try
        {
            Pump();
            var items = EqualTabWidths.Containers(tabs);
            Assert.That(items.Select(t => t.MinWidth).Distinct().Count(), Is.EqualTo(1));
            var first = items[0].MinWidth;
            Assert.That(first, Is.GreaterThan(0));

            tabs.Items.Add(new TabItem { Header = "A much longer tab name than the rest" });
            Pump();
            var now = EqualTabWidths.Containers(tabs);
            Assert.That(now.Select(t => t.MinWidth).Distinct().Count(), Is.EqualTo(1));
            Assert.That(now[0].MinWidth, Is.GreaterThan(first));

            window.Width = 900; // a resize re-runs it and keeps them equal
            Pump();
            Assert.That(EqualTabWidths.Containers(tabs).Select(t => t.MinWidth).Distinct().Count(), Is.EqualTo(1));
        }
        finally { window.Close(); }
    }
}
