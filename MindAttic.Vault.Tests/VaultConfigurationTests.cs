using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Settings;
using MindAttic.Vault.Settings.Manifest;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public sealed class VaultConfigurationTests
{
    private TempDirectory tmp = null!;

    [SetUp] public void SetUp() => tmp = new TempDirectory("vault-config");
    [TearDown] public void TearDown() => tmp.Dispose();

    private IConfigurationRoot Build(bool reload = false, bool keys = true) =>
        new ConfigurationBuilder()
            .AddMindAtticVaultConfiguration(s => { s.RoamingRoot = tmp.Path; s.ReloadOnChange = reload; s.IncludeKeys = keys; })
            .Build();

    private NamedKeyStore Keys() => new(tmp.Path, new KeyAssignmentStore(tmp.Combine("Vault")));

    [Test]
    public void EveryAppSettingsFileAndGlobalAppearUnderMindAtticSettings()
    {
        new JsonSettingsStore<SampleSettings>(tmp.Combine("SampleApp")).LoadOrSeed();
        new JsonSettingsStore<MindAtticGlobalSettings>(tmp.Combine("Global")).Save(new MindAtticGlobalSettings { Theme = "Light" });
        var config = Build();
        Assert.That(config["MindAttic:Settings:SampleApp:maxLossPerTrade"], Is.EqualTo("50"));
        Assert.That(config["MindAttic:Settings:SampleApp:server:port"], Is.EqualTo("8080"));
        Assert.That(config["MindAttic:Settings:Global:theme"], Is.EqualTo("Light"));
    }

    [Test]
    public void EveryManifestFieldNameResolvesInTheTree()
    {
        new JsonSettingsStore<SampleSettings>(tmp.Combine("SampleApp")).LoadOrSeed();
        var config = Build();
        var manifest = SettingsManifestBuilder.Build<SampleSettings>();
        foreach (var f in manifest.Fields.Where(f => f.Kind is not (SettingKind.Object or SettingKind.Collection or SettingKind.Dictionary or SettingKind.StringList)))
            Assert.That(config.GetSection(f.Name).Value, Is.Not.Null, f.Name);
    }

    [Test]
    public void PartialReadBindsOneApp()
    {
        new JsonSettingsStore<SampleSettings>(tmp.Combine("SampleApp")).Save(new SampleSettings { RetryCount = 9 });
        var bound = Build().GetSection(LampNames.AppSettings("SampleApp")).Get<SampleSettings>()!;
        Assert.That(bound.RetryCount, Is.EqualTo(9));
        Assert.That(bound.Server.Port, Is.EqualTo(8080));
    }

    [Test]
    public void CorruptAppFile_DoesNotHideOtherApps()
    {
        Directory.CreateDirectory(tmp.Combine("Broken"));
        File.WriteAllText(tmp.Combine("Broken", "settings.json"), "{ nope");
        new JsonSettingsStore<MindAtticGlobalSettings>(tmp.Combine("Global")).LoadOrSeed();
        Assert.That(Build()["MindAttic:Settings:Global:theme"], Is.EqualTo("Dark"));
    }

    [Test]
    public void NamedKeysOrderDisabledAndAssignmentsProject()
    {
        var keys = Keys();
        keys.Add("anthropic", "2026-10-31 IdiotProof", "sk-a");
        keys.Add("claude", "key1", "sk-b");
        keys.SetDisabled("claude", "key1", true);
        keys.Assignments.Set("IdiotProof", "claude", new[] { "2026-10-31 IdiotProof", "key1" });
        var config = Build();
        Assert.That(config["MindAttic:Vault:LLM:claude:keys:2026-10-31 IdiotProof"], Is.EqualTo("sk-a"));
        Assert.That(config["MindAttic:Vault:LLM:claude:keyOrder:1"], Is.EqualTo("key1"));
        Assert.That(config["MindAttic:Vault:LLM:claude:disabledKeys:0"], Is.EqualTo("key1"));
        Assert.That(config["MindAttic:Vault:Assignments:IdiotProof:claude:0"], Is.EqualTo("2026-10-31 IdiotProof"));
        Assert.That(LampNames.Key("anthropic", "x"), Is.EqualTo("MindAttic:Vault:LLM:claude:keys:x"));
    }

    [Test]
    public void IncludeKeysOff_LeavesSecretsOut()
    {
        Keys().Add("claude", "k", "sk-secret");
        var config = Build(keys: false);
        Assert.That(config.AsEnumerable().Any(kv => kv.Value == "sk-secret"), Is.False);
    }

    [Test]
    public void ServiceKeysUseTheirBucketSection()
    {
        Keys().Add("elevenlabs", "home", "el");
        Assert.That(Build()["MindAttic:Vault:LLM:elevenlabs:keys:home"], Is.EqualTo("el"));
    }

    [Test]
    public async Task ReloadOnChange_PicksUpSavedSettings()
    {
        var store = new JsonSettingsStore<SampleSettings>(tmp.Combine("SampleApp"));
        store.Save(new SampleSettings { RetryCount = 1 });
        var config = Build(reload: true);
        Assert.That(config["MindAttic:Settings:SampleApp:retryCount"], Is.EqualTo("1"));
        store.Save(new SampleSettings { RetryCount = 2 });
        for (var i = 0; i < 50 && config["MindAttic:Settings:SampleApp:retryCount"] != "2"; i++) await Task.Delay(100);
        Assert.That(config["MindAttic:Settings:SampleApp:retryCount"], Is.EqualTo("2"));
        (config as IDisposable)?.Dispose();
    }

    [Test]
    public void MissingRoot_YieldsEmptyTree() =>
        Assert.That(new ConfigurationBuilder().AddMindAtticVaultConfiguration(s => s.RoamingRoot = tmp.Combine("nope")).Build().AsEnumerable(), Is.Empty);
}

[TestFixture]
public sealed class ConfigurationKeySourceTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Test]
    public void NamedKeys_FollowKeyOrderAndDisabled()
    {
        var source = new ConfigurationKeySource(Config(new()
        {
            ["MindAttic:Vault:LLM:claude:keys:b"] = "sk-b",
            ["MindAttic:Vault:LLM:claude:keys:a"] = "sk-a",
            ["MindAttic:Vault:LLM:claude:keys:c"] = "sk-c",
            ["MindAttic:Vault:LLM:claude:keyOrder:0"] = "b",
            ["MindAttic:Vault:LLM:claude:keyOrder:1"] = "a",
            ["MindAttic:Vault:LLM:claude:disabledKeys:0"] = "a",
        }));
        var keys = source.GetKeys("anthropic");
        Assert.That(keys.Select(k => (k.Name, k.Value, k.Disabled)), Is.EqualTo(new[] { ("b", "sk-b", false), ("a", "sk-a", true), ("c", "sk-c", false) }));
    }

    [Test]
    public void OlderShapes_StillLoad()
    {
        var source = new ConfigurationKeySource(Config(new()
        {
            ["MindAttic:Vault:LLM:openai:apiKey"] = "sk-o",
            ["MindAttic:Vault:LLM:gemini:apiKeys:0:key"] = "g1",
            ["MindAttic:Vault:LLM:gemini:apiKeys:1:key"] = "g2",
            ["MindAttic:Vault:LLM:gemini:apiKeys:1:label"] = "Team",
            ["MindAttic:Vault:LLM:google:apiKey"] = "g3",
        }));
        Assert.That(source.GetKeys("openai").Single().Name, Is.EqualTo("key1"));
        Assert.That(source.GetKeys("gemini").Select(k => k.Name), Is.EqualTo(new[] { "key1", "Team", "key1 (2)" }));
    }

    [Test]
    public void Assignment_ReadsInIndexOrder()
    {
        var source = new ConfigurationKeySource(Config(new()
        {
            ["MindAttic:Vault:Assignments:IdiotProof:claude:1"] = "key1",
            ["MindAttic:Vault:Assignments:IdiotProof:claude:0"] = "2026-10-31 IdiotProof",
        }));
        Assert.That(source.GetAssignment("IdiotProof", "anthropic"), Is.EqualTo(new[] { "2026-10-31 IdiotProof", "key1" }));
        Assert.That(source.GetAssignment("Tutor", "claude"), Is.Empty);
    }

    [Test]
    public async Task KeySelectorOverConfiguration_FailsOverByAssignment()
    {
        using var tmp = new TempDirectory("cfg-selector");
        var config = Config(new()
        {
            ["MindAttic:Vault:LLM:claude:keys:bad"] = "sk-bad",
            ["MindAttic:Vault:LLM:claude:keys:good"] = "sk-good",
            ["MindAttic:Vault:Assignments:IdiotProof:claude:0"] = "bad",
            ["MindAttic:Vault:Assignments:IdiotProof:claude:1"] = "good",
        });
        var selector = new KeySelector(new ConfigurationKeySource(config), new KeyHealthStore(tmp.Path));
        var used = await selector.ExecuteAsync<string>("claude", "IdiotProof", (key, _) =>
            key == "sk-bad" ? throw new HttpRequestException("x", null, System.Net.HttpStatusCode.Unauthorized) : Task.FromResult(key));
        Assert.That(used, Is.EqualTo("sk-good"));
    }

    [Test]
    public void ConfigurationCredentialStore_SingleKeyApiReadsNamedKeys()
    {
        var store = ConfigurationCredentialStore.ForLlm(Config(new()
        {
            ["MindAttic:Vault:LLM:claude:keys:x"] = "sk-x",
            ["MindAttic:Vault:LLM:claude:keys:y"] = "sk-y",
            ["MindAttic:Vault:LLM:claude:keyOrder:0"] = "y",
            ["MindAttic:Vault:LLM:claude:keyOrder:1"] = "x",
            ["MindAttic:Vault:LLM:claude:disabledKeys:0"] = "y",
        }));
        Assert.That(store.GetKey("claude"), Is.EqualTo("sk-x"));
        Assert.That(store.ListProviders(), Does.Contain("claude"));
    }

    [Test]
    public void ConfigurationCredentialStore_ApiKeyStillWins()
    {
        var store = ConfigurationCredentialStore.ForLlm(Config(new()
        {
            ["MindAttic:Vault:LLM:claude:apiKey"] = "sk-plain",
            ["MindAttic:Vault:LLM:claude:keys:x"] = "sk-x",
        }));
        Assert.That(store.GetKey("claude"), Is.EqualTo("sk-plain"));
    }
}
