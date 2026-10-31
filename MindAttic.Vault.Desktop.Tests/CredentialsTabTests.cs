using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Desktop.ViewModels;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Tests;

/// <summary>
/// The Credentials tab (M.VALT-T62): every known credential group round-trips through the section with exactly the edits
/// made (other fields and JSON types untouched), entries and fields can be added and removed, secrets never reach a
/// bindable property, a message or the audit log, and every Save writes one audit row.
/// </summary>
[TestFixture]
public sealed class CredentialsTabTests
{
    private VaultSandbox sandbox = null!;
    private FakeDialogs dialogs = null!;
    private SqliteAuditLog audit = null!;
    private MainViewModel main = null!;

    [SetUp]
    public async Task SetUp()
    {
        sandbox = new VaultSandbox();
        dialogs = new FakeDialogs();
        audit = new SqliteAuditLog();
        var context = Samples.Context(dialogs, localRoot: sandbox.Local, audit: audit);
        main = Samples.Shell(new FakeCatalog(), context, new DesktopProfile());
        await main.InitializeAsync();
    }

    [TearDown]
    public void TearDown() => sandbox.Dispose();

    private string PathOf(string bucket, string file) => Path.Combine(sandbox.Roaming, bucket, file);

    private void Write(string bucket, string file, string json)
    {
        Directory.CreateDirectory(Path.Combine(sandbox.Roaming, bucket));
        File.WriteAllText(PathOf(bucket, file), json);
    }

    private JsonObject Read(string bucket, string file) => (JsonObject)JsonNode.Parse(File.ReadAllText(PathOf(bucket, file)))!;

    /// <summary>One known group's real shape, the plain field and the secret field a test edits.</summary>
    public sealed record Shape(string Bucket, string File, string Json, string? PlainEntry, string? PlainField, string PlainText, JsonNode? PlainStored,
        string? SecretEntry, string SecretField)
    {
        public override string ToString() => Bucket;
    }

    public static IEnumerable<Shape> Shapes()
    {
        yield return new Shape("Notifications", "providers.json", """
            {
              "twilio": { "accountSid": "AC123", "authToken": "tw-secret", "from": "+15550001", "brandId": "BN1" },
              "email": { "smtpHost": "smtp.old.test", "smtpPort": "587", "username": "me", "password": "mail-secret", "from": "me@test" },
              "to": "+15550002",
              "toEmail": "5550002@vtext.test"
            }
            """, "email", "smtpHost", "smtp.new.test", JsonValue.Create("smtp.new.test"), "twilio", "authToken");
        yield return new Shape("Subtitles", "providers.json", """
            { "OpenSubtitles": { "user": "olduser", "password": "subs-secret", "retries": 3, "vip": false } }
            """, "OpenSubtitles", "user", "newuser", JsonValue.Create("newuser"), "OpenSubtitles", "password");
        yield return new Shape("AudioStore", "providers.json", """
            { "provider": "azure", "container": "audio-old", "connectionString": "DefaultEndpointsProtocol=https;AccountKey=abc", "timeoutSeconds": 30 }
            """, null, "container", "audio-new", JsonValue.Create("audio-new"), null, "connectionString");
        yield return new Shape("Brokers", "providers.json", """
            {
              "alpaca-live": { "type": "alpaca", "apiKey": "AK1", "secret": "live-secret", "baseUrl": "https://api.alpaca.markets" },
              "alpaca-paper": { "type": "alpaca", "apiKey": "PK1", "secret": "paper-secret", "baseUrl": "https://paper-api.alpaca.markets",
                                "apiKeys": [ { "key": "PK1", "label": "key1" }, { "key": "PK2", "label": "key2" } ] }
            }
            """, "alpaca-paper", "baseUrl", "https://paper.example.test", JsonValue.Create("https://paper.example.test"), "alpaca-live", "secret");
        yield return new Shape("Ftp", "ftp.json", """
            { "host": "ftp.test", "port": 21, "user": "deploy", "password": "ftp-secret", "secure": true, "servername": "prod.test", "_rejectUnauthorized": false }
            """, null, "port", "2121", JsonValue.Create(2121), null, "password");
        yield return new Shape("Tokens", "tokens.json", """
            { "github": "ghp_old", "nuget-org": "oy2old" }
            """, null, null, "", null, null, "github");
        yield return new Shape("Hosting", "providers.json", """
            {
              "genericvps:prod-box": { "type": "ssh", "apiKey": "", "secret": "root@203.0.113.10:22", "sshKeyPath": "C:\\old\\id_ed25519" },
              "ghcr": { "type": "ghcr", "apiKey": "ghcr-secret", "secret": "me" },
              "local:cursory-local": { "packageDirectory": "C:\\pkg", "port": 7777, "upnpMapped": true }
            }
            """, "genericvps:prod-box", "sshKeyPath", "C:\\new\\id_ed25519", JsonValue.Create("C:\\new\\id_ed25519"), "ghcr", "apiKey");
    }

    private static CredentialEntryViewModel EntryOf(CredentialGroupViewModel group, string? name) =>
        name is null ? group.Root! : group.Entry(name)!;

    [TestCaseSource(nameof(Shapes))]
    public async Task Each_group_round_trips_with_exactly_the_edited_fields_changed(Shape shape)
    {
        Write(shape.Bucket, shape.File, shape.Json);
        var expected = (JsonObject)JsonNode.Parse(shape.Json)!;
        await main.Credentials.ReloadAsync();
        var group = main.Credentials.Group(shape.Bucket, shape.File)!;
        Assert.That(group, Is.Not.Null);

        if (shape.PlainField is { } plainName)
        {
            var plain = EntryOf(group, shape.PlainEntry).Field(plainName)!;
            Assert.That(plain.IsSecret, Is.False, plainName + " is a plain field");
            plain.Text = shape.PlainText;
            var target = shape.PlainEntry is null ? expected : (JsonObject)expected[shape.PlainEntry]!;
            target[plainName] = shape.PlainStored?.DeepClone();
        }

        var secret = EntryOf(group, shape.SecretEntry).Field(shape.SecretField)!;
        Assert.That(secret.IsSecret, Is.True, shape.SecretField + " is masked");
        Assert.That(secret.Text, Is.Empty, "a secret field has no bindable value");
        Assert.That(secret.ReadSecret(), Is.EqualTo((shape.SecretEntry is null ? expected : (JsonObject)expected[shape.SecretEntry]!)[shape.SecretField]!.GetValue<string>()));
        secret.EditSecret("new-" + shape.Bucket + "-secret");
        (shape.SecretEntry is null ? expected : (JsonObject)expected[shape.SecretEntry]!)[shape.SecretField] = "new-" + shape.Bucket + "-secret";

        Assert.That(group.IsDirty, Is.True);
        Assert.That(await group.SaveAsync(), Is.True, group.StatusText);

        var actual = Read(shape.Bucket, shape.File);
        Assert.That(JsonNode.DeepEquals(actual, expected), Is.True, $"expected {expected.ToJsonString()}\nactual   {actual.ToJsonString()}");
        Assert.That(group.IsDirty, Is.False);
        Assert.That(Directory.EnumerateFiles(Path.Combine(sandbox.Roaming, shape.Bucket)).Select(Path.GetFileName), Is.EqualTo(new[] { shape.File }),
            "no backup or temp file is left beside it");
    }

    [Test]
    public async Task A_number_stays_a_number_and_text_that_isnt_one_is_refused()
    {
        Write("Ftp", "ftp.json", """{ "host": "ftp.test", "port": 21, "password": "p" }""");
        await main.Credentials.ReloadAsync();
        var port = main.Credentials.Group("Ftp")!.Root!.Field("port")!;
        Assert.That(port.KindLabel, Is.EqualTo("number"));

        port.Text = "twenty-one";
        Assert.That(port.Error, Does.Contain("MindAttic:Vault:Ftp:port"));
        Assert.That(await main.Credentials.Group("Ftp")!.SaveAsync(), Is.False);
        Assert.That(Read("Ftp", "ftp.json")["port"]!.GetValue<int>(), Is.EqualTo(21));
    }

    [Test]
    public async Task Tokens_add_and_remove_through_the_token_store()
    {
        Write("Tokens", "tokens.json", """{ "github": "ghp_keep", "old-token": "drop-me" }""");
        await main.Credentials.ReloadAsync();
        var root = main.Credentials.Group("Tokens")!.Root!;

        Assert.That(root.AddField("nuget-org"), Is.True);
        var added = root.Field("nuget-org")!;
        Assert.That(added.IsSecret, Is.True, "every token is a secret");
        added.EditSecret("oy2-new-token");
        root.Field("old-token")!.RemoveCommand.Execute(null);
        Assert.That(await main.Credentials.Group("Tokens")!.SaveAsync(), Is.True);

        var store = new TokenStore(Path.Combine(sandbox.Roaming, "Tokens"));
        Assert.That(store.LoadAll().OrderBy(p => p.Key).Select(p => (p.Key, p.Value)),
            Is.EqualTo(new[] { ("github", "ghp_keep"), ("nuget-org", "oy2-new-token") }));
    }

    [Test]
    public async Task Entries_and_fields_can_be_added_and_removed()
    {
        Write("Brokers", "providers.json", """
            { "alpaca-live": { "type": "alpaca", "apiKey": "AK1", "secret": "s1", "baseUrl": "https://api" },
              "alpaca-paper": { "type": "alpaca", "apiKey": "PK1", "secret": "s2", "baseUrl": "https://paper", "note": "drop" } }
            """);
        await main.Credentials.ReloadAsync();
        var group = main.Credentials.Group("Brokers")!;

        Assert.That(group.AddEntry("alpaca-test"), Is.True);
        Assert.That(group.AddEntry("ALPACA-TEST"), Is.False, "names are unique, ignoring case");
        var entry = group.Entry("alpaca-test")!;
        entry.AddField("apiKey");
        entry.Field("apiKey")!.EditSecret("TK-new");
        entry.AddField("baseUrl");
        Assert.That(entry.Field("baseUrl")!.IsSecret, Is.False);
        entry.Field("baseUrl")!.Text = "https://test";
        group.Entry("alpaca-live")!.RemoveCommand.Execute(null);
        group.Entry("alpaca-paper")!.Field("note")!.RemoveCommand.Execute(null);

        Assert.That(await group.SaveAsync(), Is.True, group.StatusText);

        var file = Read("Brokers", "providers.json");
        Assert.That(file.Select(p => p.Key), Is.EquivalentTo(new[] { "alpaca-paper", "alpaca-test" }));
        Assert.That(JsonNode.DeepEquals(file["alpaca-test"], JsonNode.Parse("""{ "apiKey": "TK-new", "baseUrl": "https://test" }""")), Is.True);
        Assert.That(JsonNode.DeepEquals(file["alpaca-paper"],
            JsonNode.Parse("""{ "type": "alpaca", "apiKey": "PK1", "secret": "s2", "baseUrl": "https://paper" }""")), Is.True);
        // The broker store still reads what was written.
        Assert.That(new BrokerCredentialStore(Path.Combine(sandbox.Roaming, "Brokers")).GetBrokerCreds("alpaca-paper")!.Secret, Is.EqualTo("s2"));
    }

    [Test]
    public async Task Every_save_writes_one_audit_row_with_names_only()
    {
        Write("Notifications", "providers.json", """
            { "email": { "smtpHost": "smtp.test", "password": "mail-secret" }, "to": "+15550002" }
            """);
        await main.Credentials.ReloadAsync();
        var group = main.Credentials.Group("Notifications")!;
        group.Entry("email")!.Field("password")!.EditSecret("audit-canary-secret");
        group.Root!.Field("to")!.Text = "+15550003";
        Assert.That(await group.SaveAsync(), Is.True);
        group.Entry("email")!.Field("smtpHost")!.Text = "smtp2.test";
        Assert.That(await group.SaveAsync(), Is.True);
        Assert.That(await group.SaveAsync(), Is.False, "nothing to save: no row");

        var rows = audit.ReadAll().Where(r => r.Action == AuditActions.CredentialsSave).ToList();
        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows[0].Target, Is.EqualTo("credentials/Notifications/providers.json"));
        Assert.That(rows[0].Profile, Is.EqualTo(DesktopProfile.ProfileId));
        Assert.That(rows[0].Names, Is.EquivalentTo(new[] { "MindAttic:Vault:Notifications:email:password", "MindAttic:Vault:Notifications:to" }));
        Assert.That(rows[0].Detail, Is.EqualTo("secrets changed: MindAttic:Vault:Notifications:email:password"));
        Assert.That(rows[1].Names, Is.EqualTo(new[] { "MindAttic:Vault:Notifications:email:smtpHost" }));
        Assert.That(File.ReadAllBytes(audit.FilePath).AsSpan().IndexOf("audit-canary-secret"u8), Is.EqualTo(-1));
        Assert.That(Read("Notifications", "providers.json")["to"]!.GetValue<string>(), Is.EqualTo("+15550003"), "top-level fields survive");
    }

    [Test]
    public async Task A_file_changed_since_load_is_not_overwritten()
    {
        Write("Subtitles", "providers.json", """{ "OpenSubtitles": { "user": "a", "password": "p" } }""");
        await main.Credentials.ReloadAsync();
        var group = main.Credentials.Group("Subtitles")!;
        group.Entry("OpenSubtitles")!.Field("user")!.Text = "mine";
        Write("Subtitles", "providers.json", """{ "OpenSubtitles": { "user": "theirs", "password": "p" } }""");

        Assert.That(await group.SaveAsync(), Is.False);
        Assert.That(dialogs.Errors.Single(), Does.Contain("changed on disk"));
        Assert.That(Read("Subtitles", "providers.json")["OpenSubtitles"]!["user"]!.GetValue<string>(), Is.EqualTo("theirs"));

        await group.RevertAsync();
        Assert.That(group.Entry("OpenSubtitles")!.Field("user")!.Text, Is.EqualTo("theirs"));
        Assert.That(group.IsDirty, Is.False);
    }

    [Test]
    public async Task Known_groups_show_without_files_and_other_buckets_on_disk_appear()
    {
        Write("GitHub", "tokens.json", """{ "github": "ghp_x" }""");
        Write("LLM", "providers.json", """{ "claude": { "apiKey": "sk" } }""");
        await main.Credentials.ReloadAsync();

        var ids = main.Credentials.Groups.Select(g => g.Info.Id).ToList();
        Assert.That(ids.Take(7), Is.EqualTo(new[]
        {
            "Notifications/providers.json", "Subtitles/providers.json", "AudioStore/providers.json", "Brokers/providers.json",
            "Ftp/ftp.json", "Tokens/tokens.json", "Hosting/providers.json",
        }));
        Assert.That(ids, Does.Contain("GitHub/tokens.json"));
        Assert.That(ids.Any(i => i.StartsWith("LLM/", StringComparison.Ordinal)), Is.False, "LLM keys are the Keys tab's");
        Assert.That(main.Credentials.Group("GitHub")!.Root!.Field("github")!.IsSecret, Is.True);

        // A known group with no file starts from its template and creates the file on Save.
        var ftp = main.Credentials.Group("Ftp")!;
        Assert.That(ftp.Root!.Fields.Select(f => f.Name), Is.EqualTo(new[] { "host", "port", "user", "password", "secure" }));
        ftp.Root.Field("host")!.Text = "ftp.new.test";
        ftp.Root.Field("user")!.Text = "deploy";
        Assert.That(await ftp.SaveAsync(), Is.True);
        Assert.That(new FtpCredentialStore(Path.Combine(sandbox.Roaming, "Ftp")).Get()!.Host, Is.EqualTo("ftp.new.test"));
        Assert.That(Read("Ftp", "ftp.json")["port"]!.GetValue<int>(), Is.EqualTo(21));
    }

    [Test]
    public void Credentials_comes_right_after_Keys()
    {
        Assert.That(main.Tabs.Select(t => t.Id).Where(id => id != "getstarted").Take(3), Is.EqualTo(new[] { "keys", "credentials", "app:Global" }));
    }

    [Test]
    public async Task A_profile_without_credential_groups_shows_the_tab_read_only_with_a_note()
    {
        var azure = new FakeProfile("Azure", "Azure (test)");
        Assert.That(await main.SwitchProfileAsync(azure), Is.True);

        Assert.That(main.Credentials.Groups, Is.Empty);
        Assert.That(main.Credentials.Notice, Does.Contain("can't be edited in Azure (test)").And.Contain("M.VALT-T63"));
    }

    [Test]
    public async Task Switching_profile_with_unsaved_credentials_asks_first()
    {
        Write("Subtitles", "providers.json", """{ "OpenSubtitles": { "user": "a", "password": "p" } }""");
        await main.Credentials.ReloadAsync();
        main.Credentials.Group("Subtitles")!.Entry("OpenSubtitles")!.Field("user")!.Text = "edited";
        Assert.That(main.Credentials.Trailing, Is.EqualTo("unsaved"));
        dialogs.Answers.Enqueue(false);

        Assert.That(await main.SwitchProfileAsync(new FakeProfile("Other")), Is.False);
        Assert.That(dialogs.Confirms.Single(), Does.Contain("Credentials"));
    }

    [Test]
    public void Secret_looking_names()
    {
        foreach (var name in new[] { "password", "Secret", "apiKey", "authToken", "token", "connectionString", "appKey", "AccessKeyId", "clientSecret" })
            Assert.That(CredentialGroupCatalog.LooksSecret(name), Is.True, name);
        foreach (var name in new[] { "user", "smtpHost", "smtpPort", "from", "accountSid", "brandId", "baseUrl", "sshKeyPath", "type", "container" })
            Assert.That(CredentialGroupCatalog.LooksSecret(name), Is.False, name);
    }

    [Test]
    public async Task No_secret_reaches_a_bindable_property()
    {
        foreach (var shape in Shapes()) Write(shape.Bucket, shape.File, shape.Json);
        await main.Credentials.ReloadAsync();
        var secrets = main.Credentials.Groups.SelectMany(g => g.Entries).SelectMany(e => e.Fields).Where(f => f.IsSecret).Select(f => f.ReadSecret())
            .Where(s => s.Length > 2).ToList();
        Assert.That(secrets, Has.Count.GreaterThan(8));
        // An edited secret, too.
        main.Credentials.Group("Brokers")!.Entry("alpaca-live")!.Field("secret")!.EditSecret("edited-CANARY-value");
        secrets.Add("edited-CANARY-value");

        var texts = BindableText.Of(main.Credentials).ToList();
        Assert.That(texts, Is.Not.Empty);
        foreach (var secret in secrets)
            Assert.That(texts.Where(t => t.Contains(secret, StringComparison.Ordinal)), Is.Empty, "a secret is in a bindable property");
        // The broker's apiKeys list is shown by size only.
        Assert.That(main.Credentials.Group("Brokers")!.Entry("alpaca-paper")!.Field("apiKeys")!.KindLabel, Is.EqualTo("list of 2, kept as is"));
    }
}

/// <summary>Every string a binding could read from a view-model tree: public properties, recursively through collections.</summary>
public static class BindableText
{
    public static IEnumerable<string> Of(object root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<object>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var item = pending.Pop();
            if (!seen.Add(item)) continue;
            if (item.ToString() is { } s) yield return s;
            foreach (var property in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0) continue;
                object? value;
                try { value = property.GetValue(item); }
                catch (TargetInvocationException) { continue; }
                switch (value)
                {
                    case null: break;
                    case string text: yield return text; break;
                    case IEnumerable list when value is not string:
                        foreach (var child in list) if (child is not null) pending.Push(child);
                        break;
                    default:
                        if (value.GetType().Namespace?.StartsWith("MindAttic.Vault.Desktop.ViewModels", StringComparison.Ordinal) == true) pending.Push(value);
                        else if (value.ToString() is { } t) yield return t;
                        break;
                }
            }
        }
    }
}
