using MindAttic.Vault.Configuration;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

/// <summary>
/// Locks down the public string constants and path builders in
/// <see cref="VaultConfigurationKeys"/>. Every other MindAttic project pins to
/// these values; renaming them is a breaking change.
/// </summary>
[TestFixture]
public class VaultConfigurationKeysTests
{
    [Test]
    public void Section_Constants_Are_Stable()
    {
        Assert.That(VaultConfigurationKeys.RootSection,    Is.EqualTo("MindAttic"));
        Assert.That(VaultConfigurationKeys.VaultSection,   Is.EqualTo("MindAttic:Vault"));
        Assert.That(VaultConfigurationKeys.LlmSection,     Is.EqualTo("MindAttic:Vault:LLM"));
        Assert.That(VaultConfigurationKeys.BrokersSection, Is.EqualTo("MindAttic:Vault:Brokers"));
        Assert.That(VaultConfigurationKeys.TokensSection,  Is.EqualTo("MindAttic:Vault:Tokens"));
    }

    [Test]
    public void ApiKeyProperty_Is_Stable()
    {
        Assert.That(VaultConfigurationKeys.ApiKeyProperty, Is.EqualTo("apiKey"));
    }

    [Test]
    public void SharedUserSecretsId_Is_Stable()
    {
        // Pinned in every consumer's .csproj — must not change without coordinating.
        Assert.That(VaultConfigurationKeys.SharedUserSecretsId, Is.EqualTo("mindattic-vault-shared"));
    }

    [Test]
    public void ProviderSection_Builds_Colon_Delimited_Path()
    {
        var path = VaultConfigurationKeys.ProviderSection(VaultConfigurationKeys.LlmSection, "claude");
        Assert.That(path, Is.EqualTo("MindAttic:Vault:LLM:claude"));
    }

    [Test]
    public void ProviderSection_Throws_For_Empty_BucketSection()
    {
        Assert.Throws<ArgumentException>(() => VaultConfigurationKeys.ProviderSection("",    "claude"));
        Assert.Throws<ArgumentException>(() => VaultConfigurationKeys.ProviderSection("   ", "claude"));
        Assert.Throws<ArgumentException>(() => VaultConfigurationKeys.ProviderSection(null!, "claude"));
    }

    [Test]
    public void ProviderSection_Throws_For_Empty_ProviderId()
    {
        Assert.Throws<ArgumentException>(() => VaultConfigurationKeys.ProviderSection(VaultConfigurationKeys.LlmSection, ""));
        Assert.Throws<ArgumentException>(() => VaultConfigurationKeys.ProviderSection(VaultConfigurationKeys.LlmSection, "   "));
        Assert.Throws<ArgumentException>(() => VaultConfigurationKeys.ProviderSection(VaultConfigurationKeys.LlmSection, null!));
    }

    [Test]
    public void ProviderApiKeyPath_Combines_Section_And_ApiKey()
    {
        Assert.That(
            VaultConfigurationKeys.ProviderApiKeyPath(VaultConfigurationKeys.LlmSection, "claude"),
            Is.EqualTo("MindAttic:Vault:LLM:claude:apiKey"));

        Assert.That(
            VaultConfigurationKeys.ProviderApiKeyPath(VaultConfigurationKeys.BrokersSection, "alpaca-paper"),
            Is.EqualTo("MindAttic:Vault:Brokers:alpaca-paper:apiKey"));
    }

    [Test]
    public void ProviderApiKeyPath_Throws_For_Empty_Args()
    {
        Assert.Throws<ArgumentException>(() => VaultConfigurationKeys.ProviderApiKeyPath("", "claude"));
        Assert.Throws<ArgumentException>(() => VaultConfigurationKeys.ProviderApiKeyPath(VaultConfigurationKeys.LlmSection, ""));
    }
}
