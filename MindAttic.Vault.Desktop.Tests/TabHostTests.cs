using System.Text.Json.Nodes;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Desktop.ViewModels;
using MindAttic.Vault.Settings;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Tests;

[TestFixture]
public sealed class TabHostTests
{
    private VaultSandbox sandbox = null!;

    [SetUp]
    public void SetUp() => sandbox = new VaultSandbox();

    [TearDown]
    public void TearDown() => sandbox.Dispose();

    private static string[] AppIds(MainViewModel vm) => vm.AppTabs.Select(t => t.AppId).ToArray();

    [Test]
    public async Task Global_comes_first_then_one_tab_per_manifest()
    {
        var catalog = new FakeCatalog { Manifests = { Samples.Beta, Samples.Alpha } };
        var vm = Samples.Main(catalog, Samples.Context(localRoot: sandbox.Local), new FakeProfile("Desktop"));

        await vm.InitializeAsync();

        Assert.That(AppIds(vm), Is.EqualTo(new[] { "Global", "Beta", "Alpha" }));
        Assert.That(catalog.Started, Is.True);
        Assert.That(vm.SelectedTab, Is.SameAs(vm.Tabs[0]));
    }

    [Test]
    public async Task Adding_and_removing_a_manifest_folder_adds_and_removes_its_tab()
    {
        var ui = new InlineDispatcher();
        using var catalog = new FileManifestCatalog(debounce: TimeSpan.FromMilliseconds(50));
        var vm = Samples.Main(catalog, Samples.Context(ui: ui, localRoot: sandbox.Local), new FakeKeyTester(), new DesktopProfile());
        await vm.InitializeAsync();
        Assert.That(AppIds(vm), Is.EqualTo(new[] { "Global" }));

        var dir = Samples.Install<AlphaSettings>(sandbox.Roaming);
        await WaitFor(ui, () => vm.AppTabs.Any(t => t.AppId == "Alpha" && t.Document is not null));
        var alpha = vm.AppTabs.Single(t => t.AppId == "Alpha");
        Assert.That(alpha.Document!["name"]!.GetValue<string>(), Is.EqualTo("alpha"), "a new tab loads its values");

        Directory.Delete(dir, recursive: true);
        await WaitFor(ui, () => !AppIds(vm).Contains("Alpha"));
    }

    [Test]
    public async Task A_refresh_without_the_watcher_syncs_in_place()
    {
        var catalog = new FakeCatalog { Manifests = { Samples.Alpha } };
        var vm = Samples.Main(catalog, Samples.Context(localRoot: sandbox.Local), new FakeProfile("Desktop"));
        await vm.InitializeAsync();
        var alphaTab = vm.AppTabs.Single(t => t.AppId == "Alpha");

        catalog.Manifests.Add(Samples.Beta);
        await vm.RefreshAppsAsync();
        Assert.That(AppIds(vm), Is.EqualTo(new[] { "Global", "Alpha", "Beta" }));
        Assert.That(vm.AppTabs.Single(t => t.AppId == "Alpha"), Is.SameAs(alphaTab), "existing tabs are kept, not rebuilt");

        catalog.Manifests.RemoveAll(m => m.AppId == "Alpha");
        await vm.RefreshAppsAsync();
        Assert.That(AppIds(vm), Is.EqualTo(new[] { "Global", "Beta" }));
    }

    [Test]
    public async Task Global_uses_the_shared_settings_manifest()
    {
        MindAtticGlobalSettings.Store().LoadOrSeed();
        using var catalog = new FileManifestCatalog();
        var vm = Samples.Main(catalog, Samples.Context(localRoot: sandbox.Local), new FakeKeyTester(), new DesktopProfile());

        await vm.InitializeAsync();

        var global = vm.AppTabs.First();
        Assert.That(global.AppId, Is.EqualTo("Global"));
        Assert.That(global.Manifest.Find("MindAttic:Settings:Global:theme"), Is.Not.Null);
        Assert.That(global.Document!["theme"]!.GetValue<string>(), Is.EqualTo("Dark"));
    }

    [Test]
    public async Task Switching_Profile_reloads_every_tab_from_the_new_backend()
    {
        var desktop = new FakeProfile("Desktop");
        var azure = new FakeProfile("Azure");
        desktop.SettingsStore.Documents["Alpha"] = new JsonObject { ["name"] = "local" };
        azure.SettingsStore.Documents["Alpha"] = new JsonObject { ["name"] = "cloud" };
        azure.SettingsStore.Documents["Global"] = new JsonObject { ["theme"] = "Light" };
        azure.KeyStore.Keys["claude"] = new() { new("cloud key", "v") };
        var vm = Samples.Main(new FakeCatalog { Manifests = { Samples.Alpha } }, Samples.Context(localRoot: sandbox.Local), desktop, azure);
        await vm.InitializeAsync();
        var alpha = vm.AppTabs.Single(t => t.AppId == "Alpha");
        Assert.That(alpha.Document!["name"]!.GetValue<string>(), Is.EqualTo("local"));

        Assert.That(await vm.SwitchProfileAsync(azure), Is.True);

        Assert.That(vm.SelectedProfile, Is.SameAs(azure));
        Assert.That(alpha.Document!["name"]!.GetValue<string>(), Is.EqualTo("cloud"));
        Assert.That(vm.AppTabs.First().Document!["theme"]!.GetValue<string>(), Is.EqualTo("Light"));
        Assert.That(alpha.Credentials.Single().Options.Select(o => o.Name), Is.EqualTo(new[] { "cloud key" }), "keys come from the new Profile too");
        Assert.That(azure.SettingsStore.Loads, Is.EqualTo(2), "every tab loaded from Azure");
    }

    [Test]
    public async Task Switching_with_unsaved_edits_asks_and_keeps_them_on_no()
    {
        var dialogs = new FakeDialogs();
        var desktop = new FakeProfile("Desktop");
        var azure = new FakeProfile("Azure");
        var vm = Samples.Main(new FakeCatalog { Manifests = { Samples.Alpha } }, Samples.Context(dialogs, localRoot: sandbox.Local), desktop, azure);
        await vm.InitializeAsync();
        var alpha = vm.AppTabs.Single(t => t.AppId == "Alpha");
        alpha.Document!["name"] = "edited";
        alpha.MarkChanged(Samples.AlphaName);

        dialogs.Answers.Enqueue(false);
        Assert.That(await vm.SwitchProfileAsync(azure), Is.False);
        Assert.That(vm.SelectedProfile, Is.SameAs(desktop));
        Assert.That(alpha.IsDirty, Is.True);
        Assert.That(dialogs.Confirms.Single(), Does.Contain("Alpha App"));

        dialogs.Answers.Enqueue(true);
        Assert.That(await vm.SwitchProfileAsync(azure), Is.True);
        Assert.That(alpha.IsDirty, Is.False);
    }

    [Test]
    public async Task A_Profile_that_is_not_ready_shows_its_message_and_no_values()
    {
        var azure = new FakeProfile("Azure") { Readiness = ProfileReadiness.NeedsSetup("Sign in to Azure first.") };
        var vm = Samples.Main(new FakeCatalog { Manifests = { Samples.Alpha } }, Samples.Context(localRoot: sandbox.Local), new FakeProfile("Desktop"), azure);
        await vm.InitializeAsync();

        await vm.SwitchProfileAsync(azure);

        Assert.That(vm.ProfileNotice, Is.EqualTo("Sign in to Azure first."));
        Assert.That(vm.AppTabs.All(t => t.Document is null && t.Notice == "Sign in to Azure first."), Is.True);
        Assert.That(azure.SettingsStore.Loads, Is.Zero);
    }

    internal static async Task WaitFor(InlineDispatcher ui, Func<bool> condition, int timeoutMs = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            lock (ui.Gate)
                if (condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail("Timed out waiting for the condition.");
    }
}
