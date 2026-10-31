using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Settings.Manifest;
using NUnit.Framework;

namespace MindAttic.Vault.Ecosystem.Tests;

/// <summary>
/// 5. <c>AddMindAtticVaultConfiguration</c> over a temp copy of every app shows each stored setting as
/// <c>MindAttic:Settings:{AppId}:{prop}</c> with exactly the file's value, and nothing else under that app.
/// </summary>
[TestFixture, Category("Ecosystem")]
public sealed class ConfigurationProjectionTests
{
    private EcosystemSandbox sandbox = null!;
    private IConfigurationRoot configuration = null!;

    [OneTimeSetUp]
    public void CopyEveryAppAndBuild()
    {
        sandbox = new EcosystemSandbox();
        configuration = new ConfigurationBuilder()
            .AddMindAtticVaultConfiguration(s => { s.RoamingRoot = sandbox.Roaming; s.LocalRoot = sandbox.Local; s.IncludeKeys = false; })
            .Build();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        (configuration as IDisposable)?.Dispose();
        sandbox.Dispose();
    }

    [TestCaseSource(typeof(Ecosystem), nameof(Ecosystem.AppCases))]
    public void Every_stored_setting_is_exposed_under_its_full_name_with_the_files_value(string appId)
    {
        var app = Ecosystem.App(appId);
        var json = JsonNode.Parse(File.ReadAllBytes(sandbox.SettingsPathOf(app)))!.AsObject();
        var prefix = LampNames.AppSettings(app.AppId);
        Assert.That(prefix, Is.EqualTo(app.Manifest.ConfigurationPrefix), $"{appId}: LampNames and the manifest disagree on the prefix");

        var expected = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in json) Flatten(expected, $"{prefix}:{key}", value);

        var problems = new List<string>();
        foreach (var (name, value) in expected)
        {
            if (!HasKey(name)) problems.Add($"{name}: missing from IConfiguration");
            else if (!string.Equals(configuration[name], value, StringComparison.Ordinal)) problems.Add($"{name}: value differs from the file");
        }

        var actual = configuration.GetSection(prefix).AsEnumerable().Where(kv => kv.Value is not null).Select(kv => kv.Key).ToList();
        foreach (var extra in actual.Where(k => !expected.ContainsKey(k)))
            problems.Add($"{extra}: in IConfiguration but not in {appId}'s file (another folder's settings collide with this app id)");

        // Every top-level setting stored in the file resolves through its manifest name.
        foreach (var field in app.Manifest.Fields)
            if (Conformance.KeyOf(json, field.Property) is { } stored && json[stored] is JsonValue && !HasKey(field.Name))
                problems.Add($"{field.Name}: the manifest name doesn't resolve in IConfiguration");

        Ecosystem.AssertNone(problems, $"{appId} IConfiguration projection");
        TestContext.Out.WriteLine($"{appId}: {expected.Count} value(s) projected");
    }

    [TestCaseSource(typeof(Ecosystem), nameof(Ecosystem.AppCases))]
    public void The_apps_section_binds_its_scalar_settings(string appId)
    {
        var app = Ecosystem.App(appId);
        var section = configuration.GetSection(app.Manifest.ConfigurationPrefix);
        var json = JsonNode.Parse(File.ReadAllBytes(sandbox.SettingsPathOf(app)))!.AsObject();
        var problems = new List<string>();
        foreach (var field in app.Manifest.Fields.Where(f => f.Kind is SettingKind.Bool or SettingKind.Integer or SettingKind.Decimal))
        {
            var key = Conformance.KeyOf(json, field.Property);
            if (key is null || json[key] is not JsonValue v) continue;
            var text = section[field.Property];
            var ok = field.Kind switch
            {
                SettingKind.Bool => bool.TryParse(text, out var b) && b == v.GetValue<bool>(),
                _ => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                     && Conformance.TryNumber(v, out var stored) && d == stored,
            };
            if (!ok) problems.Add($"{field.Name}: doesn't bind to the stored {field.Kind}");
        }
        Ecosystem.AssertNone(problems, $"{appId} binding");
    }

    private HashSet<string>? keys;

    private bool HasKey(string name) =>
        (keys ??= configuration.AsEnumerable().Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)).Contains(name);

    /// <summary>The stock JSON configuration flattening: objects add <c>:name</c>, arrays <c>:index</c>, empty containers vanish.</summary>
    private static void Flatten(IDictionary<string, string?> sink, string path, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj) Flatten(sink, $"{path}:{key}", value);
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++) Flatten(sink, $"{path}:{i}", arr[i]);
                break;
            case null:
                sink[path] = null;
                break;
            default:
                sink[path] = node.GetValueKind() switch
                {
                    JsonValueKind.String => node.GetValue<string>(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => node.ToJsonString(),
                };
                break;
        }
    }
}
