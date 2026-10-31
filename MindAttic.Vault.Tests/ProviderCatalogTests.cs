using MindAttic.Vault.Credentials;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public sealed class ProviderCatalogTests
{
    [TestCase("anthropic", "claude")]
    [TestCase("Anthropic", "claude")]
    [TestCase("google", "gemini")]
    [TestCase("xai", "grok")]
    [TestCase("moonshot", "kimi")]
    [TestCase(" claude ", "claude")]
    public void Normalize_ResolvesAliases(string input, string expected) =>
        Assert.That(ProviderCatalog.Normalize(input), Is.EqualTo(expected));

    [Test]
    public void UnknownId_PassesThroughAsItsOwnLlmProvider()
    {
        var p = ProviderCatalog.Get("MyLocalModel");
        Assert.That(p.Id, Is.EqualTo("mylocalmodel"));
        Assert.That(p.Bucket, Is.EqualTo(LlmCredentialStore.Bucket));
        Assert.That(ProviderCatalog.IsKnown("MyLocalModel"), Is.False);
    }

    [Test]
    public void IdsFor_ListsCanonicalThenAliases() =>
        Assert.That(ProviderCatalog.IdsFor("anthropic"), Is.EqualTo(new[] { "claude", "anthropic" }));

    [Test]
    public void EveryIdAndAliasIsUnique()
    {
        var all = ProviderCatalog.All.SelectMany(p => new[] { p.Id }.Concat(p.Aliases)).ToList();
        Assert.That(all.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(all.Count));
    }

    [Test]
    public void NonLlmProvidersUseTheirOwnBuckets()
    {
        Assert.That(ProviderCatalog.Get("alpaca").Bucket, Is.EqualTo(BrokerCredentialStore.Bucket));
        Assert.That(ProviderCatalog.Get("elevenlabs").Bucket, Is.EqualTo(ProviderCatalog.ServicesBucket));
        Assert.That(ProviderCatalog.Get("jellyfin").Category, Is.EqualTo(ProviderCategory.Media));
    }

    [Test]
    public void Normalize_Blank_Throws() =>
        Assert.Throws<ArgumentException>(() => ProviderCatalog.Normalize(" "));
}
