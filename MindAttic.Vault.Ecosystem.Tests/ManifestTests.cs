using MindAttic.Vault.Credentials;
using MindAttic.Vault.Settings.Manifest;
using NUnit.Framework;

namespace MindAttic.Vault.Ecosystem.Tests;

/// <summary>2. Each app's manifest: well formed, renderable, self-consistent, no secret fields.</summary>
[TestFixture, Category("Ecosystem")]
public sealed class ManifestTests
{
    [TestCaseSource(typeof(Ecosystem), nameof(Ecosystem.AppCases))]
    public void Manifest_parses_with_a_display_name_and_its_prefix(string appId)
    {
        var m = Ecosystem.App(appId).Manifest;
        Assert.Multiple(() =>
        {
            Assert.That(m.SchemaVersion, Is.EqualTo(SettingsManifest.CurrentSchemaVersion), $"{appId}: schema version");
            Assert.That(m.DisplayName, Is.Not.Null.And.Not.Empty.And.Not.EqualTo(" "), $"{appId}: displayName is blank");
            Assert.That(m.ConfigurationPrefix, Is.EqualTo(SettingsManifest.PrefixFor(m.AppId)), $"{appId}: configurationPrefix");
            Assert.That(m.Fields, Is.Not.Empty, $"{appId}: declares no settings");
        });
    }

    [TestCaseSource(typeof(Ecosystem), nameof(Ecosystem.AppCases))]
    public void Every_setting_has_a_unique_well_formed_name(string appId)
    {
        var m = Ecosystem.App(appId).Manifest;
        var problems = new List<string>();
        CheckNames(m.Fields, m.ConfigurationPrefix, problems);
        foreach (var dup in m.AllFields().GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"{dup.Key}: declared {dup.Count()} times");
        Ecosystem.AssertNone(problems, $"{appId} setting names");
    }

    private static void CheckNames(IEnumerable<SettingField> fields, string parent, List<string> problems)
    {
        var siblings = fields.ToList();
        foreach (var dup in siblings.GroupBy(f => f.Property, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"{parent}:{dup.Key}: two settings share the property name");
        foreach (var f in siblings)
        {
            if (string.IsNullOrWhiteSpace(f.Property)) problems.Add($"{parent}: a setting has no property name");
            if (f.Property.Contains(':')) problems.Add($"{f.Name}: property name contains ':'");
            if (!string.Equals(f.Name, $"{parent}:{f.Property}", StringComparison.Ordinal))
                problems.Add($"{f.Name}: name isn't its parent's name + ':' + property ({parent}:{f.Property})");
            if (string.IsNullOrWhiteSpace(f.Label)) problems.Add($"{f.Name}: no label");
            if (f.Fields is { Count: > 0 })
                CheckNames(f.Fields, f.Kind == SettingKind.Collection ? f.Name + ":*" : f.Name, problems);
        }
    }

    [TestCaseSource(typeof(Ecosystem), nameof(Ecosystem.AppCases))]
    public void Every_setting_has_a_kind_Desktop_renders_and_a_valid_group_and_order(string appId)
    {
        var problems = new List<string>();
        foreach (var f in Ecosystem.App(appId).Manifest.AllFields())
        {
            if (!Conformance.RenderableKinds.Contains(f.Kind)) problems.Add($"{f.Name}: kind {f.Kind} renders read-only");
            if (string.IsNullOrWhiteSpace(f.Group)) problems.Add($"{f.Name}: blank group");
            if (f.Order < 0) problems.Add($"{f.Name}: negative order");
            if (f.Kind is SettingKind.Object or SettingKind.Collection && f.Fields is null) problems.Add($"{f.Name}: {f.Kind} without nested fields");
            if (f.Kind == SettingKind.Path && f.PathKind is null) problems.Add($"{f.Name}: Path without a path kind");
        }
        Ecosystem.AssertNone(problems, $"{appId} kinds/groups/orders");
    }

    [TestCaseSource(typeof(Ecosystem), nameof(Ecosystem.AppCases))]
    public void Choices_and_ranges_agree_with_each_default(string appId)
    {
        var problems = new List<string>();
        foreach (var f in Ecosystem.App(appId).Manifest.AllFields())
        {
            if (f.Min is { } min && f.Max is { } max && min > max) problems.Add($"{f.Name}: minimum above maximum");
            if ((f.Min is not null || f.Max is not null) && f.Kind is not (SettingKind.Integer or SettingKind.Decimal))
                problems.Add($"{f.Name}: a range on a {f.Kind} setting");
            if (f.Kind == SettingKind.Choice)
            {
                if (f.Choices is not { Count: > 0 }) { problems.Add($"{f.Name}: Choice without choices"); continue; }
                if (f.Choices.Select(c => c.Value?.ToJsonString()).Distinct().Count() != f.Choices.Count) problems.Add($"{f.Name}: duplicate choices");
                if (f.Choices.Any(c => string.IsNullOrWhiteSpace(c.Label))) problems.Add($"{f.Name}: a choice has no label");
            }
            if (f.Choices is { Count: > 0 } && f.Kind != SettingKind.Choice) problems.Add($"{f.Name}: choices on a {f.Kind} setting");
            // The default is checked exactly like a stored value (kind, choice, range).
            if (f.Default is not null) Conformance.CheckValue(f, f.Default, f.Name + " (default)", problems);
            if (f.ItemDefault is not null && f.Fields is not null && f.ItemDefault is System.Text.Json.Nodes.JsonObject item)
                Conformance.CheckObject(f.Fields, item, f.Name + ":* (new item default)", problems);
        }
        Ecosystem.AssertNone(problems, $"{appId} choices/ranges");
    }

    [TestCaseSource(typeof(Ecosystem), nameof(Ecosystem.AppCases))]
    public void No_text_setting_has_a_secret_looking_name(string appId)
    {
        // The builder refuses these unless the property says NotSecret; a manifest only carries the result, so any hit
        // here is a NotSecret look-alike that a person should confirm.
        var hits = Ecosystem.App(appId).Manifest.AllFields()
            .Where(f => f.Kind is SettingKind.Text or SettingKind.Multiline && SettingsManifestBuilder.LooksLikeSecretName(f.Property))
            .Select(f => $"{f.Name}: looks like a secret")
            .ToList();
        Ecosystem.AssertNone(hits, $"{appId} secret-looking settings");
    }

    [TestCaseSource(typeof(Ecosystem), nameof(Ecosystem.AppCases))]
    public void Declared_credentials_are_known_providers(string appId)
    {
        var problems = new List<string>();
        var m = Ecosystem.App(appId).Manifest;
        foreach (var c in m.Credentials)
        {
            if (string.IsNullOrWhiteSpace(c.ProviderId)) { problems.Add("a credential with no provider id"); continue; }
            if (!ProviderCatalog.IsKnown(c.ProviderId)) problems.Add($"{c.ProviderId}: not in ProviderCatalog");
            else if (ProviderCatalog.Normalize(c.ProviderId) != c.ProviderId) problems.Add($"{c.ProviderId}: stored as an alias of {ProviderCatalog.Normalize(c.ProviderId)}");
        }
        foreach (var dup in m.Credentials.GroupBy(c => ProviderCatalog.IsKnown(c.ProviderId) ? ProviderCatalog.Normalize(c.ProviderId) : c.ProviderId).Where(g => g.Count() > 1))
            problems.Add($"{dup.Key}: declared {dup.Count()} times");
        Ecosystem.AssertNone(problems, $"{appId} credentials");
    }
}
