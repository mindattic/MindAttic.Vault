using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Desktop.Services;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Tests;

[TestFixture]
public sealed class AuditLogTests
{
    private VaultSandbox sandbox = null!;
    private SqliteAuditLog audit = null!;

    [SetUp]
    public void SetUp()
    {
        sandbox = new VaultSandbox();
        audit = new SqliteAuditLog();
    }

    [TearDown]
    public void TearDown() => sandbox.Dispose();

    [Test]
    public void It_lives_in_the_local_Vault_folder()
    {
        Assert.That(audit.FilePath, Is.EqualTo(Path.Combine(sandbox.Local, "Vault", "audit.db")));
    }

    [Test]
    public void Entries_round_trip_in_order()
    {
        var t = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        audit.Record(new AuditEntry(t, "PC-me", "Desktop", "Alpha", AuditActions.SettingsSave, new[] { Samples.AlphaName }));
        audit.Record(new AuditEntry(t.AddMinutes(1), "PC-me", "Desktop", "claude", AuditActions.KeyValue, new[] { "key1" }, "value changed"));

        var all = audit.ReadAll();

        Assert.That(all.Select(e => e.Action), Is.EqualTo(new[] { AuditActions.SettingsSave, AuditActions.KeyValue }));
        Assert.That(all[0].TimeUtc, Is.EqualTo(t));
        Assert.That(all[0].Names, Is.EqualTo(new[] { Samples.AlphaName }));
        Assert.That(all[1].Detail, Is.EqualTo("value changed"));
    }

    [Test]
    public void Triggers_reject_UPDATE_and_DELETE()
    {
        audit.Record(new AuditEntry(DateTimeOffset.UtcNow, "PC-me", "Desktop", "Alpha", AuditActions.SettingsSave, new[] { "x" }));
        using var connection = audit.Open();

        foreach (var sql in new[] { "UPDATE audit SET actor = 'someone else'", "DELETE FROM audit" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var ex = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery(), sql);
            Assert.That(ex!.Message, Does.Contain("append-only"));
        }
        Assert.That(audit.ReadAll().Single().Actor, Is.EqualTo("PC-me"));
    }

    [Test]
    public void A_write_that_fails_is_swallowed()
    {
        var blocked = Path.Combine(sandbox.Local, "blocked");
        Directory.CreateDirectory(Path.Combine(blocked, "audit.db")); // a folder where the file should be
        var broken = new SqliteAuditLog(Path.Combine(blocked, "audit.db"));

        Assert.DoesNotThrow(() => broken.Record(new AuditEntry(DateTimeOffset.UtcNow, "a", "b", "c", "d", new[] { "e" })));
    }

    [Test]
    public async Task Each_Save_appends_one_row_with_field_names_and_no_values()
    {
        Samples.Install<AlphaSettings>(sandbox.Roaming);
        var profile = new DesktopProfile();
        var vm = Samples.Shell(new FileManifestCatalog(), Samples.Context(localRoot: sandbox.Local, audit: audit), new FakeKeyTester(), profile);
        await vm.InitializeAsync();
        var alpha = vm.AppTabs.Single(t => t.AppId == "Alpha");
        alpha.Document!["name"] = "VALUE-THAT-MUST-NOT-BE-AUDITED";
        alpha.MarkChanged(Samples.AlphaName);
        alpha.Document!["count"] = 6;
        alpha.MarkChanged(Samples.AlphaCount);

        await alpha.SaveAsync();
        await alpha.SaveAsync(); // nothing changed: no second row

        var row = audit.ReadAll().Single();
        Assert.That(row.Action, Is.EqualTo(AuditActions.SettingsSave));
        Assert.That(row.Target, Is.EqualTo("Alpha"));
        Assert.That(row.Profile, Is.EqualTo("Desktop"));
        Assert.That(row.Actor, Is.EqualTo(profile.Actor));
        Assert.That(row.Names, Is.EquivalentTo(new[] { Samples.AlphaName, Samples.AlphaCount }));
        Assert.That(Encoding.UTF8.GetString(File.ReadAllBytes(audit.FilePath)), Does.Not.Contain("VALUE-THAT-MUST-NOT-BE-AUDITED"));
    }

    [Test]
    public async Task A_key_assignment_is_audited_by_name()
    {
        var profile = new FakeProfile("Desktop");
        profile.KeyStore.Keys["claude"] = new() { new NamedKey("key1", "sk-not-in-audit") };
        var vm = Samples.Shell(new FakeCatalog { Manifests = { Samples.Alpha } }, Samples.Context(localRoot: sandbox.Local, audit: audit), profile);
        await vm.InitializeAsync();

        await vm.AppTabs.Single(t => t.AppId == "Alpha").Credentials.Single().SetActiveAsync("key1");

        var row = audit.ReadAll().Single();
        Assert.That((row.Action, row.Target), Is.EqualTo((AuditActions.KeysAssign, "Alpha/claude")));
        Assert.That(row.Names, Is.EqualTo(new[] { "key1" }));
        Assert.That(Encoding.UTF8.GetString(File.ReadAllBytes(audit.FilePath)), Does.Not.Contain("sk-not-in-audit"));
    }
}
