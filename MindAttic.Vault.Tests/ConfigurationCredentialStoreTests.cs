using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Credentials;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public class ConfigurationCredentialStoreTests
{
    private static IConfiguration BuildConfig(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Test]
    public void GetKey_Reads_Standard_Llm_Section()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["MindAttic:Vault:LLM:claude:apiKey"] = "sk-from-config"
        });

        var store = ConfigurationCredentialStore.ForLlm(config);
        Assert.That(store.GetKey("claude"), Is.EqualTo("sk-from-config"));
    }

    [Test]
    public void GetKey_Returns_Null_For_Missing_Provider()
    {
        var config = BuildConfig(new Dictionary<string, string?>());
        var store = ConfigurationCredentialStore.ForLlm(config);
        Assert.That(store.GetKey("claude"), Is.Null);
    }

    [Test]
    public void GetKey_Trims_Whitespace()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["MindAttic:Vault:LLM:claude:apiKey"] = "  sk-padded  "
        });

        Assert.That(ConfigurationCredentialStore.ForLlm(config).GetKey("claude"),
            Is.EqualTo("sk-padded"));
    }

    [Test]
    public void LoadAll_Returns_All_Providers()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["MindAttic:Vault:LLM:claude:apiKey"] = "C",
            ["MindAttic:Vault:LLM:gemini:apiKey"] = "G",
            ["MindAttic:Vault:LLM:grok:apiKey"]   = "X",
        });

        var all = ConfigurationCredentialStore.ForLlm(config).LoadAll();
        Assert.That(all, Has.Count.EqualTo(3));
        Assert.That(all["claude"], Is.EqualTo("C"));
        Assert.That(all["gemini"], Is.EqualTo("G"));
        Assert.That(all["grok"],   Is.EqualTo("X"));
    }

    [Test]
    public void Brokers_Bucket_Separates_From_Llm_Bucket()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["MindAttic:Vault:LLM:claude:apiKey"]                = "L",
            ["MindAttic:Vault:Brokers:alpaca-paper:apiKey"]      = "B",
        });

        Assert.That(ConfigurationCredentialStore.ForLlm(config).GetKey("alpaca-paper"),     Is.Null);
        Assert.That(ConfigurationCredentialStore.ForBrokers(config).GetKey("alpaca-paper"), Is.EqualTo("B"));
    }

    [Test]
    public void LoadAllRaw_Reconstructs_Provider_Object_With_All_Fields()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["MindAttic:Vault:LLM:claude:type"]      = "anthropic",
            ["MindAttic:Vault:LLM:claude:apiKey"]    = "sk",
            ["MindAttic:Vault:LLM:claude:model"]     = "claude-sonnet-4-6",
            ["MindAttic:Vault:LLM:claude:maxTokens"] = "8192",
        });

        var raw = ConfigurationCredentialStore.ForLlm(config).LoadAllRaw();
        Assert.That(raw, Does.ContainKey("claude"));

        using var doc = JsonDocument.Parse(raw["claude"]);
        Assert.That(doc.RootElement.GetProperty("type").GetString(),     Is.EqualTo("anthropic"));
        Assert.That(doc.RootElement.GetProperty("apiKey").GetString(),   Is.EqualTo("sk"));
        Assert.That(doc.RootElement.GetProperty("model").GetString(),    Is.EqualTo("claude-sonnet-4-6"));
        Assert.That(doc.RootElement.GetProperty("maxTokens").GetInt32(), Is.EqualTo(8192));
    }

    [Test]
    public void SetKey_Throws_NotSupported()
    {
        var config = BuildConfig(new Dictionary<string, string?>());
        var store = ConfigurationCredentialStore.ForLlm(config);
        Assert.Throws<NotSupportedException>(() => store.SetKey("claude", "x"));
    }

    [Test]
    public void ProvidersFileExists_Reflects_Section_Presence()
    {
        var empty = ConfigurationCredentialStore.ForLlm(BuildConfig(new Dictionary<string, string?>()));
        Assert.That(empty.ProvidersFileExists(), Is.False);

        var populated = ConfigurationCredentialStore.ForLlm(BuildConfig(new Dictionary<string, string?>
        {
            ["MindAttic:Vault:LLM:claude:apiKey"] = "k"
        }));
        Assert.That(populated.ProvidersFileExists(), Is.True);
    }

    // ── Constructor / argument validation ───────────────────────────────────────

    [Test]
    public void Constructor_Throws_For_Null_Configuration()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ConfigurationCredentialStore(null!, "MindAttic:Vault:LLM"));
    }

    [Test]
    public void Constructor_Throws_For_Empty_Bucket_Section()
    {
        var config = BuildConfig(new Dictionary<string, string?>());
        Assert.Throws<ArgumentException>(() => new ConfigurationCredentialStore(config, ""));
        Assert.Throws<ArgumentException>(() => new ConfigurationCredentialStore(config, "   "));
        Assert.Throws<ArgumentException>(() => new ConfigurationCredentialStore(config, null!));
    }

    [Test]
    public void GetKey_Returns_Null_For_Empty_Provider_Id()
    {
        var store = ConfigurationCredentialStore.ForLlm(BuildConfig(new Dictionary<string, string?>
        {
            ["MindAttic:Vault:LLM:claude:apiKey"] = "k"
        }));
        Assert.That(store.GetKey(""),    Is.Null);
        Assert.That(store.GetKey("   "), Is.Null);
    }

    // ── Read-only contract ──────────────────────────────────────────────────────

    [Test]
    public void Write_Methods_All_Throw_NotSupported()
    {
        var store = ConfigurationCredentialStore.ForLlm(BuildConfig(new Dictionary<string, string?>()));
        Assert.Throws<NotSupportedException>(() => store.SetKey("claude", "x"));
        Assert.Throws<NotSupportedException>(() => store.SaveAllRaw(new Dictionary<string, string>()));
        Assert.Throws<NotSupportedException>(() => store.SaveRaw("claude", "{}"));
    }

    // ── Properties / sentinels ──────────────────────────────────────────────────

    [Test]
    public void BucketSection_Exposes_Constructor_Argument()
    {
        var custom = new ConfigurationCredentialStore(
            BuildConfig(new Dictionary<string, string?>()),
            "Custom:Section:Path");
        Assert.That(custom.BucketSection, Is.EqualTo("Custom:Section:Path"));
    }

    [Test]
    public void Directory_And_ProvidersFilePath_Use_Synthetic_Sentinels()
    {
        var store = ConfigurationCredentialStore.ForLlm(BuildConfig(new Dictionary<string, string?>()));
        Assert.That(store.Directory,         Is.EqualTo("(configuration)"));
        Assert.That(store.ProvidersFilePath, Is.EqualTo("(configuration:MindAttic:Vault:LLM)"));
    }

    [Test]
    public void ListProviders_Returns_All_Providers()
    {
        var store = ConfigurationCredentialStore.ForLlm(BuildConfig(new Dictionary<string, string?>
        {
            ["MindAttic:Vault:LLM:claude:apiKey"] = "C",
            ["MindAttic:Vault:LLM:gemini:apiKey"] = "G",
        }));

        Assert.That(store.ListProviders(), Is.EquivalentTo(new[] { "claude", "gemini" }));
    }

    // ── LoadAllRaw scalar coercion ──────────────────────────────────────────────

    [Test]
    public void LoadAllRaw_Coerces_Bool_Int_And_Double_Leaves()
    {
        var store = ConfigurationCredentialStore.ForLlm(BuildConfig(new Dictionary<string, string?>
        {
            ["MindAttic:Vault:LLM:claude:apiKey"]    = "sk",
            ["MindAttic:Vault:LLM:claude:enabled"]   = "true",
            ["MindAttic:Vault:LLM:claude:maxTokens"] = "8192",
            ["MindAttic:Vault:LLM:claude:weight"]    = "0.75",
        }));

        var raw = store.LoadAllRaw();
        using var doc = JsonDocument.Parse(raw["claude"]);
        Assert.That(doc.RootElement.GetProperty("enabled").ValueKind,   Is.EqualTo(JsonValueKind.True));
        Assert.That(doc.RootElement.GetProperty("maxTokens").ValueKind, Is.EqualTo(JsonValueKind.Number));
        Assert.That(doc.RootElement.GetProperty("weight").GetDouble(),  Is.EqualTo(0.75).Within(0.0001));
    }

    [Test]
    public void LoadAllRaw_Surfaces_Numeric_Keyed_Children_As_Array()
    {
        var store = ConfigurationCredentialStore.ForLlm(BuildConfig(new Dictionary<string, string?>
        {
            ["MindAttic:Vault:LLM:claude:tags:0"] = "alpha",
            ["MindAttic:Vault:LLM:claude:tags:1"] = "beta",
            ["MindAttic:Vault:LLM:claude:tags:2"] = "gamma",
        }));

        var raw = store.LoadAllRaw();
        using var doc = JsonDocument.Parse(raw["claude"]);
        var tags = doc.RootElement.GetProperty("tags");
        Assert.That(tags.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(tags.EnumerateArray().Select(e => e.GetString()),
            Is.EqualTo(new[] { "alpha", "beta", "gamma" }));
    }

    [Test]
    public void GetKey_Trims_Trailing_Whitespace_With_All_Whitespace_Falling_Back_To_Null()
    {
        var store = ConfigurationCredentialStore.ForLlm(BuildConfig(new Dictionary<string, string?>
        {
            ["MindAttic:Vault:LLM:ghost:apiKey"] = "   "
        }));
        Assert.That(store.GetKey("ghost"), Is.Null);
    }
}
