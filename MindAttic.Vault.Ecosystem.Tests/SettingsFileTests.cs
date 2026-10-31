using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace MindAttic.Vault.Ecosystem.Tests;

/// <summary>3. Each app's real settings file: present, valid JSON, every value fits its manifest, no secrets stored.</summary>
[TestFixture, Category("Ecosystem")]
public sealed class SettingsFileTests
{
    /// <summary>Reads an app's real settings file (read-only, shared so a running app can keep writing it).</summary>
    public static JsonObject ReadSettings(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var node = JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return node as JsonObject ?? throw new AssertionException($"{path}: the root isn't a JSON object");
    }

    [TestCaseSource(typeof(Ecosystem), nameof(Ecosystem.AppCases))]
    public void Settings_file_exists_next_to_the_manifest_and_parses(string appId)
    {
        var app = Ecosystem.App(appId);
        Assert.That(File.Exists(app.SettingsPath), Is.True, $"{appId}: no {Path.GetFileName(app.SettingsPath)} beside its manifest");
        try { ReadSettings(app.SettingsPath); }
        catch (JsonException ex) { Assert.Fail($"{appId}: settings file isn't valid JSON (line {ex.LineNumber}, byte {ex.BytePositionInLine})"); }
    }

    [TestCaseSource(typeof(Ecosystem), nameof(Ecosystem.AppCases))]
    public void Every_stored_value_fits_its_setting(string appId)
    {
        var app = Ecosystem.App(appId);
        var json = ReadOrSkip(app);
        var problems = new List<string>();
        Conformance.CheckObject(app.Manifest.Fields, json, app.Manifest.ConfigurationPrefix, problems);
        Ecosystem.AssertNone(problems, $"{appId} values that don't fit the manifest");

        var missing = app.Manifest.Fields.Where(f => Conformance.KeyOf(json, f.Property) is null).Select(f => f.Name).ToList();
        if (missing.Count > 0) Ecosystem.Report($"{appId}: {missing.Count} setting(s) not in the file yet (the app's defaults apply): {string.Join(", ", missing)}");
    }

    [TestCaseSource(typeof(Ecosystem), nameof(Ecosystem.AppCases))]
    public void Stored_keys_are_all_declared(string appId)
    {
        var app = Ecosystem.App(appId);
        var json = ReadOrSkip(app);
        var undeclared = new List<string>();
        Conformance.Undeclared(app.Manifest.Fields, json, app.Manifest.ConfigurationPrefix, undeclared);
        // Reported, not failed: an app may keep [SettingIgnore]/non-setting state in the same file. Desktop keeps them
        // untouched on save (see RoundTripTests).
        foreach (var name in undeclared) Ecosystem.Report($"{appId}: {name} is in the file but not in the manifest");
        if (undeclared.Count == 0) TestContext.Out.WriteLine($"{appId}: every stored key is declared");
    }

    [TestCaseSource(typeof(Ecosystem), nameof(Ecosystem.AppCases))]
    public void No_secret_looking_values_are_stored(string appId)
    {
        var app = Ecosystem.App(appId);
        var json = ReadOrSkip(app);
        var hits = SecretScanner.Scan(json, app.Manifest.ConfigurationPrefix).Select(h => $"{h.Path}: {h.Kind}").ToList();
        Ecosystem.AssertNone(hits, $"{appId} secret-looking values in settings");
    }

    private static JsonObject ReadOrSkip(DiscoveredApp app)
    {
        if (!File.Exists(app.SettingsPath)) Assert.Fail($"{app.AppId}: no settings file");
        try { return ReadSettings(app.SettingsPath); }
        catch (JsonException) { Assert.Fail($"{app.AppId}: settings file isn't valid JSON"); throw; }
    }
}
