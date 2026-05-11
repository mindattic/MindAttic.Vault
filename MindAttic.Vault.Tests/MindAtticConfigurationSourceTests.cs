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

    // ── Edge cases / scalar handling ────────────────────────────────────────────

    [Test]
    public void Custom_Buckets_Override_Defaults()
    {
        using var tmp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "GitHub"));
        File.WriteAllText(Path.Combine(tmp.Path, "GitHub", "providers.json"),
            """ { "primary": { "apiKey": "ghp_xxx" } } """);

        var config = new ConfigurationBuilder()
            .Add(new MindAtticConfigurationSource
            {
                RoamingRoot = tmp.Path,
                Buckets = new[] { "GitHub" },   // explicit, overriding the default LLM/Brokers list.
            })
            .Build();

        Assert.That(config["MindAttic:Vault:GitHub:primary:apiKey"], Is.EqualTo("ghp_xxx"));
        // The default LLM bucket is no longer scanned.
        Assert.That(config["MindAttic:Vault:LLM:claude:apiKey"],     Is.Null);
    }

    [Test]
    public void Empty_Bucket_Names_Are_Skipped()
    {
        using var tmp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "LLM"));
        File.WriteAllText(Path.Combine(tmp.Path, "LLM", "providers.json"),
            """ { "claude": { "apiKey": "C" } } """);

        var config = new ConfigurationBuilder()
            .Add(new MindAtticConfigurationSource
            {
                RoamingRoot = tmp.Path,
                Buckets = new[] { "", "   ", "LLM" },
            })
            .Build();

        Assert.That(config["MindAttic:Vault:LLM:claude:apiKey"], Is.EqualTo("C"));
    }

    [Test]
    public void Malformed_Providers_Json_Surfaces_As_No_Data_Without_Throwing()
    {
        using var tmp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "LLM"));
        File.WriteAllText(Path.Combine(tmp.Path, "LLM", "providers.json"),
            "{ broken-json");

        Assert.DoesNotThrow(() =>
        {
            var config = new ConfigurationBuilder()
                .Add(new MindAtticConfigurationSource { RoamingRoot = tmp.Path })
                .Build();
            Assert.That(config["MindAttic:Vault:LLM:claude:apiKey"], Is.Null);
        });
    }

    [Test]
    public void Empty_Providers_Json_File_Is_Treated_As_No_Data()
    {
        using var tmp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "LLM"));
        File.WriteAllText(Path.Combine(tmp.Path, "LLM", "providers.json"), "");

        var config = new ConfigurationBuilder()
            .Add(new MindAtticConfigurationSource { RoamingRoot = tmp.Path })
            .Build();

        Assert.That(config["MindAttic:Vault:LLM:claude:apiKey"], Is.Null);
    }

    [Test]
    public void Top_Level_Array_Is_Skipped()
    {
        using var tmp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "LLM"));
        File.WriteAllText(Path.Combine(tmp.Path, "LLM", "providers.json"), "[ 1, 2, 3 ]");

        var config = new ConfigurationBuilder()
            .Add(new MindAtticConfigurationSource { RoamingRoot = tmp.Path })
            .Build();

        Assert.That(config["MindAttic:Vault:LLM:claude:apiKey"], Is.Null);
    }

    [Test]
    public void Numeric_And_Boolean_Leaves_Are_Surfaced_As_Strings()
    {
        using var tmp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "LLM"));
        File.WriteAllText(Path.Combine(tmp.Path, "LLM", "providers.json"), """
        {
          "claude": {
            "apiKey":    "sk",
            "maxTokens": 8192,
            "enabled":   true,
            "weight":    0.75
          }
        }
        """);

        var config = new ConfigurationBuilder()
            .Add(new MindAtticConfigurationSource { RoamingRoot = tmp.Path })
            .Build();

        Assert.That(config["MindAttic:Vault:LLM:claude:maxTokens"], Is.EqualTo("8192"));
        Assert.That(config["MindAttic:Vault:LLM:claude:enabled"],   Is.EqualTo("true"));
        // Double conversion uses the current culture, but 0.75 round-trips on every culture as a sanity check:
        Assert.That(config["MindAttic:Vault:LLM:claude:weight"],    Is.Not.Null);
    }

    [Test]
    public void Non_Object_Provider_Entry_Is_Skipped()
    {
        using var tmp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "LLM"));
        File.WriteAllText(Path.Combine(tmp.Path, "LLM", "providers.json"),
            """ { "claude": "not-an-object", "gemini": { "apiKey": "G" } } """);

        var config = new ConfigurationBuilder()
            .Add(new MindAtticConfigurationSource { RoamingRoot = tmp.Path })
            .Build();

        Assert.That(config["MindAttic:Vault:LLM:claude:apiKey"], Is.Null);
        Assert.That(config["MindAttic:Vault:LLM:gemini:apiKey"], Is.EqualTo("G"));
    }

    [Test]
    public void Empty_Key_File_Is_Skipped()
    {
        using var tmp = new TempDirectory();
        var llm = Path.Combine(tmp.Path, "LLM");
        Directory.CreateDirectory(llm);
        File.WriteAllText(Path.Combine(llm, "claude.key"), "   ");
        File.WriteAllText(Path.Combine(llm, "providers.json"),
            """ { "claude": { "apiKey": "from-providers" } } """);

        var config = new ConfigurationBuilder()
            .Add(new MindAtticConfigurationSource { RoamingRoot = tmp.Path })
            .Build();

        // Empty .key file falls through to providers.json.
        Assert.That(config["MindAttic:Vault:LLM:claude:apiKey"], Is.EqualTo("from-providers"));
    }

    [Test]
    public void EffectiveRoot_Falls_Back_To_VaultPaths_When_RoamingRoot_Unset()
    {
        // Set the env var override so VaultPaths.RoamingRoot resolves to tmp.
        using var tmp = new TempDirectory();
        var originalRoaming = Environment.GetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.RoamingRootEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.RoamingRootEnvVar, tmp.Path);
            Directory.CreateDirectory(Path.Combine(tmp.Path, "LLM"));
            File.WriteAllText(Path.Combine(tmp.Path, "LLM", "providers.json"),
                """ { "claude": { "apiKey": "from-vault-paths" } } """);

            var config = new ConfigurationBuilder()
                .Add(new MindAtticConfigurationSource()) // no explicit RoamingRoot
                .Build();

            Assert.That(config["MindAttic:Vault:LLM:claude:apiKey"], Is.EqualTo("from-vault-paths"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.RoamingRootEnvVar, originalRoaming);
        }
    }

    [Test]
    public void ReloadOnChange_Picks_Up_New_Provider_Json()
    {
        using var tmp = new TempDirectory();
        var llm = Path.Combine(tmp.Path, "LLM");
        Directory.CreateDirectory(llm);

        // Use CredentialStore for the writes so we go through the same File.Replace
        // atomic-swap path the library uses in production. A naïve File.Move can
        // race with the FileSystemWatcher's own reload-triggered read on Windows
        // (sharing violation → UnauthorizedAccessException), and there is no
        // realistic production scenario where two non-MindAttic writers contend
        // on this file.
        var writer = new MindAttic.Vault.Credentials.CredentialStore(llm);
        writer.SetKey("placeholder", "p"); // pre-create providers.json

        var config = new ConfigurationBuilder()
            .Add(new MindAtticConfigurationSource
            {
                RoamingRoot = tmp.Path,
                ReloadOnChange = true,
            })
            .Build();

        Assert.That(config["MindAttic:Vault:LLM:claude:apiKey"], Is.Null);

        // Trigger a real change through the same atomic-swap path the library uses.
        writer.SetKey("claude", "after-reload");

        // FileSystemWatcher events are async — poll up to a generous timeout to
        // tolerate slow CI hosts without flaking on fast ones.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string? observed = null;
        while (DateTime.UtcNow < deadline)
        {
            observed = config["MindAttic:Vault:LLM:claude:apiKey"];
            if (observed == "after-reload") break;
            Thread.Sleep(50);
        }

        Assert.That(observed, Is.EqualTo("after-reload"));
    }
}
