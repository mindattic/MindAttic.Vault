using System.Net;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Desktop.ViewModels;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Tests;

[TestFixture]
public sealed class KeysTabTests
{
    private VaultSandbox sandbox = null!;
    private FakeDialogs dialogs = null!;
    private FakeKeyTester tester = null!;
    private SqliteAuditLog audit = null!;
    private DesktopProfile profile = null!;
    private NamedKeyStore store = null!;
    private MainViewModel main = null!;
    private KeyHealthStore health = null!;

    [SetUp]
    public async Task SetUp()
    {
        sandbox = new VaultSandbox();
        dialogs = new FakeDialogs();
        tester = new FakeKeyTester();
        audit = new SqliteAuditLog();
        store = NamedKeyStore.ForRoamingRoot();
        profile = new DesktopProfile(keys: store);
        var context = Samples.Context(dialogs, localRoot: sandbox.Local, audit: audit);
        health = context.Health;
        main = Samples.Shell(new FakeCatalog { Manifests = { Samples.Alpha, Samples.Beta } }, context, tester, profile);
        await main.InitializeAsync();
    }

    [TearDown]
    public void TearDown() => sandbox.Dispose();

    private ProviderKeysViewModel Claude => main.Keys.Provider("claude")!;
    private KeyRowViewModel Row(string name) => Claude.Keys.Single(k => k.Name == name);

    private async Task Add(string name, string value)
    {
        Claude.NewKeyName = name;
        Assert.That(await Claude.AddAsync(value), Is.True);
        await main.KeysChangedAsync();
    }

    [Test]
    public void Keys_comes_before_Global_and_groups_providers_by_category()
    {
        Assert.That(main.Tabs.Select(t => t.Id).Where(id => id != "getstarted").Take(3), Is.EqualTo(new[] { "keys", "credentials", "app:Global" }));
        Assert.That(main.Keys.Groups.Select(g => g.Title), Is.EqualTo(new[] { "LLM providers", "Brokers", "Text to speech", "Media", "Other services" }));
        Assert.That(main.Keys.Groups[0].Providers.Select(p => p.Id), Does.Contain("claude").And.Contain("openai"));
        Assert.That(main.Keys.Provider("alpaca")!.Info.Category, Is.EqualTo(ProviderCategory.Broker));
    }

    [Test]
    public async Task Providers_with_keys_outside_the_catalog_get_a_section()
    {
        store.Add("acme-ai", "key1", "acme-value");

        await main.KeysChangedAsync();

        Assert.That(main.Keys.Provider("acme-ai")!.Keys.Single().Name, Is.EqualTo("key1"));
    }

    [Test]
    public async Task Add_stores_the_key_names_it_and_audits_without_the_value()
    {
        await Add("2026-10-31 Alpha", "sk-add-value");
        Claude.NewKeyName = "";
        Assert.That(await Claude.AddAsync("sk-second"), Is.True);
        await main.KeysChangedAsync();

        Assert.That(store.GetKeys("claude").Select(k => (k.Name, k.Value)),
            Is.EqualTo(new[] { ("2026-10-31 Alpha", "sk-add-value"), ("key1", "sk-second") }));
        Assert.That(Row("2026-10-31 Alpha").ReadSecret(), Is.EqualTo("sk-add-value"));
        Assert.That(Row("key1").StatusLabel, Is.EqualTo("Never used"));
        var rows = audit.ReadAll().Where(r => r.Action == AuditActions.KeyAdd).ToList();
        Assert.That(rows.Select(r => r.Names.Single()), Is.EqualTo(new[] { "2026-10-31 Alpha", "key1" }));
        Assert.That(rows.All(r => r.Detail == "value set"), Is.True);
    }

    [Test]
    public async Task A_bad_name_or_empty_value_is_refused_with_a_plain_message()
    {
        Claude.NewKeyName = "has:colon";
        Assert.That(await Claude.AddAsync("v"), Is.False);
        Claude.NewKeyName = "ok";
        Assert.That(await Claude.AddAsync("  "), Is.False);
        await Add("dup", "v1");
        Claude.NewKeyName = "DUP";
        Assert.That(await Claude.AddAsync("v2"), Is.False);

        Assert.That(dialogs.Errors, Has.Count.EqualTo(3));
        Assert.That(dialogs.Errors[0], Does.Contain("':'"));
        Assert.That(dialogs.Errors[2], Does.Contain("already exists"));
        Assert.That(store.GetKeys("claude").Select(k => k.Name), Is.EqualTo(new[] { "dup" }));
    }

    [Test]
    public async Task Rename_keeps_health_and_assignments_following_the_key()
    {
        await Add("old", "v1");
        store.Assignments.Set("Alpha", "claude", new[] { "old" });
        health.RecordSuccess("claude", "old", DateTimeOffset.UtcNow);

        Assert.That(await Row("old").RenameAsync("new"), Is.True);
        await main.KeysChangedAsync();

        Assert.That(store.GetKeys("claude").Single().Name, Is.EqualTo("new"));
        Assert.That(store.Assignments.Get("Alpha", "claude"), Is.EqualTo(new[] { "new" }));
        Assert.That(Row("new").Status, Is.EqualTo(KeyStatus.Healthy));
        Assert.That(audit.ReadAll().Last().Names, Is.EqualTo(new[] { "old", "new" }));
        var alphaKeys = main.AppTabs.Single(t => t.AppId == "Alpha").Credentials.Single();
        Assert.That(alphaKeys.Active!.Name, Is.EqualTo("new"), "the app tab's picker follows");
    }

    [Test]
    public async Task Removing_an_assigned_key_is_blocked_and_names_the_apps()
    {
        await Add("in use", "v1");
        store.Assignments.Set("Alpha", "claude", new[] { "in use" });
        store.Assignments.Set("Beta", "claude", new[] { "in use" });

        Assert.That(await Row("in use").RemoveAsync(), Is.False);

        Assert.That(dialogs.Errors.Single(), Does.Contain("assigned to Alpha, Beta"));
        Assert.That(store.GetKeys("claude"), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Removing_asks_first_and_clears_health()
    {
        await Add("gone", "v1");
        health.RecordFailure("claude", "gone", KeyFailureKind.Auth, DateTimeOffset.UtcNow);

        dialogs.Answers.Enqueue(false);
        Assert.That(await Row("gone").RemoveAsync(), Is.False);
        Assert.That(store.GetKeys("claude"), Has.Count.EqualTo(1));

        Assert.That(await Row("gone").RemoveAsync(), Is.True);
        await main.KeysChangedAsync();
        Assert.That(store.GetKeys("claude"), Is.Empty);
        Assert.That(health.GetAll("claude").ContainsKey("gone"), Is.False);
    }

    [Test]
    public async Task Disable_and_enable_round_trip_and_show_on_the_light()
    {
        await Add("k", "v1");

        await Row("k").SetDisabledAsync(true);
        await main.KeysChangedAsync();
        Assert.That(store.GetKeys("claude").Single().Disabled, Is.True);
        Assert.That(Row("k").StatusLabel, Is.EqualTo("Disabled"));
        Assert.That(Row("k").DisableLabel, Is.EqualTo("Enable"));

        await Row("k").SetDisabledAsync(false);
        await main.KeysChangedAsync();
        Assert.That(store.GetKeys("claude").Single().Disabled, Is.False);
        Assert.That(audit.ReadAll().Select(r => r.Action), Does.Contain(AuditActions.KeyDisable).And.Contain(AuditActions.KeyEnable));
    }

    [Test]
    public async Task Up_and_down_reorder_the_shared_failover_order()
    {
        await Add("a", "1");
        await Add("b", "2");
        await Add("c", "3");
        Assert.That(Row("a").CanMoveUp, Is.False);
        Assert.That(Row("c").CanMoveDown, Is.False);

        await Row("c").MoveAsync(-1);
        await main.KeysChangedAsync();
        await Row("a").MoveAsync(+1);
        await main.KeysChangedAsync();

        Assert.That(store.GetKeys("claude").Select(k => k.Name), Is.EqualTo(new[] { "c", "a", "b" }));
        Assert.That(Claude.Keys.Select(k => k.Name), Is.EqualTo(new[] { "c", "a", "b" }));
    }

    [Test]
    public async Task A_new_value_resets_the_keys_health()
    {
        await Add("k", "old-value");
        health.RecordFailure("claude", "k", KeyFailureKind.Auth, DateTimeOffset.UtcNow);
        await main.KeysChangedAsync();
        Assert.That(Row("k").Status, Is.EqualTo(KeyStatus.Failed));
        Assert.That(main.Keys.Status, Is.EqualTo(Components.Wpf.Controls.MaStatus.Bad));

        Assert.That(await Row("k").SetValueAsync("new-value"), Is.True);
        await main.KeysChangedAsync();

        Assert.That(store.GetKeys("claude").Single().Value, Is.EqualTo("new-value"));
        Assert.That(Row("k").Status, Is.EqualTo(KeyStatus.NeverUsed));
        Assert.That(audit.ReadAll().Last().Detail, Is.EqualTo("value changed"));
    }

    [Test]
    public async Task Test_success_marks_healthy_and_failure_marks_failed()
    {
        await Add("good", "sk-good");
        await Add("bad", "sk-bad");
        tester.Results["sk-bad"] = new KeyTestResult(KeyTestOutcome.KeyFailed, "Claude refused the key (HTTP 401, wrong or revoked)", KeyFailureKind.Auth);
        health.RecordFailure("claude", "good", KeyFailureKind.Auth, DateTimeOffset.UtcNow);

        var ok = await Row("good").TestAsync();
        await main.KeysChangedAsync();
        var bad = await Row("bad").TestAsync();
        await main.KeysChangedAsync();

        Assert.That(ok.Outcome, Is.EqualTo(KeyTestOutcome.Ok));
        Assert.That(bad.Outcome, Is.EqualTo(KeyTestOutcome.KeyFailed));
        Assert.That(Row("good").Status, Is.EqualTo(KeyStatus.Healthy), "a passing test clears an old failure");
        Assert.That(Row("bad").Status, Is.EqualTo(KeyStatus.Failed));
        Assert.That(Claude.TestMessage, Does.Contain("401"));
        Assert.That(tester.Tested, Is.EqualTo(new[] { ("claude", "sk-good"), ("claude", "sk-bad") }));
    }

    [Test]
    public async Task Used_by_lists_assigned_apps_and_apps_on_the_shared_order()
    {
        await Add("k1", "1");
        await Add("k2", "2");
        store.Assignments.Set("Alpha", "claude", new[] { "k2" });
        await main.KeysChangedAsync();

        Assert.That(Row("k2").UsedBy, Is.EqualTo("Used by Alpha App"));
        Assert.That(Row("k1").UsedBy, Is.EqualTo("Not used by any app"));
        Assert.That(Claude.Summary, Is.EqualTo("2 keys · used by Alpha App"));

        store.Assignments.Set("Alpha", "claude", Array.Empty<string>());
        await main.KeysChangedAsync();
        Assert.That(Row("k1").UsedBy, Is.EqualTo("Used by Alpha App (shared)"));
    }

    [Test]
    public void Collapsing_a_group_is_remembered_and_reported()
    {
        var raised = 0;
        main.Keys.CollapsedGroupsChanged += (_, _) => raised++;

        main.Keys.Groups[1].IsExpanded = false;

        Assert.That(main.Keys.CollapsedGroups, Is.EquivalentTo(new[] { "Brokers" }));
        Assert.That(raised, Is.EqualTo(1));
    }
}

[TestFixture]
public sealed class ProviderKeyTesterTests
{
    private sealed class ScriptedHandler(HttpStatusCode status, Action<HttpRequestMessage>? seen = null, Exception? fail = null) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            seen?.Invoke(request);
            if (fail is not null) return Task.FromException<HttpResponseMessage>(fail);
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.TooManyRequests) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        }
    }

    [Test]
    public async Task Claude_lists_models_with_x_api_key_and_the_anthropic_version()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK);
        var result = await new ProviderKeyTester(handler).TestAsync("anthropic", "sk-ant-test");

        Assert.That(result.Outcome, Is.EqualTo(KeyTestOutcome.Ok));
        Assert.That(handler.Last!.RequestUri!.ToString(), Is.EqualTo("https://api.anthropic.com/v1/models"));
        Assert.That(handler.Last.Headers.GetValues("x-api-key").Single(), Is.EqualTo("sk-ant-test"));
        Assert.That(handler.Last.Headers.GetValues("anthropic-version").Single(), Is.EqualTo(ProviderKeyTester.AnthropicVersion));
        Assert.That(handler.Last.Method, Is.EqualTo(HttpMethod.Get));
    }

    [Test]
    public async Task OpenAI_uses_a_bearer_token_and_Gemini_the_key_parameter()
    {
        var openai = new ScriptedHandler(HttpStatusCode.OK);
        await new ProviderKeyTester(openai).TestAsync("openai", "sk-oa");
        Assert.That(openai.Last!.RequestUri!.ToString(), Is.EqualTo("https://api.openai.com/v1/models"));
        Assert.That(openai.Last.Headers.Authorization!.ToString(), Is.EqualTo("Bearer sk-oa"));

        var gemini = new ScriptedHandler(HttpStatusCode.OK);
        await new ProviderKeyTester(gemini).TestAsync("gemini", "AIza key");
        Assert.That(gemini.Last!.RequestUri!.AbsoluteUri, Is.EqualTo("https://generativelanguage.googleapis.com/v1beta/models?key=AIza%20key"));
    }

    [TestCase(HttpStatusCode.Unauthorized, KeyFailureKind.Auth)]
    [TestCase(HttpStatusCode.Forbidden, KeyFailureKind.Auth)]
    [TestCase(HttpStatusCode.PaymentRequired, KeyFailureKind.Quota)]
    [TestCase(HttpStatusCode.TooManyRequests, KeyFailureKind.RateLimited)]
    public async Task Refusals_are_classified_like_KeySelector(HttpStatusCode status, KeyFailureKind kind)
    {
        var result = await new ProviderKeyTester(new ScriptedHandler(status)).TestAsync("openai", "sk-x");

        Assert.That(result.Outcome, Is.EqualTo(KeyTestOutcome.KeyFailed));
        Assert.That(result.FailureKind, Is.EqualTo(kind));
        Assert.That(result.Message, Does.Not.Contain("sk-x"));
        if (kind == KeyFailureKind.RateLimited) Assert.That(result.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public async Task Server_errors_and_network_failures_do_not_blame_the_key()
    {
        var server = await new ProviderKeyTester(new ScriptedHandler(HttpStatusCode.InternalServerError)).TestAsync("claude", "k");
        var network = await new ProviderKeyTester(new ScriptedHandler(HttpStatusCode.OK,
            fail: new HttpRequestException("https://generativelanguage.googleapis.com/v1beta/models?key=SECRET"))).TestAsync("gemini", "SECRET");

        Assert.That(server.Outcome, Is.EqualTo(KeyTestOutcome.Inconclusive));
        Assert.That(network.Outcome, Is.EqualTo(KeyTestOutcome.Inconclusive));
        Assert.That(network.Message, Does.Not.Contain("SECRET"));
    }

    [Test]
    public async Task Providers_without_a_known_endpoint_say_so()
    {
        var tester = new ProviderKeyTester(new ScriptedHandler(HttpStatusCode.OK));

        Assert.That(tester.CanTest("perplexity"), Is.False);
        Assert.That((await tester.TestAsync("perplexity", "k")).Message, Is.EqualTo("Test not available"));
        Assert.That(tester.CanTest("claude"), Is.True);
    }
}
