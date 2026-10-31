using MindAttic.Vault.Settings.Manifest;
using NUnit.Framework;

namespace MindAttic.Vault.Ecosystem.Tests;

/// <summary>1. Discovery: every migrated app has a manifest on this machine, under the root its scope names.</summary>
[TestFixture, Category("Ecosystem")]
public sealed class DiscoveryTests
{
    [Test]
    public void The_real_roots_exist()
    {
        Assert.That(Directory.Exists(Ecosystem.RealRoaming), Is.True, "roaming root missing");
        Assert.That(Directory.Exists(Ecosystem.RealLocal), Is.True, "local root missing");
        TestContext.Out.WriteLine($"roaming = {Ecosystem.RealRoaming}");
        TestContext.Out.WriteLine($"local   = {Ecosystem.RealLocal}");
    }

    [Test]
    public void Discover_finds_every_expected_app()
    {
        var found = Ecosystem.Apps.Select(a => a.AppId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = Ecosystem.ExpectedApps.Where(e => !found.Contains(e)).ToList();
        foreach (var extra in found.Where(f => !Ecosystem.ExpectedApps.Contains(f, StringComparer.OrdinalIgnoreCase)))
            Ecosystem.Report($"{extra} has a manifest but isn't in the expected list");
        TestContext.Out.WriteLine($"discovered {found.Count}: {string.Join(", ", found.Order(StringComparer.OrdinalIgnoreCase))}");
        Ecosystem.AssertNone(missing.Select(m => $"{m}: no settings.manifest.json under either root").ToList(), "Missing apps");
    }

    [Test]
    public void App_ids_are_unique_across_both_roots()
    {
        // Discover keeps one per id; read every manifest directly to see duplicates it would hide.
        var all = new List<(string Root, string Folder, string AppId)>();
        foreach (var root in new[] { Ecosystem.RealRoaming, Ecosystem.RealLocal })
            foreach (var dir in Directory.EnumerateDirectories(root))
                if (SettingsManifestFile.TryRead(Path.Combine(dir, SettingsManifest.FileName)) is { } m)
                    all.Add((root, Path.GetFileName(dir), m.AppId));

        var problems = all.GroupBy(a => a.AppId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: manifests in {string.Join(" and ", g.Select(x => Path.Combine(x.Root, x.Folder)))}")
            .ToList();
        Ecosystem.AssertNone(problems, "Duplicate app ids");
    }

    [Test]
    public void Every_manifest_file_under_the_roots_is_readable()
    {
        // A manifest that TryRead rejects (bad JSON, unknown schema, no app id) silently drops the app's tab.
        var problems = new List<string>();
        foreach (var root in new[] { Ecosystem.RealRoaming, Ecosystem.RealLocal })
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var path = Path.Combine(dir, SettingsManifest.FileName);
                if (File.Exists(path) && SettingsManifestFile.TryRead(path) is null)
                    problems.Add($"{path}: exists but isn't a readable schema-{SettingsManifest.CurrentSchemaVersion} manifest");
            }
        Ecosystem.AssertNone(problems, "Unreadable manifests");
    }

    [TestCaseSource(typeof(Ecosystem), nameof(Ecosystem.AppCases))]
    public void Folder_name_is_the_app_id(string appId)
    {
        var app = Ecosystem.App(appId);
        Assert.That(app.FolderName, Is.EqualTo(app.AppId), $"{appId}: folder name differs from the manifest's appId (M.VALT-LAW-2)");
    }

    [TestCaseSource(typeof(Ecosystem), nameof(Ecosystem.AppCases))]
    public void Scope_matches_the_root_it_was_found_in(string appId)
    {
        var app = Ecosystem.App(appId);
        Assert.That(app.Manifest.Scope, Is.EqualTo(app.FoundScope).IgnoreCase,
            $"{appId}: manifest says {app.Manifest.Scope} but was found under the {app.FoundScope} root");
    }
}
