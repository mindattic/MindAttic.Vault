using System.Text.Json.Nodes;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Desktop.ViewModels;
using MindAttic.Vault.Settings.Manifest;
using NUnit.Framework;

namespace MindAttic.Vault.Ecosystem.Tests;

/// <summary>
/// 4. Saving from Vault.Desktop never corrupts an app's file: a temp copy of each app is loaded into the real
/// <see cref="AppSettingsViewModel"/> over the Desktop Profile, every setting is marked changed (as if each one were
/// edited and put back), saved, and the file must hold exactly the same JSON as before.
/// </summary>
[TestFixture, Category("Ecosystem")]
public sealed class RoundTripTests
{
    [TestCaseSource(typeof(Ecosystem), nameof(Ecosystem.AppCases))]
    public async Task Saving_every_setting_unchanged_from_Desktop_keeps_the_file_content(string appId)
    {
        var app = Ecosystem.App(appId);
        using var sandbox = new EcosystemSandbox(only: new[] { app });
        var path = sandbox.SettingsPathOf(app);
        Assert.That(File.Exists(path), Is.True, $"{appId}: no settings file to round-trip");
        var before = File.ReadAllBytes(path);
        var original = JsonNode.Parse(before);

        var (tab, _) = await OpenTabAsync(app, sandbox);
        Assert.That(tab.Document, Is.Not.Null, $"{appId}: Desktop didn't load the file ({tab.Notice})");
        Assert.That(JsonNode.DeepEquals(tab.Document, original), Is.True, $"{appId}: Desktop's loaded document differs from the file");

        var names = app.Manifest.AllFields().Select(f => f.Name).Where(n => !n.Contains(":*")).ToList();
        foreach (var name in names) tab.MarkChanged(name);
        Assert.That(await tab.SaveAsync(), Is.True, $"{appId}: Save refused ({tab.StatusText})");

        var after = File.ReadAllBytes(path);
        var saved = JsonNode.Parse(after);
        if (!JsonNode.DeepEquals(saved, original))
            Assert.Fail($"{appId}: saving {names.Count} unchanged setting(s) changed the file at: {string.Join(", ", Differences(original, saved, app.Manifest.ConfigurationPrefix))}");
        if (!before.AsSpan().SequenceEqual(after))
            TestContext.Out.WriteLine($"{appId}: same JSON, different bytes (Desktop re-indents/escapes on save)");

        // Opening it again shows the same values (the saved file is still what Desktop reads).
        var (again, _) = await OpenTabAsync(app, sandbox);
        Assert.That(JsonNode.DeepEquals(again.Document, original), Is.True, $"{appId}: reloading after save shows different values");
    }

    [TestCaseSource(typeof(Ecosystem), nameof(Ecosystem.AppCases))]
    public async Task Editing_one_setting_from_Desktop_changes_only_that_setting(string appId)
    {
        var app = Ecosystem.App(appId);
        using var sandbox = new EcosystemSandbox(only: new[] { app });
        var path = sandbox.SettingsPathOf(app);
        var original = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();

        // A top-level setting stored in the file that Desktop can edit; put its default in (or flip a bool).
        var stored = app.Manifest.Fields.Where(f => !f.ReadOnly && Conformance.KeyOf(original, f.Property) is not null).ToList();
        var field = stored.FirstOrDefault(f => f.Kind == SettingKind.Bool && original[Conformance.KeyOf(original, f.Property)!] is JsonValue)
                    ?? stored.FirstOrDefault(f => f.Default is not null)
                    ?? stored.FirstOrDefault(f => f.Kind is SettingKind.Text or SettingKind.Path or SettingKind.Url or SettingKind.Multiline);
        if (field is null) Assert.Ignore($"{appId}: no stored, editable top-level setting");
        var key = Conformance.KeyOf(original, field!.Property)!;
        JsonNode? newValue = field.Kind == SettingKind.Bool ? JsonValue.Create(!original[key]!.GetValue<bool>())
            : field.Default is not null ? field.Default.DeepClone()
            : JsonValue.Create("ecosystem-test-value");

        var (tab, _) = await OpenTabAsync(app, sandbox);
        tab.Document![key] = newValue?.DeepClone();
        tab.MarkChanged(field.Name);
        Assert.That(await tab.SaveAsync(), Is.True, $"{appId}: Save refused ({tab.StatusText})");

        var expected = original.DeepClone().AsObject();
        expected[key] = newValue?.DeepClone();
        var saved = JsonNode.Parse(File.ReadAllBytes(path));
        if (!JsonNode.DeepEquals(saved, expected))
            Assert.Fail($"{appId}: editing {field.Name} also changed: {string.Join(", ", Differences(expected, saved, app.Manifest.ConfigurationPrefix))}");
    }

    internal static async Task<(AppSettingsViewModel Tab, DesktopProfile Profile)> OpenTabAsync(DiscoveredApp app, EcosystemSandbox sandbox)
    {
        var keys = new NamedKeyStore(sandbox.Roaming, new KeyAssignmentStore(Path.Combine(sandbox.Roaming, KeyAssignmentStore.Bucket)));
        var profile = new DesktopProfile(sandbox.Roaming, keys, sandbox.Local);
        var tab = new AppSettingsViewModel(app.Manifest, DesktopFakes.Context(sandbox.Local));
        await tab.LoadAsync(profile, ProfileReadiness.Ready);
        return (tab, profile);
    }

    /// <summary>Paths where two documents differ (names only).</summary>
    internal static IEnumerable<string> Differences(JsonNode? a, JsonNode? b, string path)
    {
        if (JsonNode.DeepEquals(a, b)) yield break;
        if (a is JsonObject oa && b is JsonObject ob)
        {
            foreach (var key in oa.Select(p => p.Key).Union(ob.Select(p => p.Key)))
                foreach (var d in Differences(oa[key], ob[key], $"{path}:{key}")) yield return d;
            var orderA = oa.Select(p => p.Key).ToList();
            var orderB = ob.Select(p => p.Key).ToList();
            if (orderA.SequenceEqual(orderB) is false && orderA.Order().SequenceEqual(orderB.Order())) yield return $"{path} (property order)";
            yield break;
        }
        if (a is JsonArray aa && b is JsonArray ab && aa.Count == ab.Count)
        {
            for (var i = 0; i < aa.Count; i++)
                foreach (var d in Differences(aa[i], ab[i], $"{path}:{i}")) yield return d;
            yield break;
        }
        yield return path;
    }
}
