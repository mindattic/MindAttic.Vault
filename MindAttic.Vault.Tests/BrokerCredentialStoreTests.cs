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

    // ── GetBrokerCreds defensive paths ──────────────────────────────────────────

    [Test]
    public void GetBrokerCreds_Returns_Null_For_Empty_Provider_Id()
    {
        using var tmp = new TempDirectory();
        var store = new BrokerCredentialStore(tmp.Path);
        Assert.That(store.GetBrokerCreds(""),    Is.Null);
        Assert.That(store.GetBrokerCreds("   "), Is.Null);
        Assert.That(store.GetBrokerCreds(null!), Is.Null);
    }

    [Test]
    public void GetBrokerCreds_Returns_Null_When_Secret_Missing()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"),
            """ { "alpaca-paper": { "apiKey": "PK" } } """);

        var store = new BrokerCredentialStore(tmp.Path);
        Assert.That(store.GetBrokerCreds("alpaca-paper"), Is.Null);
    }

    [Test]
    public void GetBrokerCreds_Returns_Null_When_ApiKey_Missing()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"),
            """ { "alpaca-paper": { "secret": "S" } } """);

        var store = new BrokerCredentialStore(tmp.Path);
        Assert.That(store.GetBrokerCreds("alpaca-paper"), Is.Null);
    }

    [Test]
    public void GetBrokerCreds_Trims_Whitespace_On_All_Fields()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"), """
        {
          "alpaca-paper": {
            "apiKey":  "  PK123  ",
            "secret":  "  SECRET123  ",
            "baseUrl": "  https://x  "
          }
        }
        """);

        var creds = new BrokerCredentialStore(tmp.Path).GetBrokerCreds("alpaca-paper");
        Assert.That(creds, Is.Not.Null);
        Assert.That(creds!.ApiKey,  Is.EqualTo("PK123"));
        Assert.That(creds.Secret,   Is.EqualTo("SECRET123"));
        Assert.That(creds.BaseUrl,  Is.EqualTo("https://x"));
    }

    [Test]
    public void GetBrokerCreds_Returns_Null_For_Wrong_Field_Types()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"),
            """ { "alpaca-paper": { "apiKey": 123, "secret": true } } """);

        var store = new BrokerCredentialStore(tmp.Path);
        Assert.That(store.GetBrokerCreds("alpaca-paper"), Is.Null);
    }

    // ── SetBrokerCreds argument validation ──────────────────────────────────────

    [Test]
    public void SetBrokerCreds_Throws_For_Empty_Provider_Id()
    {
        using var tmp = new TempDirectory();
        var store = new BrokerCredentialStore(tmp.Path);
        var creds = new BrokerCredentialStore.BrokerCreds("a", "b", null);
        Assert.Throws<ArgumentException>(() => store.SetBrokerCreds("",    creds));
        Assert.Throws<ArgumentException>(() => store.SetBrokerCreds("   ", creds));
    }

    [Test]
    public void SetBrokerCreds_Throws_When_Creds_Null()
    {
        using var tmp = new TempDirectory();
        var store = new BrokerCredentialStore(tmp.Path);
        Assert.Throws<ArgumentNullException>(() => store.SetBrokerCreds("alpaca-paper", null!));
    }

    [Test]
    public void SetBrokerCreds_Preserves_Existing_Type_Field()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"), """
        { "custom-broker": { "type": "custom", "apiKey": "OLD", "secret": "S" } }
        """);

        var store = new BrokerCredentialStore(tmp.Path);
        store.SetBrokerCreds("custom-broker",
            new BrokerCredentialStore.BrokerCreds("NEW", "S2", null),
            brokerType: "alpaca");  // explicit default would be "alpaca"; existing wins.

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "providers.json")));
        Assert.That(doc.RootElement.GetProperty("custom-broker").GetProperty("type").GetString(),
            Is.EqualTo("custom"));
    }

    [Test]
    public void SetBrokerCreds_Omits_BaseUrl_When_Null_Or_Empty()
    {
        using var tmp = new TempDirectory();
        var store = new BrokerCredentialStore(tmp.Path);
        store.SetBrokerCreds("alpaca-paper",
            new BrokerCredentialStore.BrokerCreds("PK", "S", null));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "providers.json")));
        Assert.That(doc.RootElement.GetProperty("alpaca-paper").TryGetProperty("baseUrl", out _), Is.False);
    }

    [Test]
    public void SetKey_Defaults_Type_To_Alpaca_For_Alpaca_Prefix()
    {
        using var tmp = new TempDirectory();
        var store = new BrokerCredentialStore(tmp.Path);
        store.SetKey("alpaca-paper", "PK");

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "providers.json")));
        Assert.That(doc.RootElement.GetProperty("alpaca-paper").GetProperty("type").GetString(),
            Is.EqualTo("alpaca"));
    }

    [Test]
    public void SetKey_Defaults_Type_To_Bearer_For_Non_Alpaca_Prefix()
    {
        using var tmp = new TempDirectory();
        var store = new BrokerCredentialStore(tmp.Path);
        store.SetKey("schwab", "X");

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "providers.json")));
        Assert.That(doc.RootElement.GetProperty("schwab").GetProperty("type").GetString(),
            Is.EqualTo("bearer"));
    }

    // ── Static surface ──────────────────────────────────────────────────────────

    [Test]
    public void Default_Singleton_Is_Resolvable()
    {
        Assert.That(BrokerCredentialStore.Default, Is.Not.Null);
        Assert.That(BrokerCredentialStore.Default.Directory, Is.Not.Empty);
    }

    [Test]
    public void Bucket_And_DirectoryEnvVar_Constants_Are_Stable()
    {
        Assert.That(BrokerCredentialStore.Bucket,          Is.EqualTo("Brokers"));
        Assert.That(BrokerCredentialStore.DirectoryEnvVar, Is.EqualTo("MINDATTIC_BROKER_CREDENTIALS"));
    }
}
