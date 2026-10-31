using System.Text.Json.Nodes;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Desktop.ViewModels;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Tests;

[TestFixture]
public sealed class AppSettingsTabTests
{
    private VaultSandbox sandbox = null!;
    private FakeDialogs dialogs = null!;
    private FakeProfile profile = null!;
    private AppSettingsViewModel tab = null!;

    [SetUp]
    public async Task SetUp()
    {
        sandbox = new VaultSandbox();
        dialogs = new FakeDialogs();
        profile = new FakeProfile("Desktop");
        profile.SettingsStore.Documents["Alpha"] = new JsonObject { ["name"] = "stored", ["count"] = 4 };
        tab = new AppSettingsViewModel(Samples.Alpha, Samples.Context(dialogs, localRoot: sandbox.Local));
        await tab.LoadAsync(profile, ProfileReadiness.Ready);
    }

    [TearDown]
    public void TearDown() => sandbox.Dispose();

    private void Edit(string property, JsonNode value, string fullName)
    {
        tab.Document![property] = value;
        tab.MarkChanged(fullName);
    }

    [Test]
    public void Each_changed_field_is_tracked_once_and_marks_the_tab()
    {
        Assert.That(tab.IsDirty, Is.False);
        Assert.That(tab.Trailing, Is.Null);

        Edit("name", "x", Samples.AlphaName);
        Edit("name", "y", Samples.AlphaName);

        Assert.That(tab.ChangedFields, Is.EquivalentTo(new[] { Samples.AlphaName }));
        Assert.That(tab.Trailing, Is.EqualTo("unsaved"));
        Assert.That(tab.SaveCommand.CanExecute(null), Is.True);
    }

    [Test]
    public async Task Save_sends_only_the_changed_field_names_and_says_when_apps_see_it()
    {
        Edit("name", "new", Samples.AlphaName);
        string[]? savedNames = null;
        tab.Saved += (_, names) => savedNames = names.ToArray();

        Assert.That(await tab.SaveAsync(), Is.True);

        var save = profile.SettingsStore.Saves.Single();
        Assert.That(save.Changed, Is.EqualTo(new[] { Samples.AlphaName }));
        Assert.That(profile.SettingsStore.Documents["Alpha"]["name"]!.GetValue<string>(), Is.EqualTo("new"));
        Assert.That(profile.SettingsStore.Documents["Alpha"]["count"]!.GetValue<int>(), Is.EqualTo(4));
        Assert.That(tab.IsDirty, Is.False);
        Assert.That(tab.StatusText, Does.Contain("after the fake refresh"));
        Assert.That(savedNames, Is.EqualTo(new[] { Samples.AlphaName }));
    }

    [Test]
    public async Task Save_with_nothing_changed_does_nothing()
    {
        Assert.That(await tab.SaveAsync(), Is.False);
        Assert.That(profile.SettingsStore.Saves, Is.Empty);
    }

    [Test]
    public async Task Revert_drops_edits_and_reloads()
    {
        Edit("name", "scratch", Samples.AlphaName);

        await tab.RevertAsync();

        Assert.That(tab.IsDirty, Is.False);
        Assert.That(tab.Document!["name"]!.GetValue<string>(), Is.EqualTo("stored"));
        Assert.That(profile.SettingsStore.Saves, Is.Empty);
    }

    [Test]
    public async Task Validation_errors_block_Save()
    {
        Edit("count", 99, Samples.AlphaCount);
        tab.Validator = () => new[] { Samples.AlphaCount };

        Assert.That(await tab.SaveAsync(), Is.False);

        Assert.That(profile.SettingsStore.Saves, Is.Empty);
        Assert.That(tab.IsDirty, Is.True);
        Assert.That(tab.StatusText, Does.Contain("Fix"));
    }

    [Test]
    public async Task A_concurrency_conflict_offers_reload_and_reloads_on_yes()
    {
        Edit("name", "mine", Samples.AlphaName);
        profile.SettingsStore.ChangeElsewhere("Alpha", new JsonObject { ["name"] = "theirs" });
        var reloaded = 0;
        tab.DocumentReloaded += (_, _) => reloaded++;
        dialogs.Answers.Enqueue(true);

        Assert.That(await tab.SaveAsync(), Is.False);

        Assert.That(dialogs.Confirms.Single(), Does.Contain("Reload"));
        Assert.That(tab.Document!["name"]!.GetValue<string>(), Is.EqualTo("theirs"));
        Assert.That(tab.IsDirty, Is.False);
        Assert.That(reloaded, Is.EqualTo(1));
    }

    [Test]
    public async Task A_concurrency_conflict_keeps_the_edits_on_no()
    {
        Edit("name", "mine", Samples.AlphaName);
        profile.SettingsStore.ChangeElsewhere("Alpha", new JsonObject { ["name"] = "theirs" });
        dialogs.Answers.Enqueue(false);

        Assert.That(await tab.SaveAsync(), Is.False);

        Assert.That(tab.Document!["name"]!.GetValue<string>(), Is.EqualTo("mine"));
        Assert.That(tab.IsDirty, Is.True);
        Assert.That(profile.SettingsStore.Documents["Alpha"]["name"]!.GetValue<string>(), Is.EqualTo("theirs"));
    }

    [Test]
    public async Task Access_denied_shows_its_plain_message()
    {
        Edit("name", "mine", Samples.AlphaName);
        profile.SettingsStore.ThrowOnSave = new ProfileAccessDeniedException("You need App Configuration Data Owner on store X.");

        Assert.That(await tab.SaveAsync(), Is.False);

        Assert.That(dialogs.Errors.Single(), Is.EqualTo("You need App Configuration Data Owner on store X."));
        Assert.That(tab.IsDirty, Is.True);
    }

    [Test]
    public async Task Access_denied_on_load_becomes_the_tab_notice()
    {
        profile.SettingsStore.ThrowOnLoad = new ProfileAccessDeniedException("You need Key Vault Secrets Officer.");

        await tab.RevertAsync();

        Assert.That(tab.Document, Is.Null);
        Assert.That(tab.Notice, Is.EqualTo("You need Key Vault Secrets Officer."));
    }

    [Test]
    public void A_deep_link_to_a_known_setting_is_kept_for_the_view_and_an_unknown_app_field_refused()
    {
        string? asked = null;
        tab.ScrollRequested += (_, s) => asked = s;

        Assert.That(tab.RequestScroll(Samples.AlphaHost), Is.True);
        Assert.That(asked, Is.EqualTo(Samples.AlphaHost));
        Assert.That(tab.PendingScroll, Is.EqualTo(Samples.AlphaHost));
        Assert.That(tab.RequestScroll("MindAttic:Settings:Beta:enabled"), Is.False);
    }

    [Test]
    public async Task Keys_used_lists_the_declared_provider_with_status_lights()
    {
        profile.KeyStore.Keys["claude"] = new() { new NamedKey("key1", "v1"), new NamedKey("spare", "v2", Disabled: true) };
        await tab.LoadAsync(profile, ProfileReadiness.Ready);

        var claude = tab.Credentials.Single();
        Assert.That(claude.ProviderId, Is.EqualTo("claude"));
        Assert.That(claude.Options.Select(o => (o.Name, o.StatusLabel)), Is.EqualTo(new[] { ("key1", "Never used"), ("spare", "Disabled") }));
        Assert.That(claude.UsesSharedDefault, Is.True);
        Assert.That(claude.Summary, Is.EqualTo("Shared default: key1."));
    }

    [Test]
    public async Task Assigning_keys_writes_names_only_and_shared_default_clears()
    {
        profile.KeyStore.Keys["claude"] = new() { new NamedKey("key1", "v1"), new NamedKey("2026-10-31 Alpha", "v2"), new NamedKey("key3", "v3") };
        await tab.LoadAsync(profile, ProfileReadiness.Ready);
        var claude = tab.Credentials.Single();
        var events = new List<string[]>();
        tab.KeysAssigned += (_, _, names) => events.Add(names.ToArray());

        await claude.SetActiveAsync("2026-10-31 Alpha");
        await claude.AddFallbackAsync("key1");
        await claude.AddFallbackAsync("key3");
        await claude.MoveFallbackAsync("key3", -1);
        Assert.That(profile.KeyStore.Assignments[("Alpha", "claude")], Is.EqualTo(new[] { "2026-10-31 Alpha", "key3", "key1" }));
        Assert.That(claude.Active!.Name, Is.EqualTo("2026-10-31 Alpha"));
        Assert.That(claude.Fallbacks.Select(f => f.Name), Is.EqualTo(new[] { "key3", "key1" }));

        await claude.RemoveFallbackAsync("key3");
        Assert.That(profile.KeyStore.Assignments[("Alpha", "claude")], Is.EqualTo(new[] { "2026-10-31 Alpha", "key1" }));

        await claude.UseSharedDefaultAsync();
        Assert.That(profile.KeyStore.Assignments.ContainsKey(("Alpha", "claude")), Is.False);
        Assert.That(claude.UsesSharedDefault, Is.True);
        Assert.That(events.Last(), Is.Empty);
        Assert.That(events, Has.Count.EqualTo(6));
    }
}
