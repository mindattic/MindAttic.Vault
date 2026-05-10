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
}
