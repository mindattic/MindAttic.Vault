using System.Text.Json.Nodes;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Desktop.ViewModels;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Tests;

[TestFixture]
public sealed class GetStartedTests
{
    private VaultSandbox sandbox = null!;
    private FakeDialogs dialogs = null!;
    private FakeKeyTester tester = null!;
    private SqliteAuditLog audit = null!;
    private NamedKeyStore store = null!;
    private Dictionary<string, string> env = null!;
    private string workspace = null!;

    [SetUp]
    public void SetUp()
    {
        sandbox = new VaultSandbox();
        dialogs = new FakeDialogs();
        tester = new FakeKeyTester();
        audit = new SqliteAuditLog();
        store = NamedKeyStore.ForRoamingRoot();
        env = new Dictionary<string, string>();
        workspace = Directory.CreateDirectory(Path.Combine(sandbox.Root, "workspace")).FullName;
    }

    [TearDown]
    public void TearDown() => sandbox.Dispose();

    private async Task<MainViewModel> Start(params Settings.Manifest.SettingsManifest[] manifests)
    {
        var context = Samples.Context(dialogs, localRoot: sandbox.Local, audit: audit);
        var sources = new GetStartedSources(() => new KeyImporter(store, name => env.TryGetValue(name, out var v) ? v : null), () => workspace);
        var catalog = new FakeCatalog();
        catalog.Manifests.AddRange(manifests);
        var main = new MainViewModel(new IProfile[] { new DesktopProfile(keys: store) }, catalog, context,
            new KeyOperations(context, tester), getStartedSources: sources);
        await main.InitializeAsync();
        return main;
    }

    [Test]
    public async Task It_shows_first_while_a_required_provider_has_no_key()
    {
        var main = await Start(Samples.Alpha, Samples.Beta);

        Assert.That(main.Tabs[0], Is.SameAs(main.GetStarted));
        Assert.That(main.SelectedTab, Is.SameAs(main.GetStarted));
        var row = main.GetStarted.Missing.Single();
        Assert.That(row.ProviderId, Is.EqualTo("claude"), "Beta's openai key is optional");
        Assert.That(row.Heading, Is.EqualTo("Claude — needed by Alpha App"));
        Assert.That(row.KeyPageUrl, Is.EqualTo(ProviderCatalog.Get("claude").KeyPageUrl));
        row.OpenKeyPageCommand.Execute(null);
        Assert.That(dialogs.Opened.Single(), Does.StartWith("https://console.anthropic.com"));
    }

    [Test]
    public async Task It_is_not_shown_when_every_required_provider_has_a_usable_key()
    {
        store.Add("claude", "key1", "sk-present");

        var main = await Start(Samples.Alpha);

        Assert.That(main.Tabs.Contains(main.GetStarted), Is.False);
        Assert.That(main.SelectedTab, Is.SameAs(main.Keys));
    }

    [Test]
    public async Task A_failed_or_disabled_key_is_not_usable()
    {
        store.Add("claude", "key1", "sk-bad");
        store.Add("claude", "key2", "sk-off");
        store.SetDisabled("claude", "key2", true);
        new KeyHealthStore(Path.Combine(sandbox.Local, "Vault")).RecordFailure("claude", "key1", KeyFailureKind.Auth, DateTimeOffset.UtcNow);

        var main = await Start(Samples.Alpha);

        Assert.That(main.GetStarted.IsNeeded, Is.True);
        Assert.That(main.Tabs[0], Is.SameAs(main.GetStarted));
    }

    [Test]
    public async Task Adding_a_working_key_tests_it_and_the_tab_disappears()
    {
        var main = await Start(Samples.Alpha);
        var row = main.GetStarted.Missing.Single();

        var result = await row.AddAndTestAsync("sk-pasted");

        Assert.That(result!.Outcome, Is.EqualTo(KeyTestOutcome.Ok));
        Assert.That(tester.Tested.Single(), Is.EqualTo(("claude", "sk-pasted")));
        Assert.That(store.GetKeys("claude").Single().Name, Is.EqualTo("key1"));
        Assert.That(main.Tabs.Contains(main.GetStarted), Is.False);
        Assert.That(main.SelectedTab, Is.Not.SameAs(main.GetStarted));
    }

    [Test]
    public async Task A_key_that_fails_its_test_keeps_the_tab()
    {
        tester.Results["sk-wrong"] = new KeyTestResult(KeyTestOutcome.KeyFailed, "Claude refused the key (HTTP 401, wrong or revoked)", KeyFailureKind.Auth);
        var main = await Start(Samples.Alpha);

        await main.GetStarted.Missing.Single().AddAndTestAsync("sk-wrong");
        await main.KeysChangedAsync();

        Assert.That(main.Tabs.Contains(main.GetStarted), Is.True);
        Assert.That(main.GetStarted.Missing.Single().TestMessage, Does.Contain("401"));
    }

    [Test]
    public async Task Environment_legion_json_and_app_copies_are_offered_and_imported_only_on_click()
    {
        env["ANTHROPIC_API_KEY"] = "sk-from-env";
        Directory.CreateDirectory(Path.Combine(workspace, "SomeProject"));
        File.WriteAllText(Path.Combine(workspace, "SomeProject", "legion.json"), """{ "apiKeys": { "openai": "sk-from-legion" } }""");
        new LlmCredentialStore(Path.Combine(sandbox.Roaming, "LLM")).SetKey("tutor-claude", "sk-tutor-copy");

        var main = await Start(Samples.Alpha);
        var candidates = main.GetStarted.Candidates.ToList();

        Assert.That(candidates.Select(c => c.Candidate.Kind),
            Is.EquivalentTo(new[] { ImportSourceKind.Environment, ImportSourceKind.LegionJson, ImportSourceKind.AppScoped }));
        Assert.That(store.GetKeys("openai"), Is.Empty, "nothing is imported until Import is clicked");
        Assert.That(candidates.Single(c => c.Candidate.Kind == ImportSourceKind.Environment).Description, Is.EqualTo("Environment variable ANTHROPIC_API_KEY"));
        Assert.That(candidates.Select(c => c.Description + c.ImportsAs), Has.None.Contains("sk-"));

        Assert.That(await main.GetStarted.ImportAsync(candidates.Single(c => c.Candidate.Kind == ImportSourceKind.LegionJson).Candidate), Is.True);
        Assert.That(store.GetKeys("openai").Single().Value, Is.EqualTo("sk-from-legion"));

        Assert.That(await main.GetStarted.ImportAsync(candidates.Single(c => c.Candidate.Kind == ImportSourceKind.Environment).Candidate), Is.True);
        await main.KeysChangedAsync();
        Assert.That(store.GetKeys("claude").Single().Value, Is.EqualTo("sk-from-env"));
        Assert.That(main.Tabs.Contains(main.GetStarted), Is.False, "Claude now has a key");

        var imports = audit.ReadAll().Where(r => r.Action == AuditActions.KeyImport).ToList();
        Assert.That(imports, Has.Count.EqualTo(2));
        Assert.That(imports.All(r => r.Names.All(n => !n.StartsWith("sk-"))), Is.True);
    }

    [Test]
    public async Task Imports_are_not_offered_on_another_Profile()
    {
        env["ANTHROPIC_API_KEY"] = "sk-from-env";
        var context = Samples.Context(dialogs, localRoot: sandbox.Local, audit: audit);
        var sources = new GetStartedSources(() => new KeyImporter(store, name => env.TryGetValue(name, out var v) ? v : null), () => workspace);
        var azure = new FakeProfile("Azure");
        var main = new MainViewModel(new IProfile[] { azure }, new FakeCatalog { Manifests = { Samples.Alpha } }, context,
            new KeyOperations(context, tester), getStartedSources: sources);

        await main.InitializeAsync();

        Assert.That(main.GetStarted.IsNeeded, Is.True);
        Assert.That(main.GetStarted.Candidates, Is.Empty);
    }

    [Test]
    public async Task The_document_tabs_are_unaffected_by_Get_Started()
    {
        var main = await Start(Samples.Alpha);

        Assert.That(main.AppTabs.Single(t => t.AppId == "Alpha").Document!["name"]!.GetValue<string>(), Is.EqualTo("alpha"));
        Assert.That(main.AppTabs.Single(t => t.AppId == "Alpha").Document, Is.InstanceOf<JsonObject>());
    }
}
