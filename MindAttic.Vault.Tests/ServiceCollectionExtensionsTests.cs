using Microsoft.Extensions.DependencyInjection;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.DependencyInjection;
using MindAttic.Vault.Settings;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public class ServiceCollectionExtensionsTests
{
    public sealed class AppConfig { public string Theme { get; set; } = "alpaca"; }

    [Test]
    public void AddMindAtticVault_Resolves_Llm_And_Broker_Stores()
    {
        var sp = new ServiceCollection().AddMindAtticVault().BuildServiceProvider();

        Assert.That(sp.GetService<LlmCredentialStore>(),    Is.Not.Null);
        Assert.That(sp.GetService<BrokerCredentialStore>(), Is.Not.Null);
        Assert.That(sp.GetService<ICredentialStore>(),      Is.Not.Null);
    }

    [Test]
    public void AddMindAtticVault_Resolves_Default_ICredentialStore_To_Llm_Store()
    {
        var sp = new ServiceCollection().AddMindAtticVault().BuildServiceProvider();

        var generic = sp.GetRequiredService<ICredentialStore>();
        var llm     = sp.GetRequiredService<LlmCredentialStore>();
        Assert.That(generic, Is.SameAs(llm));
    }

    [Test]
    public void AddVaultAppSettings_Registers_JsonSettingsStore()
    {
        var sp = new ServiceCollection()
            .AddVaultAppSettings<AppConfig>("Sample.Test.App")
            .BuildServiceProvider();

        var store = sp.GetService<JsonSettingsStore<AppConfig>>();
        Assert.That(store, Is.Not.Null);
        Assert.That(store!.Directory, Does.EndWith("Sample.Test.App"));
    }

    // ── Argument validation ─────────────────────────────────────────────────────

    [Test]
    public void AddMindAtticVault_Without_Configuration_Throws_For_Null_Services()
    {
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddMindAtticVault());
    }

    [Test]
    public void AddMindAtticVault_With_Configuration_Throws_For_Null_Services()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddMindAtticVault(config));
    }

    [Test]
    public void AddMindAtticVault_With_Configuration_Throws_For_Null_Configuration()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ServiceCollection().AddMindAtticVault(null!));
    }

    [Test]
    public void AddVaultAppSettings_Throws_For_Null_Services()
    {
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddVaultAppSettings<AppConfig>("App"));
    }

    [Test]
    public void AddVaultAppSettings_Throws_For_Empty_App()
    {
        Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddVaultAppSettings<AppConfig>(""));
        Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddVaultAppSettings<AppConfig>("   "));
    }

    [Test]
    public void AddVaultAppSettings_Honours_Custom_File_Name()
    {
        var sp = new ServiceCollection()
            .AddVaultAppSettings<AppConfig>("Sample.App", "custom-config.json")
            .BuildServiceProvider();

        var store = sp.GetRequiredService<JsonSettingsStore<AppConfig>>();
        Assert.That(store.FileName, Is.EqualTo("custom-config.json"));
    }

    // ── Fluent return ───────────────────────────────────────────────────────────

    [Test]
    public void AddMindAtticVault_Returns_Same_Services_For_Chaining()
    {
        var services = new ServiceCollection();
        Assert.That(services.AddMindAtticVault(), Is.SameAs(services));
    }

    [Test]
    public void AddMindAtticVault_With_Configuration_Returns_Same_Services_For_Chaining()
    {
        var services = new ServiceCollection();
        var config   = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        Assert.That(services.AddMindAtticVault(config), Is.SameAs(services));
    }

    [Test]
    public void AddVaultAppSettings_Returns_Same_Services_For_Chaining()
    {
        var services = new ServiceCollection();
        Assert.That(services.AddVaultAppSettings<AppConfig>("App"), Is.SameAs(services));
    }
}
