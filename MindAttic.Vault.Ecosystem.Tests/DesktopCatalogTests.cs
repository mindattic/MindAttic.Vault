using MindAttic.Vault.Configuration;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Desktop.ViewModels;
using NUnit.Framework;

namespace MindAttic.Vault.Ecosystem.Tests;

/// <summary>8. Vault.Desktop's own catalog and shell show one tab per discovered app, and each tab loads its values.</summary>
[TestFixture, Category("Ecosystem")]
public sealed class DesktopCatalogTests
{
    [Test]
    public void Desktops_catalog_lists_every_discovered_app_from_the_real_roots()
    {
        // FileManifestCatalog.Discover only reads; Start (the watchers) is never called here.
        using var catalog = new FileManifestCatalog(() => Ecosystem.RealRoaming, localRoot: () => Ecosystem.RealLocal);
        var tabs = catalog.Discover().Select(m => m.AppId).ToList();
        var discovered = Ecosystem.Apps.Select(a => a.AppId).ToList();

        Assert.That(tabs, Is.EquivalentTo(discovered), "Desktop's catalog and SettingsManifestFile.Discover disagree");
        var missing = Ecosystem.ExpectedApps.Where(e => !tabs.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
        Ecosystem.AssertNone(missing.Select(m => $"{m}: no tab").ToList(), "Desktop tabs");
    }

    [Test]
    public async Task The_shell_opens_one_tab_per_app_with_Global_first_and_every_tab_loads()
    {
        using var sandbox = new EcosystemSandbox();
        using var catalog = new FileManifestCatalog(() => sandbox.Roaming, TimeSpan.FromMilliseconds(50), () => sandbox.Local);
        var context = DesktopFakes.Context(sandbox.Local);
        var keys = new NamedKeyStore(sandbox.Roaming, new KeyAssignmentStore(Path.Combine(sandbox.Roaming, KeyAssignmentStore.Bucket)));
        var shell = new MainViewModel(new IProfile[] { new DesktopProfile(sandbox.Roaming, keys, sandbox.Local) }, catalog, context,
            new KeyOperations(context, new DesktopFakes.NoKeyTester()), getStartedSources: new GetStartedSources(() => null, () => null));

        await shell.InitializeAsync();

        var tabIds = shell.AppTabs.Select(t => t.AppId).ToList();
        Assert.That(tabIds.First(), Is.EqualTo(LampNames.GlobalApp), "Global is the first app tab");
        Assert.That(tabIds, Is.EquivalentTo(Ecosystem.Apps.Select(a => a.AppId)), "one tab per discovered app");
        Assert.That(tabIds.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(tabIds.Count), "duplicate tabs");

        var problems = shell.AppTabs.Where(t => t.Document is null).Select(t => $"{t.AppId}: tab didn't load ({t.Notice})").ToList();
        Ecosystem.AssertNone(problems, "Tabs");

        // Every app's cog (VaultSettings.Open → --app <id>) selects that app's tab.
        foreach (var app in Ecosystem.Apps)
        {
            Assert.That(await shell.OpenAsync(new Vault.Launch.VaultOpenRequest(app.AppId)), Is.True, $"{app.AppId}: --app didn't open a tab");
            var tab = shell.SelectedTab as AppSettingsViewModel;
            Assert.That(tab?.AppId, Is.EqualTo(app.AppId), $"{app.AppId}: --app selected another tab");
            // ...and --setting <full name> finds every one of its settings to scroll to.
            foreach (var field in app.Manifest.AllFields().Where(f => !f.Name.Contains(":*")))
                if (!tab!.RequestScroll(field.Name)) problems.Add($"{field.Name}: --setting doesn't find it on the tab");
            tab!.ClearPendingScroll();
        }
        Ecosystem.AssertNone(problems, "Setting deep links");
    }
}
