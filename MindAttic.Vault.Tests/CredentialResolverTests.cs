using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Credentials;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public class CredentialResolverTests
{
    [Test]
    public void LlmCredentialResolver_Reads_Configuration_First_File_Second()
    {
        using var tmp = new TempDirectory();
        var fileStore = new LlmCredentialStore(tmp.Path);
        fileStore.SetKey("gemini", "G-from-file");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MindAttic:Vault:LLM:claude:apiKey"] = "C-from-config"
            })
            .Build();

        var resolver = new LlmCredentialResolver(config, fileStore);

        Assert.That(resolver.GetKey("claude"), Is.EqualTo("C-from-config"));
        Assert.That(resolver.GetKey("gemini"), Is.EqualTo("G-from-file"));
    }

    [Test]
    public void LlmCredentialResolver_SetKey_Lands_In_File_Store()
    {
        using var tmp = new TempDirectory();
        var fileStore = new LlmCredentialStore(tmp.Path);
        var config = new ConfigurationBuilder().Build();

        var resolver = new LlmCredentialResolver(config, fileStore);
        resolver.SetKey("openai", "k");

        Assert.That(fileStore.GetKey("openai"), Is.EqualTo("k"));
    }

    [Test]
    public void BrokerCredentialResolver_Reads_Configuration_First_File_Second()
    {
        using var tmp = new TempDirectory();
        var fileStore = new BrokerCredentialStore(tmp.Path);
        fileStore.SetBrokerCreds("alpaca-live",
            new BrokerCredentialStore.BrokerCreds("AK-file", "S-file", null));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MindAttic:Vault:Brokers:alpaca-paper:apiKey"] = "PK-from-config"
            })
            .Build();

        var resolver = new BrokerCredentialResolver(config, fileStore);

        Assert.That(resolver.GetKey("alpaca-paper"), Is.EqualTo("PK-from-config"));
        Assert.That(resolver.GetKey("alpaca-live"),  Is.EqualTo("AK-file"));
    }
}
