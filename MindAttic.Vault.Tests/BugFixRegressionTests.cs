using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Paths;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

/// <summary>
/// Regression tests pinning the round of bug fixes. Each test fails against the
/// pre-fix code and documents the contract the fix restores.
/// </summary>
[TestFixture]
public class BugFixRegressionTests
{
    // Bug 1: legacy credentials.json lookups must honour the documented
    // case-insensitive provider-id contract (the .key and providers.json layers
    // already do; this layer used a case-sensitive dictionary).
    [Test]
    public void GetKey_From_CredentialsJson_Is_Case_Insensitive()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "credentials.json"), "{ \"openai\": \"sk-legacy\" }");

        var store = new CredentialStore(tmp.Path);
        Assert.That(store.GetKey("openai"), Is.EqualTo("sk-legacy"));
        Assert.That(store.GetKey("OpenAI"), Is.EqualTo("sk-legacy"));
        Assert.That(store.GetKey("OPENAI"), Is.EqualTo("sk-legacy"));
    }

    // Bug 4: rotating apiKey must not silently drop a maxTokens that isn't an
    // Int32-parseable JSON number (a hand-edited string value, here). It is
    // preserved verbatim rather than discarded.
    [Test]
    public void SetKey_Preserves_StringTyped_MaxTokens_On_Rotation()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"),
            "{ \"claude\": { \"type\": \"anthropic\", \"apiKey\": \"old\", \"maxTokens\": \"8192\" } }");

        var store = new LlmCredentialStore(tmp.Path);
        store.SetKey("claude", "new");

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "providers.json")));
        var entry = doc.RootElement.GetProperty("claude");
        Assert.That(entry.GetProperty("apiKey").GetString(), Is.EqualTo("new"));
        Assert.That(entry.TryGetProperty("maxTokens", out var mt), Is.True, "maxTokens was dropped");
        Assert.That(mt.GetString(), Is.EqualTo("8192"));
    }

    // Bug 4 (companion): a proper integer maxTokens still canonicalizes to a JSON
    // number — the fix must not regress the common case.
    [Test]
    public void SetKey_Keeps_Integer_MaxTokens_As_Number()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"),
            "{ \"claude\": { \"type\": \"anthropic\", \"apiKey\": \"old\", \"maxTokens\": 8192 } }");

        var store = new LlmCredentialStore(tmp.Path);
        store.SetKey("claude", "new");

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "providers.json")));
        var mt = doc.RootElement.GetProperty("claude").GetProperty("maxTokens");
        Assert.That(mt.ValueKind, Is.EqualTo(JsonValueKind.Number));
        Assert.That(mt.GetInt32(), Is.EqualTo(8192));
    }

    // Bug 5: credential-bearing fields coming from configuration (always strings)
    // must not be retyped to JSON numbers/booleans by LoadAllRaw — a string-typed
    // reader (e.g. GetBrokerCreds) would otherwise drop them.
    [Test]
    public void Config_LoadAllRaw_Keeps_Numeric_Credentials_As_Strings()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MindAttic:Vault:Brokers:alpaca-paper:apiKey"] = "1234567890",
            ["MindAttic:Vault:Brokers:alpaca-paper:secret"] = "0098765",
        }).Build();

        var store = ConfigurationCredentialStore.ForBrokers(config);
        using var doc = JsonDocument.Parse(store.LoadAllRaw()["alpaca-paper"]);

        Assert.That(doc.RootElement.GetProperty("apiKey").ValueKind, Is.EqualTo(JsonValueKind.String));
        Assert.That(doc.RootElement.GetProperty("secret").ValueKind, Is.EqualTo(JsonValueKind.String));
        // Leading zero must survive — proof it was never coerced to a number.
        Assert.That(doc.RootElement.GetProperty("secret").GetString(), Is.EqualTo("0098765"));
    }

    // Bug 5 (companion): non-credential numeric fields (maxTokens) still infer to a
    // JSON number, so the rich payload round-trips with the file format.
    [Test]
    public void Config_LoadAllRaw_Still_Infers_Numeric_MaxTokens()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MindAttic:Vault:LLM:claude:apiKey"]    = "sk-ant",
            ["MindAttic:Vault:LLM:claude:maxTokens"] = "8192",
        }).Build();

        var store = ConfigurationCredentialStore.ForLlm(config);
        using var doc = JsonDocument.Parse(store.LoadAllRaw()["claude"]);

        Assert.That(doc.RootElement.GetProperty("maxTokens").ValueKind, Is.EqualTo(JsonValueKind.Number));
        Assert.That(doc.RootElement.GetProperty("maxTokens").GetInt32(), Is.EqualTo(8192));
    }

    // Bug 9: the full broker setter must preserve user-added fields outside the
    // canonical {type, apiKey, secret, baseUrl} set, matching the SetKey path.
    [Test]
    public void SetBrokerCreds_Preserves_UserAdded_Extra_Fields()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"),
            "{ \"alpaca-paper\": { \"type\": \"alpaca\", \"apiKey\": \"OLD\", \"secret\": \"S\", \"accountId\": \"acct-123\" } }");

        var store = new BrokerCredentialStore(tmp.Path);
        store.SetBrokerCreds("alpaca-paper",
            new BrokerCredentialStore.BrokerCreds("NEW", "S2", BaseUrl: null));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "providers.json")));
        var entry = doc.RootElement.GetProperty("alpaca-paper");
        Assert.That(entry.GetProperty("apiKey").GetString(), Is.EqualTo("NEW"));
        Assert.That(entry.GetProperty("secret").GetString(), Is.EqualTo("S2"));
        Assert.That(entry.TryGetProperty("accountId", out var acct), Is.True, "accountId was dropped");
        Assert.That(acct.GetString(), Is.EqualTo("acct-123"));
    }

    // Bug 7: an override env var explicitly set to whitespace must be treated as
    // unset, falling back to a real rooted app-data path rather than a blank path.
    [Test]
    public void RoamingRoot_Treats_Blank_Override_As_Unset()
    {
        var key = VaultPaths.RoamingRootEnvVar;
        var original = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, "   ");
            var root = VaultPaths.RoamingRoot;
            Assert.That(string.IsNullOrWhiteSpace(root), Is.False);
            Assert.That(Path.IsPathRooted(root), Is.True);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, original);
        }
    }
}
