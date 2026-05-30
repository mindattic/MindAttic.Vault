using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Credentials;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

/// <summary>
/// Second round of bug fixes ("find 10 / fix 10"). Each test fails against the
/// pre-fix code and pins the contract the fix restores. One test per bug.
/// </summary>
[TestFixture]
public class FindFixRegressionTests
{
    // Bug 1: a tokens.json carrying two keys that differ only in case (written by
    // another tool/version) made the OrdinalIgnoreCase copy-constructor throw, which
    // was swallowed — silently wiping EVERY token. It must collapse the collision
    // instead, keeping the tokens visible.
    [Test]
    public void TokenStore_CaseVariant_Duplicate_Keys_Do_Not_Wipe_All_Tokens()
    {
        using var tmp = new TempDirectory();
        var store = new TokenStore(tmp.Path);
        File.WriteAllText(store.TokensFilePath, "{\"github\":\"ghp_AAA\",\"GitHub\":\"ghp_BBB\"}");

        var all = store.LoadAll();

        Assert.That(all, Is.Not.Empty, "case-variant duplicate keys silently wiped every token");
        Assert.That(store.Get("github"), Is.Not.Null);
        Assert.That(store.Get("github"), Is.AnyOf("ghp_AAA", "ghp_BBB"));
    }

    // Bug 2: same defect in the legacy credentials.json layer — case-variant duplicate
    // keys threw in the copy-constructor and the swallow dropped EVERY legacy credential.
    [Test]
    public void CredentialStore_CaseVariant_Duplicate_Keys_In_CredentialsJson_Survive()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "credentials.json"),
            "{\"openai\":\"sk-a\",\"OpenAI\":\"sk-b\"}");

        var store = new CredentialStore(tmp.Path);

        Assert.That(store.LoadAll(), Is.Not.Empty, "case-variant duplicate keys dropped every legacy credential");
        Assert.That(store.GetKey("openai"), Is.AnyOf("sk-a", "sk-b"));
    }

    // Bug 3: an integer larger than Int64 was reformatted by GetDouble() into lossy
    // scientific notation ("1E+20"), corrupting the value and diverging from the stock
    // JsonConfigurationProvider. The verbatim digits must survive.
    [Test]
    public void Provider_Preserves_Integer_Larger_Than_Int64()
    {
        using var tmp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "LLM"));
        File.WriteAllText(Path.Combine(tmp.Path, "LLM", "providers.json"),
            "{ \"claude\": { \"apiKey\": \"sk\", \"big\": 99999999999999999999 } }");

        var config = new ConfigurationBuilder()
            .Add(new MindAtticConfigurationSource { RoamingRoot = tmp.Path, Buckets = new[] { "LLM" } })
            .Build();

        Assert.That(config["MindAttic:Vault:LLM:claude:big"], Is.EqualTo("99999999999999999999"));
    }

    // Bug 4: rotating apiKey via SetKey on a broker entry must not drop a non-string
    // secret/baseUrl. A dropped secret is unrecoverable credential loss.
    [Test]
    public void Broker_SetKey_Preserves_NonString_Secret_And_BaseUrl()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"),
            "{ \"alpaca-paper\": { \"type\": \"alpaca\", \"apiKey\": \"OLD\", \"secret\": 12345, \"baseUrl\": true } }");

        var store = new BrokerCredentialStore(tmp.Path);
        store.SetKey("alpaca-paper", "NEW");

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "providers.json")));
        var entry = doc.RootElement.GetProperty("alpaca-paper");
        Assert.That(entry.GetProperty("apiKey").GetString(), Is.EqualTo("NEW"));
        Assert.That(entry.TryGetProperty("secret", out var secret), Is.True, "non-string secret was dropped");
        Assert.That(secret.GetInt32(), Is.EqualTo(12345));
        Assert.That(entry.TryGetProperty("baseUrl", out var baseUrl), Is.True, "non-string baseUrl was dropped");
        Assert.That(baseUrl.ValueKind, Is.EqualTo(JsonValueKind.True));
    }

    // Bug 5: rotating apiKey on an LLM entry must not drop a non-string model/type.
    [Test]
    public void Llm_SetKey_Preserves_NonString_Model()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"),
            "{ \"claude\": { \"type\": \"anthropic\", \"apiKey\": \"old\", \"model\": 5 } }");

        var store = new LlmCredentialStore(tmp.Path);
        store.SetKey("claude", "new");

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "providers.json")));
        var entry = doc.RootElement.GetProperty("claude");
        Assert.That(entry.GetProperty("apiKey").GetString(), Is.EqualTo("new"));
        Assert.That(entry.TryGetProperty("model", out var model), Is.True, "non-string model was dropped");
        Assert.That(model.GetInt32(), Is.EqualTo(5));
    }

    // Bug 6: SetBrokerCreds must preserve a non-string `type` verbatim rather than
    // silently resetting it to the brokerType default.
    [Test]
    public void SetBrokerCreds_Preserves_NonString_Type()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"),
            "{ \"custom\": { \"type\": 7, \"apiKey\": \"OLD\", \"secret\": \"S\" } }");

        var store = new BrokerCredentialStore(tmp.Path);
        store.SetBrokerCreds("custom",
            new BrokerCredentialStore.BrokerCreds("NEW", "S2", BaseUrl: null), brokerType: "alpaca");

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "providers.json")));
        var entry = doc.RootElement.GetProperty("custom");
        Assert.That(entry.GetProperty("apiKey").GetString(), Is.EqualTo("NEW"));
        Assert.That(entry.GetProperty("type").ValueKind, Is.EqualTo(JsonValueKind.Number), "non-string type was reset");
        Assert.That(entry.GetProperty("type").GetInt32(), Is.EqualTo(7));
    }

    // Bug 7: across composite layers a higher-priority field must OVERRIDE a
    // differently-cased lower-priority field, not leave a stale duplicate (which would
    // leak the old credential alongside the new one).
    [Test]
    public void Composite_Merge_CaseVariant_Field_Overrides_Instead_Of_Duplicating()
    {
        using var lowDir  = new TempDirectory();
        using var highDir = new TempDirectory();
        // Lower layer uses a hand-edited "ApiKey" (capital A) plus a unique field.
        File.WriteAllText(Path.Combine(lowDir.Path, "providers.json"),
            "{ \"claude\": { \"ApiKey\": \"old\", \"model\": \"m\" } }");
        File.WriteAllText(Path.Combine(highDir.Path, "providers.json"),
            "{ \"claude\": { \"apiKey\": \"new\" } }");

        var composite = new CompositeCredentialStore(
            new CredentialStore(highDir.Path),   // higher priority first
            new CredentialStore(lowDir.Path));

        using var doc = JsonDocument.Parse(composite.LoadAllRaw()["claude"]);
        var root = doc.RootElement;

        // The lower layer's unique field survives...
        Assert.That(root.GetProperty("model").GetString(), Is.EqualTo("m"));
        // ...the higher apiKey wins...
        Assert.That(root.GetProperty("apiKey").GetString(), Is.EqualTo("new"));
        // ...and the stale case-variant "ApiKey" must NOT linger (no leaked old key).
        var apiKeyish = 0;
        foreach (var p in root.EnumerateObject())
            if (string.Equals(p.Name, "apiKey", StringComparison.OrdinalIgnoreCase))
                apiKeyish++;
        Assert.That(apiKeyish, Is.EqualTo(1), "stale case-variant apiKey leaked into the merged record");
    }

    // Bug 8: nested object/array provider fields must be projected as individually
    // navigable keys (:child / :index), matching the stock JsonConfigurationProvider —
    // not stuffed into the parent key as a raw-JSON blob.
    [Test]
    public void Provider_Flattens_Nested_Arrays_And_Objects()
    {
        using var tmp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "LLM"));
        File.WriteAllText(Path.Combine(tmp.Path, "LLM", "providers.json"),
            "{ \"claude\": { \"apiKey\": \"sk\", \"scopes\": [\"a\",\"b\"], \"nested\": { \"k\": 1 } } }");

        var config = new ConfigurationBuilder()
            .Add(new MindAtticConfigurationSource { RoamingRoot = tmp.Path, Buckets = new[] { "LLM" } })
            .Build();

        Assert.That(config["MindAttic:Vault:LLM:claude:scopes:0"], Is.EqualTo("a"));
        Assert.That(config["MindAttic:Vault:LLM:claude:scopes:1"], Is.EqualTo("b"));
        Assert.That(config["MindAttic:Vault:LLM:claude:nested:k"], Is.EqualTo("1"));
    }

    // Bug 9: with ReloadOnChange, a bucket directory created AFTER the first Load must
    // still be observed. Previously no watcher existed on the root, so the new bucket
    // was never picked up.
    [Test]
    public void ReloadOnChange_Picks_Up_Bucket_Dir_Created_After_First_Load()
    {
        using var tmp = new TempDirectory();   // root exists, but no bucket dirs yet

        var config = new ConfigurationBuilder()
            .Add(new MindAtticConfigurationSource
            {
                RoamingRoot = tmp.Path,
                Buckets = new[] { "LLM" },
                ReloadOnChange = true,
            })
            .Build();

        Assert.That(config["MindAttic:Vault:LLM:claude:apiKey"], Is.Null);

        // Create the bucket dir + providers.json only now, through the same atomic-swap
        // write path the library uses in production.
        var llm = Path.Combine(tmp.Path, "LLM");
        Directory.CreateDirectory(llm);
        new CredentialStore(llm).SetKey("claude", "after-reload");

        var deadline = DateTime.UtcNow.AddSeconds(8);
        string? observed = null;
        while (DateTime.UtcNow < deadline)
        {
            observed = config["MindAttic:Vault:LLM:claude:apiKey"];
            if (observed == "after-reload") break;
            Thread.Sleep(50);
        }

        Assert.That(observed, Is.EqualTo("after-reload"));
    }

    // Bug 10: an integer string too large for Int64 (e.g. a 20-digit id) must not be
    // mangled into lossy scientific notation by GetDouble — decimal preserves it.
    [Test]
    public void Config_LoadAllRaw_Preserves_Int64_Overflow_Numeric_Field()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MindAttic:Vault:LLM:claude:apiKey"] = "sk-ant",
            ["MindAttic:Vault:LLM:claude:bignum"] = "99999999999999999999",
        }).Build();

        var store = ConfigurationCredentialStore.ForLlm(config);
        using var doc = JsonDocument.Parse(store.LoadAllRaw()["claude"]);
        var bignum = doc.RootElement.GetProperty("bignum");

        Assert.That(bignum.ValueKind, Is.EqualTo(JsonValueKind.Number));
        Assert.That(bignum.GetRawText(), Is.EqualTo("99999999999999999999"));
    }
}
