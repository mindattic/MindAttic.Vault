using System.Text.Json;
using MindAttic.Vault.Credentials;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public class BrokerCredentialStoreTests
{
    [Test]
    public void GetBrokerCreds_Returns_Null_When_Provider_Missing()
    {
        using var tmp = new TempDirectory();
        var store = new BrokerCredentialStore(tmp.Path);
        Assert.That(store.GetBrokerCreds("alpaca-paper"), Is.Null);
    }

    [Test]
    public void GetBrokerCreds_Reads_All_Fields()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"), """
        {
          "alpaca-paper": {
            "type": "alpaca",
            "apiKey": "PK123",
            "secret": "SECRET123",
            "baseUrl": "https://paper-api.alpaca.markets"
          }
        }
        """);

        var store = new BrokerCredentialStore(tmp.Path);
        var creds = store.GetBrokerCreds("alpaca-paper");

        Assert.That(creds, Is.Not.Null);
        Assert.That(creds!.ApiKey,  Is.EqualTo("PK123"));
        Assert.That(creds.Secret,  Is.EqualTo("SECRET123"));
        Assert.That(creds.BaseUrl, Is.EqualTo("https://paper-api.alpaca.markets"));
    }

    [Test]
    public void GetBrokerCreds_Returns_Null_When_ApiKey_Or_Secret_Empty()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"), """
        {
          "alpaca-paper": { "type": "alpaca", "apiKey": "PK", "secret": "" }
        }
        """);

        var store = new BrokerCredentialStore(tmp.Path);
        Assert.That(store.GetBrokerCreds("alpaca-paper"), Is.Null);
    }

    [Test]
    public void SetBrokerCreds_Persists_All_Fields()
    {
        using var tmp = new TempDirectory();
        var store = new BrokerCredentialStore(tmp.Path);

        store.SetBrokerCreds("alpaca-live",
            new BrokerCredentialStore.BrokerCreds("AK", "SS", "https://api.alpaca.markets"));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "providers.json")));
        var entry = doc.RootElement.GetProperty("alpaca-live");
        Assert.That(entry.GetProperty("type").GetString(),    Is.EqualTo("alpaca"));
        Assert.That(entry.GetProperty("apiKey").GetString(),  Is.EqualTo("AK"));
        Assert.That(entry.GetProperty("secret").GetString(),  Is.EqualTo("SS"));
        Assert.That(entry.GetProperty("baseUrl").GetString(), Is.EqualTo("https://api.alpaca.markets"));
    }

    [Test]
    public void SetKey_Preserves_Secret_And_BaseUrl_When_Rotating_Only_ApiKey()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"), """
        {
          "alpaca-paper": {
            "type": "alpaca",
            "apiKey": "OLD",
            "secret": "S",
            "baseUrl": "https://paper-api.alpaca.markets"
          }
        }
        """);

        var store = new BrokerCredentialStore(tmp.Path);
        store.SetKey("alpaca-paper", "NEW");

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "providers.json")));
        var entry = doc.RootElement.GetProperty("alpaca-paper");
        Assert.That(entry.GetProperty("apiKey").GetString(),  Is.EqualTo("NEW"));
        Assert.That(entry.GetProperty("secret").GetString(),  Is.EqualTo("S"));
        Assert.That(entry.GetProperty("baseUrl").GetString(), Is.EqualTo("https://paper-api.alpaca.markets"));
    }
}
