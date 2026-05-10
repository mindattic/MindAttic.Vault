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
}
