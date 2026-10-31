using System.Text.Json.Nodes;
using System.Windows;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Desktop.ViewModels;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Tests;

[TestFixture]
public sealed class DesktopStateTests
{
    private VaultSandbox sandbox = null!;

    [SetUp]
    public void SetUp() => sandbox = new VaultSandbox();

    [TearDown]
    public void TearDown() => sandbox.Dispose();

    private MainViewModel Main(DesktopStateKeeper state, params IProfile[] profiles)
    {
        var context = Samples.Context(localRoot: sandbox.Local);
        return new MainViewModel(profiles, new FakeCatalog { Manifests = { Samples.Alpha, Samples.Beta } }, context,
            new KeyOperations(context, new FakeKeyTester()), getStartedSources: Samples.NoImports, state: state);
    }

    private static FakeProfile WithClaudeKey(string id)
    {
        var p = new FakeProfile(id);
        p.KeyStore.Keys["claude"] = new() { new NamedKey("key1", "v") }; // so Get Started doesn't take the first tab
        return p;
    }

    [Test]
    public void It_lives_in_the_Vault_Desktop_roaming_bucket()
    {
        using var keeper = new DesktopStateKeeper();
        Assert.That(keeper.FilePath, Is.EqualTo(Path.Combine(sandbox.Roaming, "Vault.Desktop", "state.json")));
    }

    [Test]
    public async Task Changes_are_saved_debounced_and_flushed_on_exit()
    {
        using var keeper = new DesktopStateKeeper(debounce: TimeSpan.FromMilliseconds(100));
        keeper.Update(s => s.LastTab = "keys");
        keeper.Update(s => s.LastTab = "app:Alpha");
        Assert.That(File.Exists(keeper.FilePath), Is.False, "nothing written before the debounce");

        for (var i = 0; i < 50 && !File.Exists(keeper.FilePath); i++) await Task.Delay(50);
        Assert.That(new DesktopStateKeeper().State.LastTab, Is.EqualTo("app:Alpha"));

        keeper.Update(s => s.CollapsedGroups = new List<string> { "Brokers" });
        keeper.Flush();
        Assert.That(new DesktopStateKeeper().State.CollapsedGroups, Is.EqualTo(new[] { "Brokers" }));
    }

    [Test]
    public void A_corrupt_state_file_is_defaults()
    {
        Directory.CreateDirectory(Path.Combine(sandbox.Roaming, "Vault.Desktop"));
        File.WriteAllText(Path.Combine(sandbox.Roaming, "Vault.Desktop", "state.json"), "{ not json");

        using var keeper = new DesktopStateKeeper();

        Assert.That(keeper.State.LastProfile, Is.Null);
        Assert.That(keeper.State.CollapsedGroups, Is.Empty);
    }

    [Test]
    public async Task Profile_tab_and_collapsed_groups_are_remembered_and_restored()
    {
        var desktop = WithClaudeKey("Desktop");
        var azure = WithClaudeKey("Azure");
        using (var keeper = new DesktopStateKeeper())
        {
            var first = Main(keeper, desktop, azure);
            await first.InitializeAsync();
            await first.SwitchProfileAsync(azure);
            first.SelectedTab = first.AppTabs.Single(t => t.AppId == "Beta");
            first.Keys.Groups.Single(g => g.Title == "Brokers").IsExpanded = false;
            keeper.Update(s => s.AzureEnvironments = new JsonObject { ["prod"] = "opaque" });
        }

        using var reopened = new DesktopStateKeeper();
        var second = Main(reopened, WithClaudeKey("Desktop"), WithClaudeKey("Azure"));
        await second.InitializeAsync();

        Assert.That(second.SelectedProfile!.Id, Is.EqualTo("Azure"));
        Assert.That(second.SelectedTab!.Id, Is.EqualTo("app:Beta"));
        Assert.That(second.Keys.Groups.Single(g => g.Title == "Brokers").IsExpanded, Is.False);
        Assert.That(second.Keys.Groups.Single(g => g.Title == "LLM providers").IsExpanded, Is.True);
        Assert.That(reopened.State.AzureEnvironments!["prod"]!.GetValue<string>(), Is.EqualTo("opaque"));
    }

    [Test]
    public async Task Get_Started_wins_over_the_remembered_tab_while_a_key_is_missing()
    {
        using (var keeper = new DesktopStateKeeper()) keeper.Update(s => s.LastTab = "app:Beta");

        using var reopened = new DesktopStateKeeper();
        var main = Main(reopened, new FakeProfile("Desktop"));
        await main.InitializeAsync();

        Assert.That(main.SelectedTab, Is.SameAs(main.GetStarted));
        Assert.That(reopened.State.LastTab, Is.EqualTo("app:Beta"), "showing Get Started doesn't forget the last tab");
    }

    [Test]
    public async Task A_remembered_tab_whose_app_is_gone_falls_back_to_the_first_tab()
    {
        using (var keeper = new DesktopStateKeeper()) keeper.Update(s => s.LastTab = "app:Uninstalled");

        using var reopened = new DesktopStateKeeper();
        var main = Main(reopened, WithClaudeKey("Desktop"));
        await main.InitializeAsync();

        Assert.That(main.SelectedTab, Is.SameAs(main.Tabs[0]));
    }

    // Two monitors side by side: primary 0..1920, second 1920..3840.
    private static readonly Rect Primary = new(0, 0, 1920, 1040);
    private static readonly Rect Second = new(1920, 0, 1920, 1040);

    [Test]
    public void A_window_on_a_monitor_that_still_exists_stays_put()
    {
        var bounds = WindowPlacement.Clamp(new WindowBounds(2100, 100, 1100, 760), new[] { Primary, Second }, Primary);

        Assert.That(bounds, Is.EqualTo(new WindowBounds(2100, 100, 1100, 760)));
    }

    [Test]
    public void A_window_on_a_removed_monitor_moves_to_the_primary_one()
    {
        var bounds = WindowPlacement.Clamp(new WindowBounds(2100, 100, 1100, 760, Maximized: true), new[] { Primary }, Primary)!;

        Assert.That(Primary.Contains(new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height)), Is.True);
        Assert.That((bounds.Left, bounds.Top), Is.EqualTo((410d, 140d)), "centred");
        Assert.That(bounds.Maximized, Is.True);
    }

    [Test]
    public void A_window_mostly_off_the_edge_is_pulled_back_on()
    {
        var bounds = WindowPlacement.Clamp(new WindowBounds(1700, 900, 1100, 760), new[] { Primary }, Primary)!;

        Assert.That(bounds, Is.EqualTo(new WindowBounds(820, 280, 1100, 760)));
    }

    [Test]
    public void A_window_barely_visible_counts_as_off_screen()
    {
        var bounds = WindowPlacement.Clamp(new WindowBounds(1900, -700, 1100, 760), new[] { Primary }, Primary)!;

        Assert.That(bounds.Left, Is.EqualTo(410d));
    }

    [Test]
    public void A_window_larger_than_its_monitor_is_shrunk_to_fit()
    {
        var small = new Rect(0, 0, 1280, 680);
        var bounds = WindowPlacement.Clamp(new WindowBounds(10, 10, 3000, 2000), new[] { small }, small)!;

        Assert.That((bounds.Width, bounds.Height, bounds.Left, bounds.Top), Is.EqualTo((1280d, 680d, 0d, 0d)));
    }

    [Test]
    public void Nothing_saved_means_the_default_placement() =>
        Assert.That(WindowPlacement.Clamp(null, new[] { Primary }, Primary), Is.Null);
}
