using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.DependencyInjection;
using MindAttic.Vault.Resolution;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

/// <summary>
/// End-to-end tests that exercise the full cloud-native flow without mocks:
/// build a real <see cref="IConfiguration"/> with multiple sources (in-memory
/// stand-in for User Secrets, env vars, the on-disk %APPDATA% file source),
/// register Vault via DI, and assert the composed store resolves credentials
/// in the order documented in the README.
///
/// <para>Vault is a class library with no UI — Cypress does not apply. These
/// integration tests are the equivalent end-to-end coverage.</para>
/// </summary>
[TestFixture]
public class CloudNativeIntegrationTests
{
    private string? originalRoamingRoot;
    private string? originalLlmEnv;
    private string? originalBrokerEnv;

    [SetUp]
    public void Setup()
    {
        originalRoamingRoot = Environment.GetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.RoamingRootEnvVar);
        originalLlmEnv      = Environment.GetEnvironmentVariable(LlmCredentialStore.DirectoryEnvVar);
        originalBrokerEnv   = Environment.GetEnvironmentVariable(BrokerCredentialStore.DirectoryEnvVar);
    }

    [TearDown]
    public void Teardown()
    {
        Environment.SetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.RoamingRootEnvVar, originalRoamingRoot);
        Environment.SetEnvironmentVariable(LlmCredentialStore.DirectoryEnvVar,                 originalLlmEnv);
        Environment.SetEnvironmentVariable(BrokerCredentialStore.DirectoryEnvVar,              originalBrokerEnv);
    }

    [Test]
    public void Configuration_Wins_Over_Existing_AppData_File()
    {
        using var tmp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "LLM"));
        File.WriteAllText(Path.Combine(tmp.Path, "LLM", "providers.json"),
            """ { "claude": { "apiKey": "from-disk-legacy" } } """);

        // Stand in for User Secrets / App Service Application Settings.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MindAttic:Vault:LLM:claude:apiKey"] = "sk-ant-from-config"
            })
            .Build();

        // Point the LLM file store at our temp dir.
        Environment.SetEnvironmentVariable(LlmCredentialStore.DirectoryEnvVar, Path.Combine(tmp.Path, "LLM"));

        var composite = new CompositeCredentialStore(
            ConfigurationCredentialStore.ForLlm(config),
            new LlmCredentialStore(Path.Combine(tmp.Path, "LLM")));

        Assert.That(composite.GetKey("claude"), Is.EqualTo("sk-ant-from-config"));
    }

    [Test]
    public void File_Source_Surfaces_Through_IConfiguration_When_Registered()
    {
        using var tmp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "LLM"));
        File.WriteAllText(Path.Combine(tmp.Path, "LLM", "providers.json"),
            """ { "claude": { "apiKey": "from-disk" } } """);

        // Use the IConfigurationBuilder extension exactly the way Program.cs would.
        var config = new ConfigurationBuilder()
            .Add(new MindAtticConfigurationSource { RoamingRoot = tmp.Path })
            .Build();

        var store = ConfigurationCredentialStore.ForLlm(config);
        Assert.That(store.GetKey("claude"), Is.EqualTo("from-disk"));
    }

    [Test]
    public void Env_Vars_Override_File_Through_Standard_Configuration_Provider_Order()
    {
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

            var store = ConfigurationCredentialStore.ForLlm(config);
            Assert.That(store.GetKey("claude"), Is.EqualTo("from-env"));
        }
        finally { Environment.SetEnvironmentVariable(envVar, original); }
    }

    [Test]
    public void AddMindAtticVault_With_Configuration_Resolves_Both_Resolvers()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MindAttic:Vault:LLM:claude:apiKey"]            = "C",
                ["MindAttic:Vault:Brokers:alpaca-paper:apiKey"]  = "B",
            })
            .Build();

        var sp = new ServiceCollection().AddMindAtticVault(config).BuildServiceProvider();

        Assert.That(sp.GetRequiredService<LlmCredentialResolver>().GetKey("claude"),       Is.EqualTo("C"));
        Assert.That(sp.GetRequiredService<BrokerCredentialResolver>().GetKey("alpaca-paper"), Is.EqualTo("B"));
        Assert.That(sp.GetRequiredService<ICredentialStore>().GetKey("claude"),             Is.EqualTo("C"));
        Assert.That(sp.GetRequiredService<LlmCredentialStore>(),                            Is.Not.Null);
        Assert.That(sp.GetRequiredService<BrokerCredentialStore>(),                         Is.Not.Null);
    }

    [Test]
    public void KeyResolver_FromConfiguration_Step_Composes_With_Env_And_File_Steps()
    {
        using var tmp = new TempDirectory();
        var file = new CredentialStore(tmp.Path);
        file.SetKey("claude", "from-file");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MindAttic:Vault:LLM:claude:apiKey"] = "from-config"
            })
            .Build();

        var resolver = KeyResolver
            .From(KeyResolver.FromConfiguration(config, VaultConfigurationKeys.LlmSection))
            .Then(KeyResolver.FromStore(file));

        Assert.That(resolver.Resolve("claude"), Is.EqualTo("from-config"));
    }

    [Test]
    public void Standard_Schema_Constants_Match_Documented_Paths()
    {
        Assert.That(VaultConfigurationKeys.RootSection,    Is.EqualTo("MindAttic"));
        Assert.That(VaultConfigurationKeys.VaultSection,   Is.EqualTo("MindAttic:Vault"));
        Assert.That(VaultConfigurationKeys.LlmSection,     Is.EqualTo("MindAttic:Vault:LLM"));
        Assert.That(VaultConfigurationKeys.BrokersSection, Is.EqualTo("MindAttic:Vault:Brokers"));
        Assert.That(VaultConfigurationKeys.TokensSection,  Is.EqualTo("MindAttic:Vault:Tokens"));
        Assert.That(VaultConfigurationKeys.ProviderApiKeyPath(VaultConfigurationKeys.LlmSection, "claude"),
            Is.EqualTo("MindAttic:Vault:LLM:claude:apiKey"));
    }

    [Test]
    public void Shared_UserSecretsId_Constant_Stays_Stable()
    {
        // Every project's .csproj should pin this exact value to share dev secrets family-wide.
        Assert.That(VaultConfigurationKeys.SharedUserSecretsId, Is.EqualTo("mindattic-vault-shared"));
    }

    [Test]
    public void Json_Source_Mirrors_Standard_Schema()
    {
        // Smoke test: an appsettings.json snippet that follows the documented
        // schema must resolve through ConfigurationCredentialStore unchanged.
        using var tmp = new TempDirectory();
        var path = Path.Combine(tmp.Path, "appsettings.json");
        File.WriteAllText(path, """
        {
          "MindAttic": {
            "Vault": {
              "LLM": {
                "claude": { "type": "anthropic", "apiKey": "sk-from-json", "model": "claude-sonnet-4-6", "maxTokens": 8192 }
              },
              "Brokers": {
                "alpaca-paper": { "type": "alpaca", "apiKey": "PK", "secret": "S", "baseUrl": "https://paper-api.alpaca.markets" }
              }
            }
          }
        }
        """);

        var config = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();

        Assert.That(ConfigurationCredentialStore.ForLlm(config).GetKey("claude"),         Is.EqualTo("sk-from-json"));
        Assert.That(ConfigurationCredentialStore.ForBrokers(config).GetKey("alpaca-paper"), Is.EqualTo("PK"));
    }

    [Test]
    public void Test_Settings_Path_Stays_In_Roaming_AppData()
    {
        // Verifies the user's stated rule: secrets move into IConfiguration,
        // settings stay in %APPDATA%.
        using var tmp = new TempDirectory();
        Environment.SetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.RoamingRootEnvVar, tmp.Path);

        var store = MindAttic.Vault.Settings.JsonSettingsStore<TestSettings>.ForApp("Sample");
        Assert.That(store.Directory, Is.EqualTo(Path.Combine(tmp.Path, "Sample")));
    }

    public class TestSettings { public string Theme { get; set; } = "default"; }
}
