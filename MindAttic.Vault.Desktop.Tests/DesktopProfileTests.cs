using System.Text.Json.Nodes;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Settings;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Tests;

[TestFixture]
public sealed class DesktopProfileTests
{
    private VaultSandbox sandbox = null!;
    private DesktopProfile profile = null!;

    [SetUp]
    public void SetUp()
    {
        sandbox = new VaultSandbox();
        profile = new DesktopProfile();
    }

    [TearDown]
    public void TearDown() => sandbox.Dispose();

    private string SettingsPath => Path.Combine(sandbox.Roaming, "Alpha", "settings.json");

    [Test]
    public async Task A_missing_file_loads_the_manifest_defaults()
    {
        var doc = await profile.Settings.LoadAsync(Samples.Alpha);

        Assert.That(doc.Version, Is.EqualTo(DesktopSettingsBackend.AbsentVersion));
        Assert.That(doc.Values["name"]!.GetValue<string>(), Is.EqualTo("alpha"));
        Assert.That(doc.Values["count"]!.GetValue<int>(), Is.EqualTo(3));
        Assert.That(doc.Values["connection"]!["host"]!.GetValue<string>(), Is.EqualTo("localhost"));
        Assert.That(File.Exists(SettingsPath), Is.False, "loading never writes");
    }

    [Test]
    public async Task Save_writes_only_the_changed_fields_and_keeps_everything_else()
    {
        JsonSettingsStore<AlphaSettings>.ForApp("Alpha").Save(new AlphaSettings { Name = "on disk", Count = 7 });
        var raw = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        raw["unknownToManifest"] = "keep me";
        File.WriteAllText(SettingsPath, raw.ToJsonString());

        var doc = await profile.Settings.LoadAsync(Samples.Alpha);
        doc.Values["name"] = "edited";
        doc.Values["count"] = 9;                       // edited in the document but not reported as changed
        doc.Values["connection"]!["host"] = "example.org";
        var version = await profile.Settings.SaveAsync(Samples.Alpha, doc, new[] { Samples.AlphaName, Samples.AlphaHost });

        var saved = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.That(saved["name"]!.GetValue<string>(), Is.EqualTo("edited"));
        Assert.That(saved["count"]!.GetValue<int>(), Is.EqualTo(7), "an unreported field is not written");
        Assert.That(saved["connection"]!["host"]!.GetValue<string>(), Is.EqualTo("example.org"));
        Assert.That(saved["connection"]!["port"]!.GetValue<int>(), Is.EqualTo(80), "the nested sibling stays");
        Assert.That(saved["unknownToManifest"]!.GetValue<string>(), Is.EqualTo("keep me"));
        Assert.That(version, Is.EqualTo(DesktopSettingsBackend.Hash(File.ReadAllBytes(SettingsPath))));
        Assert.That(JsonSettingsStore<AlphaSettings>.ForApp("Alpha").Load().Name, Is.EqualTo("edited"), "the app reads it back");
    }

    [Test]
    public async Task First_save_of_a_missing_file_writes_just_the_changed_field()
    {
        var doc = await profile.Settings.LoadAsync(Samples.Alpha);
        doc.Values["count"] = 5;

        await profile.Settings.SaveAsync(Samples.Alpha, doc, new[] { Samples.AlphaCount });

        var saved = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.That(saved.Select(p => p.Key), Is.EqualTo(new[] { "count" }));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(SettingsPath)!, "*.tmp"), Is.Empty, "atomic write leaves no temp file");
    }

    [Test]
    public async Task A_file_changed_since_load_is_a_concurrency_conflict()
    {
        JsonSettingsStore<AlphaSettings>.ForApp("Alpha").Save(new AlphaSettings());
        var doc = await profile.Settings.LoadAsync(Samples.Alpha);
        JsonSettingsStore<AlphaSettings>.ForApp("Alpha").Save(new AlphaSettings { Name = "someone else" });
        doc.Values["name"] = "mine";

        Assert.ThrowsAsync<ConcurrencyConflictException>(() => profile.Settings.SaveAsync(Samples.Alpha, doc, new[] { Samples.AlphaName }));
        Assert.That(JsonSettingsStore<AlphaSettings>.ForApp("Alpha").Load().Name, Is.EqualTo("someone else"));
    }

    [Test]
    public async Task A_save_after_a_save_uses_the_returned_version()
    {
        var doc = await profile.Settings.LoadAsync(Samples.Alpha);
        doc.Values["name"] = "one";
        var v1 = await profile.Settings.SaveAsync(Samples.Alpha, doc, new[] { Samples.AlphaName });
        doc.Values["name"] = "two";

        await profile.Settings.SaveAsync(Samples.Alpha, doc with { Version = v1 }, new[] { Samples.AlphaName });

        Assert.That(JsonSettingsStore<AlphaSettings>.ForApp("Alpha").Load().Name, Is.EqualTo("two"));
    }

    [TestCase("..")]
    [TestCase(".")]
    [TestCase("a/b")]
    public void An_app_id_that_leaves_the_roaming_root_is_refused(string appId)
    {
        var manifest = Samples.Alpha;
        manifest.AppId = appId;

        Assert.ThrowsAsync<InvalidOperationException>(() => profile.Settings.LoadAsync(manifest));
    }

    [TestCase(@"..\settings.json")]
    [TestCase("..")]
    [TestCase(@"C:\x.json")]
    public void A_settings_file_outside_the_app_folder_is_refused(string file)
    {
        var manifest = Samples.Alpha;
        manifest.SettingsFile = file;

        Assert.ThrowsAsync<InvalidOperationException>(() => profile.Settings.LoadAsync(manifest));
    }

    [Test]
    public void Merge_ignores_names_outside_the_prefix_and_copies_lists_whole()
    {
        var source = JsonNode.Parse("""{"items":[{"n":1},{"n":2}],"other":1}""")!.AsObject();
        var target = JsonNode.Parse("""{"items":[{"n":9}],"other":0}""")!.AsObject();

        Assert.That(SettingsDocumentMerge.Apply(source, target, "MindAttic:Settings:X", "MindAttic:Settings:Y:other"), Is.False);
        Assert.That(SettingsDocumentMerge.Apply(source, target, "MindAttic:Settings:X", "MindAttic:Settings:X:items:1:n"), Is.True);

        Assert.That(target["items"]!.ToJsonString(), Is.EqualTo("""[{"n":1},{"n":2}]"""));
        Assert.That(target["other"]!.GetValue<int>(), Is.EqualTo(0));
    }

    [Test]
    public void When_apps_see_changes_is_plain_words() =>
        Assert.That(profile.Settings.WhenAppsSeeChanges, Does.Contain("next start"));
}
