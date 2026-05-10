using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public class MindAtticConfigurationSourceTests
{
    [Test]
    public void Surfaces_Existing_Providers_Json_Under_MindAttic_Vault_Section()
    {
        using var tmp = new TempDirectory();
        var llmDir = Path.Combine(tmp.Path, "LLM");
        Directory.CreateDirectory(llmDir);
        File.WriteAllText(Path.Combine(llmDir, "providers.json"), """
        {
          "claude": { "type": "anthropic", "apiKey": "sk-ant-fromfile", "model": "claude-sonnet-4-6", "maxTokens": 8192 }
        }
        """);

        var config = new ConfigurationBuilder()
            .Add(new MindAtticConfigurationSource { RoamingRoot = tmp.Path })
            .Build();

        Assert.That(config["MindAttic:Vault:LLM:claude:apiKey"],    Is.EqualTo("sk-ant-fromfile"));
        Assert.That(config["MindAttic:Vault:LLM:claude:type"],      Is.EqualTo("anthropic"));
        Assert.That(config["MindAttic:Vault:LLM:claude:model"],     Is.EqualTo("claude-sonnet-4-6"));
        Assert.That(config["MindAttic:Vault:LLM:claude:maxTokens"], Is.EqualTo("8192"));
    }

    [Test]
    public void Surfaces_Per_Provider_Key_Files_With_Highest_Priority()
    {
        using var tmp = new TempDirectory();
        var llmDir = Path.Combine(tmp.Path, "LLM");
        Directory.CreateDirectory(llmDir);
        File.WriteAllText(Path.Combine(llmDir, "providers.json"),
            """ { "claude": { "apiKey": "from-providers" } } """);
        File.WriteAllText(Path.Combine(llmDir, "claude.key"), "from-key-file");

        var config = new ConfigurationBuilder()
            .Add(new MindAtticConfigurationSource { RoamingRoot = tmp.Path })
            .Build();

        Assert.That(config["MindAttic:Vault:LLM:claude:apiKey"], Is.EqualTo("from-key-file"));
    }

    [Test]
    public void Reads_Multiple_Buckets()
    {
        using var tmp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "LLM"));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "Brokers"));

        File.WriteAllText(Path.Combine(tmp.Path, "LLM", "providers.json"),
            """ { "claude": { "apiKey": "L" } } """);
        File.WriteAllText(Path.Combine(tmp.Path, "Brokers", "providers.json"),
            """ { "alpaca-paper": { "apiKey": "B", "secret": "S" } } """);

        var config = new ConfigurationBuilder()
            .Add(new MindAtticConfigurationSource { RoamingRoot = tmp.Path })
            .Build();

        Assert.That(config["MindAttic:Vault:LLM:claude:apiKey"],            Is.EqualTo("L"));
        Assert.That(config["MindAttic:Vault:Brokers:alpaca-paper:apiKey"],  Is.EqualTo("B"));
        Assert.That(config["MindAttic:Vault:Brokers:alpaca-paper:secret"],  Is.EqualTo("S"));
    }

    [Test]
    public void Empty_Tmp_Yields_No_Keys_Without_Throwing()
    {
        using var tmp = new TempDirectory();

        var config = new ConfigurationBuilder()
            .Add(new MindAtticConfigurationSource { RoamingRoot = tmp.Path })
            .Build();

        Assert.That(config["MindAttic:Vault:LLM:claude:apiKey"], Is.Null);
    }

    [Test]
    public void Env_Vars_Override_File_Values_Through_Standard_Provider_Order()
    {
        // Confirms the cloud-native promise: env vars (App Service Application Settings)
        // beat the on-disk %APPDATA% file when both are present.
        using var tmp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "LLM"));
        File.WriteAllText(Path.Combine(tmp.Path, "LLM", "providers.json"),
            """ { "claude": { "apiKey": "from-disk" } } """);

        const string envVar = "MindAttic__Vault__LLM__claude__apiKey";
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "from-env");

            var config = new ConfigurationBuilder()
                .Add(new MindAtticConfigurationSource { RoamingRoot = tmp.Path })
                .AddEnvironmentVariables()
                .Build();

            Assert.That(config["MindAttic:Vault:LLM:claude:apiKey"], Is.EqualTo("from-env"));
        }
        finally { Environment.SetEnvironmentVariable(envVar, original); }
    }
}
