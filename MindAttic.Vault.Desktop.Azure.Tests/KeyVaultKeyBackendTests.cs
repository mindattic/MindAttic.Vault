using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Azure.Keys;
using MindAttic.Vault.Desktop.Azure.Tests.Fakes;
using MindAttic.Vault.Desktop.Profiles;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Azure.Tests;

[TestFixture]
public sealed class KeyVaultKeyBackendTests
{
    private const string KeyRef = "application/vnd.microsoft.appconfig.keyvaultref+json;charset=utf-8";
    private const string Canary = "sk-canary-0123456789-DO-NOT-LEAK";

    private static KeyVaultKeyBackend Keys(FakeAzure fake) =>
        new(fake.Clients.CreateConfigurationClient(FakeAzure.StoreEndpoint), fake.Clients.CreateSecretClient(FakeAzure.VaultUri), "teststore", "testvault");

    private static string KeyName(string provider, string name) => $"MindAttic:Vault:LLM:{provider}:keys:{name}";

    private static string SecretOf(FakeAzure fake, string provider, string name) =>
        KeyVaultReference.TryParse(global::Azure.Data.AppConfiguration.ConfigurationModelFactory.ConfigurationSetting(
            KeyName(provider, name), fake.Settings[KeyName(provider, name)].Value, null, KeyRef, default, null, null))!.SecretName;

    [Test]
    public async Task Add_StoresSecretInKeyVault_AndReferenceInAppConfiguration()
    {
        var fake = new FakeAzure();
        var keys = Keys(fake);

        await keys.AddAsync("anthropic", "2026-10-31 IdiotProof", "sk-value-1");

        var kv = fake.Settings[KeyName("claude", "2026-10-31 IdiotProof")];
        var secretName = SecretNames.Generate("claude", "2026-10-31 IdiotProof");
        Assert.Multiple(() =>
        {
            Assert.That(kv.ContentType, Is.EqualTo(KeyRef));
            Assert.That(kv.Value, Is.EqualTo($"{{\"uri\":\"https://testvault.vault.azure.net/secrets/{secretName}\"}}"));
            Assert.That(fake.Secrets[secretName].Single().Value, Is.EqualTo("sk-value-1"));
            Assert.That(fake.Secrets[secretName].Single().Tags, Is.EquivalentTo(new Dictionary<string, string>
            {
                ["name"] = "2026-10-31 IdiotProof",
                ["provider"] = "claude",
            }));
            Assert.That(fake.Settings["MindAttic:Vault:LLM:claude:keyOrder:0"].Value, Is.EqualTo("2026-10-31 IdiotProof"));
            Assert.That(fake.Settings.Values.Any(s => s.Value?.Contains("sk-value-1") == true), Is.False, "no value in App Configuration");
        });

        var listed = await keys.GetKeysAsync("claude");
        Assert.That(listed, Is.EqualTo(new[] { new NamedKey("2026-10-31 IdiotProof", "sk-value-1") }));
    }

    [Test]
    public async Task Add_AppendsToTheOrder_AndRejectsDuplicateOrInvalidNames()
    {
        var fake = new FakeAzure();
        var keys = Keys(fake);
        await keys.AddAsync("openai", "key1", "v1");
        await keys.AddAsync("openai", "key2", "v2");

        Assert.That((await keys.GetKeysAsync("openai")).Select(k => k.Name), Is.EqualTo(new[] { "key1", "key2" }));
        Assert.ThrowsAsync<ArgumentException>(() => keys.AddAsync("openai", "KEY1", "v3"));
        Assert.ThrowsAsync<ArgumentException>(() => keys.AddAsync("openai", "a:b", "v3"));
        Assert.ThrowsAsync<ArgumentException>(() => keys.AddAsync("openai", "   ", "v3"));
        Assert.ThrowsAsync<ArgumentException>(() => keys.AddAsync("openai", "fine", " "));
        Assert.That(fake.Secrets.Count, Is.EqualTo(2), "a refused name creates no secret");
    }

    [Test]
    public async Task SetValue_WritesANewSecretVersion_AndLeavesAppConfigurationAlone()
    {
        var fake = new FakeAzure();
        var keys = Keys(fake);
        await keys.AddAsync("claude", "main", "old-value");
        var secretName = SecretOf(fake, "claude", "main");
        var refETag = fake.Settings[KeyName("claude", "main")].ETag;
        var writes = fake.AppConfigurationWrites.Count();

        await keys.SetValueAsync("claude", "MAIN", "new-value");

        Assert.Multiple(() =>
        {
            Assert.That(fake.Secrets[secretName].Select(v => v.Value), Is.EqualTo(new[] { "old-value", "new-value" }));
            Assert.That(fake.Secrets[secretName][^1].Tags["name"], Is.EqualTo("main"));
            Assert.That(fake.Settings[KeyName("claude", "main")].ETag, Is.EqualTo(refETag));
            Assert.That(fake.AppConfigurationWrites.Count(), Is.EqualTo(writes));
        });
        Assert.That((await keys.GetKeysAsync("claude")).Single().Value, Is.EqualTo("new-value"));
    }

    [Test]
    public async Task SetValue_OnAPlainValue_MovesItBehindAKeyVaultReference()
    {
        var fake = new FakeAzure();
        fake.Seed(KeyName("claude", "typed in portal"), "plain-value");
        var keys = Keys(fake);
        Assert.That((await keys.GetKeysAsync("claude")).Single().Value, Is.EqualTo("plain-value"));

        await keys.SetValueAsync("claude", "typed in portal", "secret-value");

        Assert.That(fake.Settings[KeyName("claude", "typed in portal")].ContentType, Is.EqualTo(KeyRef));
        Assert.That((await keys.GetKeysAsync("claude")).Single().Value, Is.EqualTo("secret-value"));
    }

    [Test]
    public async Task Rename_KeepsTheSecret_RetagsIt_AndRewritesOrderDisabledAndAssignments()
    {
        var fake = new FakeAzure();
        var keys = Keys(fake);
        await keys.AddAsync("claude", "key1", "v1");
        await keys.AddAsync("claude", "old name", "v2");
        await keys.AddAsync("openai", "old name", "v3");
        await keys.SetDisabledAsync("claude", "old name", true);
        await keys.SetAssignmentAsync("IdiotProof", "claude", new[] { "old name", "key1" });
        await keys.SetAssignmentAsync("Tutor", "anthropic", new[] { "old name" });
        await keys.SetAssignmentAsync("Prose", "openai", new[] { "old name" });
        var secretName = SecretOf(fake, "claude", "old name");
        var secretCount = fake.Secrets.Count;

        await keys.RenameAsync("claude", "OLD NAME", "2026-10-31 IdiotProof");

        Assert.Multiple(() =>
        {
            Assert.That(fake.Settings.ContainsKey(KeyName("claude", "old name")), Is.False);
            Assert.That(SecretOf(fake, "claude", "2026-10-31 IdiotProof"), Is.EqualTo(secretName), "same secret");
            Assert.That(fake.Secrets.Count, Is.EqualTo(secretCount), "no new secret");
            Assert.That(fake.Secrets[secretName].Single().Value, Is.EqualTo("v2"));
            Assert.That(fake.Secrets[secretName].Single().Tags["name"], Is.EqualTo("2026-10-31 IdiotProof"));
            Assert.That(fake.Settings["MindAttic:Vault:LLM:claude:keyOrder:1"].Value, Is.EqualTo("2026-10-31 IdiotProof"));
            Assert.That(fake.Settings["MindAttic:Vault:LLM:claude:disabledKeys:0"].Value, Is.EqualTo("2026-10-31 IdiotProof"));
        });
        Assert.That(await keys.GetAssignmentAsync("IdiotProof", "claude"), Is.EqualTo(new[] { "2026-10-31 IdiotProof", "key1" }));
        Assert.That(await keys.GetAssignmentAsync("Tutor", "claude"), Is.EqualTo(new[] { "2026-10-31 IdiotProof" }));
        Assert.That(await keys.GetAssignmentAsync("Prose", "openai"), Is.EqualTo(new[] { "old name" }), "another provider's key of the same name is untouched");
        Assert.That(await keys.AppsUsingAsync("claude", "old name"), Is.Empty);
        Assert.That(await keys.AppsUsingAsync("claude", "2026-10-31 IdiotProof"), Is.EqualTo(new[] { "IdiotProof", "Tutor" }));
        var listed = await keys.GetKeysAsync("claude");
        Assert.That(listed, Is.EqualTo(new[] { new NamedKey("key1", "v1"), new NamedKey("2026-10-31 IdiotProof", "v2", Disabled: true) }));
    }

    [Test]
    public async Task Rename_CaseOnly_AndToAnExistingName()
    {
        var fake = new FakeAzure();
        var keys = Keys(fake);
        await keys.AddAsync("claude", "key1", "v1");
        await keys.AddAsync("claude", "key2", "v2");

        Assert.ThrowsAsync<ArgumentException>(() => keys.RenameAsync("claude", "key1", "KEY2"));
        await keys.RenameAsync("claude", "key1", "Key1");

        Assert.That((await keys.GetKeysAsync("claude")).Select(k => k.Name), Is.EqualTo(new[] { "Key1", "key2" }));
        Assert.That(fake.Settings.ContainsKey(KeyName("claude", "key1")), Is.False);
    }

    [Test]
    public async Task Remove_IsRefusedWhileAssigned_ThenDeletesAndSoftDeletesTheSecret()
    {
        var fake = new FakeAzure();
        var keys = Keys(fake);
        await keys.AddAsync("claude", "key1", "v1");
        await keys.AddAsync("claude", "doomed", "v2");
        await keys.AddAsync("claude", "key3", "v3");
        await keys.SetDisabledAsync("claude", "doomed", true);
        await keys.SetAssignmentAsync("IdiotProof", "claude", new[] { "doomed" });
        await keys.SetAssignmentAsync("Tutor", "claude", new[] { "key1", "doomed" });
        var secretName = SecretOf(fake, "claude", "doomed");

        var ex = Assert.ThrowsAsync<KeyInUseException>(() => keys.RemoveAsync("claude", "doomed"));
        Assert.That(ex!.Apps, Is.EqualTo(new[] { "IdiotProof", "Tutor" }));
        Assert.That(fake.Settings.ContainsKey(KeyName("claude", "doomed")), Is.True);
        Assert.That(fake.DeletedSecrets, Is.Empty);

        await keys.SetAssignmentAsync("IdiotProof", "claude", Array.Empty<string>());
        await keys.SetAssignmentAsync("Tutor", "claude", new[] { "key1" });
        await keys.RemoveAsync("claude", "doomed");

        Assert.Multiple(() =>
        {
            Assert.That(fake.Settings.ContainsKey(KeyName("claude", "doomed")), Is.False);
            Assert.That(fake.DeletedSecrets, Does.Contain(secretName));
            Assert.That(fake.Settings.ContainsKey("MindAttic:Vault:LLM:claude:keyOrder:2"), Is.False);
            Assert.That(fake.Settings["MindAttic:Vault:LLM:claude:keyOrder:1"].Value, Is.EqualTo("key3"));
            Assert.That(fake.Settings.Keys.Any(k => k.Contains("disabledKeys", StringComparison.Ordinal)), Is.False);
            Assert.That(fake.Settings.Keys.Any(k => k.StartsWith("MindAttic:Vault:Assignments:IdiotProof", StringComparison.Ordinal)), Is.False);
        });
        Assert.That((await keys.GetKeysAsync("claude")).Select(k => k.Name), Is.EqualTo(new[] { "key1", "key3" }));
    }

    [Test]
    public async Task ReAdd_AfterRemove_PicksAnotherSecretNameBecauseTheOldOneIsSoftDeleted()
    {
        var fake = new FakeAzure();
        var keys = Keys(fake);
        await keys.AddAsync("claude", "again", "v1");
        var first = SecretOf(fake, "claude", "again");
        await keys.RemoveAsync("claude", "again");

        await keys.AddAsync("claude", "again", "v2");

        var second = SecretOf(fake, "claude", "again");
        Assert.That(second, Is.Not.EqualTo(first));
        Assert.That(second, Is.EqualTo(SecretNames.Generate("claude", "again", attempt: 1)));
        Assert.That((await keys.GetKeysAsync("claude")).Single().Value, Is.EqualTo("v2"));
    }

    [Test]
    public async Task Add_NeverReusesASecretHeldByARenamedKeyOrAnOrphan()
    {
        var fake = new FakeAzure();
        var keys = Keys(fake);
        await keys.AddAsync("claude", "alpha", "v1");
        await keys.RenameAsync("claude", "alpha", "beta"); // beta still uses alpha's secret name
        fake.SeedSecret(SecretNames.Generate("claude", "gamma"), "orphan");

        await keys.AddAsync("claude", "alpha", "v2");
        await keys.AddAsync("claude", "gamma", "v3");

        Assert.That(SecretOf(fake, "claude", "alpha"), Is.Not.EqualTo(SecretOf(fake, "claude", "beta")));
        Assert.That(SecretOf(fake, "claude", "gamma"), Is.Not.EqualTo(SecretNames.Generate("claude", "gamma")));
        var values = (await keys.GetKeysAsync("claude")).ToDictionary(k => k.Name, k => k.Value);
        Assert.That(values, Is.EquivalentTo(new Dictionary<string, string> { ["beta"] = "v1", ["alpha"] = "v2", ["gamma"] = "v3" }));
        Assert.That(fake.Secrets[SecretNames.Generate("claude", "gamma")].Single().Value, Is.EqualTo("orphan"), "the orphan got no new version");
    }

    [Test]
    public async Task Disable_Reorder_Assignments_ProvidersWithKeys()
    {
        var fake = new FakeAzure();
        var keys = Keys(fake);
        await keys.AddAsync("claude", "a", "1");
        await keys.AddAsync("claude", "b", "2");
        await keys.AddAsync("claude", "c", "3");
        await keys.AddAsync("alpaca", "paper", "4");
        await keys.AddAsync("xai", "g", "5");

        await keys.ReorderAsync("claude", new[] { "C", "a", "b" });
        await keys.SetDisabledAsync("claude", "a", true);
        await keys.SetDisabledAsync("claude", "b", true);
        await keys.SetDisabledAsync("claude", "a", false);
        await keys.SetAssignmentAsync("Tutor", "claude", new[] { " b ", "c", "B", "" });

        Assert.That(await keys.GetKeysAsync("claude"), Is.EqualTo(new[]
        {
            new NamedKey("c", "3"), new NamedKey("a", "1"), new NamedKey("b", "2", Disabled: true),
        }));
        Assert.That(await keys.GetAssignmentAsync("Tutor", "claude"), Is.EqualTo(new[] { "b", "c" }));
        Assert.That(await keys.GetAssignmentAsync("Tutor", "openai"), Is.Empty);
        Assert.That(await keys.ProvidersWithKeysAsync(), Is.EqualTo(new[] { "alpaca", "claude", "grok" }));
        Assert.That(fake.Settings.ContainsKey("MindAttic:Vault:Brokers:alpaca:keys:paper"), Is.True);
        Assert.ThrowsAsync<ArgumentException>(() => keys.ReorderAsync("claude", new[] { "a", "b" }));
        Assert.ThrowsAsync<ArgumentException>(() => keys.ReorderAsync("claude", new[] { "a", "b", "B" }));
        Assert.ThrowsAsync<KeyNotFoundException>(() => keys.SetDisabledAsync("claude", "nope", true));

        await keys.SetAssignmentAsync("Tutor", "claude", Array.Empty<string>());
        Assert.That(fake.Settings.Keys.Any(k => k.StartsWith("MindAttic:Vault:Assignments:Tutor", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public async Task NamesWithFilterCharacters_AreStoredAndListedExactly()
    {
        var fake = new FakeAzure();
        var keys = Keys(fake);
        await keys.AddAsync("claude", "a,b*c\\d", "v1");
        await keys.AddAsync("claude", "日本語 キー", "v2");

        Assert.That((await keys.GetKeysAsync("claude")).Select(k => k.Name), Is.EqualTo(new[] { "a,b*c\\d", "日本語 キー" }));
        await keys.RenameAsync("claude", "a,b*c\\d", "plain");
        Assert.That((await keys.GetKeysAsync("claude")).Select(k => k.Name), Is.EqualTo(new[] { "plain", "日本語 キー" }));
    }

    [Test]
    public async Task AliasHeldKeys_AreListedButNotEditedHere()
    {
        var fake = new FakeAzure();
        var secret = fake.SeedSecret("legacy-anthropic", "alias-value");
        fake.Seed("MindAttic:Vault:LLM:anthropic:keys:legacy",
            $"{{\"uri\":\"https://{FakeAzure.VaultHost}/secrets/legacy-anthropic\"}}", KeyRef);
        var keys = Keys(fake);

        Assert.That(await keys.GetKeysAsync("claude"), Is.EqualTo(new[] { new NamedKey("legacy", "alias-value") }));
        Assert.That(secret, Is.Not.Empty);
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => keys.SetValueAsync("claude", "legacy", "x"));
        Assert.That(ex!.Message, Does.Contain("alias 'anthropic'"));
        Assert.ThrowsAsync<ArgumentException>(() => keys.AddAsync("claude", "LEGACY", "x"));
    }

    [Test]
    public void KeyVault_Forbidden_SaysWhichRoleOnWhichVault_WithoutTheValue()
    {
        var fake = new FakeAzure();
        fake.Handler.Route(r => r.Uri.Host == FakeAzure.VaultHost && r.Method == "PUT" && r.Header("Authorization") is not null
            ? FakeAzure.Json(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"Forbidden\",\"message\":\"Caller is not authorized.\"}}")
            : null);

        var ex = Assert.ThrowsAsync<ProfileAccessDeniedException>(() => Keys(fake).AddAsync("claude", "k", Canary));
        Assert.That(ex!.Message, Is.EqualTo("You need the 'Key Vault Secrets Officer' role on Key Vault 'testvault'."));
        Assert.That(ex.ToString(), Does.Not.Contain(Canary));
        Assert.That(fake.Settings, Is.Empty, "nothing was written to App Configuration");
    }

    [Test]
    public async Task AppConfiguration_Forbidden_SaysWhichRole_AndRemovesTheOrphanSecret()
    {
        var fake = new FakeAzure();
        fake.Handler.Route(r => r.Uri.Host == FakeAzure.StoreHost && r.Method == "PUT"
            ? FakeAzure.Json(HttpStatusCode.Forbidden, "{}")
            : null);

        var ex = Assert.ThrowsAsync<ProfileAccessDeniedException>(() => Keys(fake).AddAsync("claude", "k", Canary));
        Assert.That(ex!.Message, Does.Contain("App Configuration Data Owner").And.Contain("teststore"));
        Assert.That(ex.ToString(), Does.Not.Contain(Canary));
        Assert.That(fake.DeletedSecrets, Is.EquivalentTo(fake.Secrets.Keys), "the secret created first was soft-deleted again");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ConcurrentEdit_OfTheOrder_IsAConflict()
    {
        var fake = new FakeAzure();
        var keys = Keys(fake);
        await keys.AddAsync("claude", "a", "1");
        await keys.AddAsync("claude", "b", "2");

        // Another editor reorders between our read and our write.
        var raced = false;
        fake.Handler.Route(r =>
        {
            if (!raced && r.Method == "PUT" && r.Uri.AbsolutePath.Contains("keyOrder", StringComparison.Ordinal))
            {
                raced = true;
                fake.Seed("MindAttic:Vault:LLM:claude:keyOrder:0", "b");
            }
            return null;
        });

        Assert.ThrowsAsync<ConcurrencyConflictException>(() => keys.ReorderAsync("claude", new[] { "b", "a" }));
    }

    [Test]
    public async Task NoValueEverAppearsInAppConfigurationOrExceptionMessages()
    {
        var fake = new FakeAzure();
        var keys = Keys(fake);
        await keys.AddAsync("claude", "canary", Canary);
        await keys.SetValueAsync("claude", "canary", Canary + "-2");
        await keys.SetAssignmentAsync("App", "claude", new[] { "canary" });

        Assert.That(fake.Settings.Values.Any(s => s.Value?.Contains("canary-0123") == true), Is.False);
        var messages = new List<string>
        {
            Assert.ThrowsAsync<KeyInUseException>(() => keys.RemoveAsync("claude", "canary"))!.ToString(),
            Assert.ThrowsAsync<ArgumentException>(() => keys.AddAsync("claude", "canary", Canary))!.ToString(),
            Assert.ThrowsAsync<KeyNotFoundException>(() => keys.SetValueAsync("claude", "missing", Canary))!.ToString(),
        };
        Assert.That(messages, Has.None.Contains("canary-0123"));
        Assert.That((await keys.GetKeysAsync("claude")).Single().ToString(), Does.Not.Contain("canary-0123"));
        var appConfigBodies = fake.Handler.Requests.Where(r => r.Uri.Host == FakeAzure.StoreHost).Select(r => r.Body ?? "");
        Assert.That(appConfigBodies, Has.None.Contains("canary-0123"), "values only ever travel to Key Vault");
    }

    [Test]
    public async Task WhatWasWritten_ReadsBackThroughConfigurationKeySource_Identically()
    {
        var fake = new FakeAzure();
        var keys = Keys(fake);
        await keys.AddAsync("claude", "key1", "c1");
        await keys.AddAsync("claude", "2026-10-31 IdiotProof", "c2");
        await keys.AddAsync("claude", "spare", "c3");
        await keys.AddAsync("openai", "only", "o1");
        await keys.AddAsync("alpaca", "paper", "a1");
        await keys.ReorderAsync("claude", new[] { "spare", "2026-10-31 IdiotProof", "key1" });
        await keys.SetDisabledAsync("claude", "key1", true);
        await keys.SetValueAsync("claude", "spare", "c3-new");
        await keys.RenameAsync("claude", "spare", "backup");
        await keys.SetAssignmentAsync("IdiotProof", "claude", new[] { "2026-10-31 IdiotProof", "backup" });
        await keys.SetAssignmentAsync("Tutor", "openai", new[] { "only" });

        IConfiguration deployed = new ConfigurationBuilder().AddInMemoryCollection(fake.ResolvedConfiguration()).Build();
        var source = new ConfigurationKeySource(deployed);

        var expectedClaude = new[]
        {
            new NamedKey("backup", "c3-new"), new NamedKey("2026-10-31 IdiotProof", "c2"), new NamedKey("key1", "c1", Disabled: true),
        };
        Assert.Multiple(async () =>
        {
            Assert.That(source.GetKeys("claude"), Is.EqualTo(expectedClaude));
            Assert.That(source.GetKeys("anthropic"), Is.EqualTo(expectedClaude));
            Assert.That(await keys.GetKeysAsync("claude"), Is.EqualTo(expectedClaude));
            Assert.That(source.GetKeys("openai"), Is.EqualTo(new[] { new NamedKey("only", "o1") }));
            Assert.That(source.GetKeys("alpaca"), Is.EqualTo(new[] { new NamedKey("paper", "a1") }));
            Assert.That(source.GetAssignment("IdiotProof", "claude"), Is.EqualTo(new[] { "2026-10-31 IdiotProof", "backup" }));
            Assert.That(source.GetAssignment("IdiotProof", "anthropic"), Is.EqualTo(new[] { "2026-10-31 IdiotProof", "backup" }));
            Assert.That(source.GetAssignment("Tutor", "openai"), Is.EqualTo(new[] { "only" }));
            Assert.That(source.GetAssignment("Tutor", "claude"), Is.Empty);
        });

        // The deployed app's selector picks the assigned active key, by name, through the same tree.
        var selector = new KeySelector(new ConfigurationKeySource(deployed), new KeyHealthStore(Path.Combine(Path.GetTempPath(), "VaultDesktopAzureTests", Guid.NewGuid().ToString("N"))));
        Assert.That(selector.GetActiveKey("claude", "IdiotProof")?.Name, Is.EqualTo("2026-10-31 IdiotProof"));
    }
}
