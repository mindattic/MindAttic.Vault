using System.Text.Json;
using System.Text.Json.Nodes;
using MindAttic.Vault.Credentials;
using NUnit.Framework;

namespace MindAttic.Vault.Ecosystem.Tests;

/// <summary>
/// 6. The real named keys and assignments, read-only: every provider is known, every assignment names a real key, every
/// app's required provider has a usable key, and Vault left no <c>.bak</c> copy of a key file. Only names are printed.
/// </summary>
[TestFixture, Category("Ecosystem")]
public sealed class CredentialTests
{
    private static string AssignmentsDir => Path.Combine(Ecosystem.RealRoaming, KeyAssignmentStore.Bucket);
    private static string AssignmentsFile => Path.Combine(AssignmentsDir, KeyAssignmentStore.FileName);

    /// <summary>The real stores. Reads only: GetKeys/Get/Plan never write.</summary>
    private static NamedKeyStore Keys() => new(Ecosystem.RealRoaming, new KeyAssignmentStore(AssignmentsDir));
    private static KeyHealthStore Health() => new(Path.Combine(Ecosystem.RealLocal, KeyAssignmentStore.Bucket));

    private static readonly string[] KeyBuckets = { LlmCredentialStore.Bucket, BrokerCredentialStore.Bucket };

    [Test]
    public void Key_files_parse()
    {
        var problems = new List<string>();
        foreach (var bucket in KeyBuckets)
        {
            var file = Path.Combine(Ecosystem.RealRoaming, bucket, CredentialStore.ProvidersJsonFile);
            if (!File.Exists(file)) { Ecosystem.Report($"{bucket}\\{CredentialStore.ProvidersJsonFile} doesn't exist"); continue; }
            try
            {
                if (JsonNode.Parse(ReadShared(file)) is not JsonObject root) problems.Add($"{bucket}\\{CredentialStore.ProvidersJsonFile}: root isn't an object");
                else
                    foreach (var (id, _) in root)
                        if (!ProviderCatalog.IsKnown(id)) Ecosystem.Report($"{bucket}\\{CredentialStore.ProvidersJsonFile}: provider '{id}' isn't in ProviderCatalog (kept as its own list)");
            }
            catch (JsonException ex) { problems.Add($"{bucket}\\{CredentialStore.ProvidersJsonFile}: invalid JSON at line {ex.LineNumber}"); }
        }
        if (File.Exists(AssignmentsFile))
        {
            try
            {
                if (JsonNode.Parse(ReadShared(AssignmentsFile)) is not JsonObject) problems.Add($"{KeyAssignmentStore.FileName}: root isn't an object");
            }
            catch (JsonException ex) { problems.Add($"{KeyAssignmentStore.FileName}: invalid JSON at line {ex.LineNumber}"); }
        }
        else Ecosystem.Report($"{KeyAssignmentStore.FileName} doesn't exist (every app uses the shared order)");
        Ecosystem.AssertNone(problems, "Key files");
    }

    [Test]
    public void Every_provider_with_keys_names_them_uniquely()
    {
        var store = Keys();
        var problems = new List<string>();
        foreach (var provider in store.ProvidersWithKeys())
        {
            var keys = store.GetKeys(provider);
            foreach (var dup in keys.GroupBy(k => k.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                problems.Add($"{provider}: key name '{dup.Key}' used {dup.Count()} times");
            foreach (var k in keys.Where(k => string.IsNullOrWhiteSpace(k.Value)))
                problems.Add($"{provider}: key '{k.Name}' has an empty value");
            TestContext.Out.WriteLine($"{provider}: {keys.Count} key(s): {string.Join(", ", keys.Select(k => k.ToString()))}");
        }
        Ecosystem.AssertNone(problems, "Named keys");
    }

    [Test]
    public void Every_assignment_names_an_existing_key_of_a_known_provider_for_a_known_app()
    {
        var store = Keys();
        var apps = Ecosystem.Apps.Select(a => a.AppId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        var all = store.Assignments.All();
        foreach (var (app, byProvider) in all)
        {
            if (!apps.Contains(app)) problems.Add($"{app}: has key assignments but no manifest (orphan app)");
            foreach (var (provider, names) in byProvider)
            {
                if (!ProviderCatalog.IsKnown(provider)) problems.Add($"{app}/{provider}: provider isn't in ProviderCatalog");
                var keys = store.GetKeys(provider);
                foreach (var name in names.Where(n => !keys.Any(k => string.Equals(k.Name, n, StringComparison.OrdinalIgnoreCase))))
                    problems.Add($"{app}/{provider}: assigned key '{name}' doesn't exist");
                var declared = apps.Contains(app) && Ecosystem.App(app).Manifest.Credentials.Any(c => ProviderCatalog.Normalize(c.ProviderId) == provider);
                if (apps.Contains(app) && !declared) Ecosystem.Report($"{app}/{provider}: assigned but the manifest doesn't declare [UsesCredential(\"{provider}\")]");
            }
        }
        TestContext.Out.WriteLine($"{all.Count} app(s) with assignments");
        Ecosystem.AssertNone(problems, "Key assignments");
    }

    [TestCaseSource(typeof(Ecosystem), nameof(Ecosystem.AppsWithCredentials))]
    public void Each_required_credential_has_a_usable_key(string appId)
    {
        var app = Ecosystem.App(appId);
        var selector = new KeySelector(new FileKeySource(Keys()), Health());
        var problems = new List<string>();
        foreach (var use in app.Manifest.Credentials)
        {
            KeyPlan plan;
            try { plan = selector.Plan(use.ProviderId, app.AppId); }
            catch (Exception ex) { problems.Add($"{use.ProviderId}: planning threw {ex.GetType().Name}"); continue; }

            var line = $"{use.ProviderId}{(use.Required ? " (required)" : "")}: usable [{string.Join(", ", plan.Usable.Select(k => k.Name))}]"
                       + (plan.Skipped.Count > 0 ? $", skipped [{string.Join(", ", plan.Skipped.Select(s => $"{s.KeyName}={s.Outcome}"))}]" : "")
                       + (plan.MissingAssigned.Count > 0 ? $", assigned but missing [{string.Join(", ", plan.MissingAssigned)}]" : "");
            TestContext.Out.WriteLine(line);
            if (plan.MissingAssigned.Count > 0) problems.Add($"{use.ProviderId}: assigned key(s) {string.Join(", ", plan.MissingAssigned)} don't exist");
            if (plan.Usable.Count == 0)
            {
                if (use.Required) problems.Add($"{use.ProviderId}: required, but no usable key ({line})");
                else Ecosystem.Report($"{appId}/{use.ProviderId}: optional and has no usable key");
            }
        }
        Ecosystem.AssertNone(problems, $"{appId} keys");
    }

    [Test]
    public void Vault_left_no_backup_copy_of_a_key_file()
    {
        // Key files live in the LLM and Brokers buckets, the assignments in Vault, health in local Vault. Since M.VALT-T61
        // no Vault store writes a .bak; older ones predate that and are reported by name only.
        var dirs = KeyBuckets.Select(b => Path.Combine(Ecosystem.RealRoaming, b))
            .Append(AssignmentsDir)
            .Append(Path.Combine(Ecosystem.RealLocal, KeyAssignmentStore.Bucket))
            .Where(Directory.Exists);
        var problems = new List<string>();
        foreach (var dir in dirs)
            foreach (var file in Directory.EnumerateFiles(dir, "*.bak*"))
            {
                var written = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                var name = Path.GetRelativePath(Path.GetDirectoryName(dir)!, file);
                if (written >= Ecosystem.NoKeyBackupsSince) problems.Add($"{name}: written {written:u}, after Vault stopped keeping backups");
                else Ecosystem.Report($"{name}: pre-existing backup from {written:yyyy-MM-dd} (before M.VALT-T61); holds old key values, remove when no longer needed");
            }
        Ecosystem.AssertNone(problems, "Key file backups");
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
