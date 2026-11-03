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
    [TestCase("claude-api", "claude")]
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
        Assert.That(ProviderCatalog.IdsFor("anthropic"), Is.EqualTo(new[] { "claude", "anthropic", "claude-api" }));

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
        Assert.That(ProviderCatalog.Get("elevenlabs").Bucket, Is.EqualTo(LlmCredentialStore.Bucket), "every non-broker API key lives in the LLM bucket, where they are today");
        Assert.That(ProviderCatalog.Get("jellyfin").Category, Is.EqualTo(ProviderCategory.Media));
    }

    // M.VALT-T70: every provider an app declares with [UsesCredential] is in the catalog, so Desktop shows its name and key page.
    [TestCase("stability", "Stability AI")]
    [TestCase("kling", "Kling AI")]
    [TestCase("runway", "Runway")]
    [TestCase("usps", "USPS")]
    public void ProvidersAppsDeclareAreKnown(string id, string displayName)
    {
        Assert.That(ProviderCatalog.IsKnown(id), Is.True);
        var p = ProviderCatalog.Get(id);
        Assert.That(p.DisplayName, Is.EqualTo(displayName));
        Assert.That(p.Bucket, Is.EqualTo(LlmCredentialStore.Bucket), "non-broker keys live in the LLM bucket");
    }

    [Test]
    public void LegacyClaudeApiEntry_MergesIntoClaudesNamedList()
    {
        using var tmp = new TempDirectory();
        var llm = new LlmCredentialStore(System.IO.Path.Combine(tmp.Path, LlmCredentialStore.Bucket));
        llm.SetKey("claude", "sk-ant-one");
        llm.SetKey("claude-api", "sk-ant-one");
        var store = new NamedKeyStore(tmp.Path, new KeyAssignmentStore(System.IO.Path.Combine(tmp.Path, KeyAssignmentStore.Bucket)));

        Assert.That(store.GetKeys("claude").Select(k => k.Value), Is.EqualTo(new[] { "sk-ant-one" }), "the duplicate under claude-api is the same key");
        Assert.That(store.ProvidersWithKeys(), Does.Not.Contain("claude-api"), "no separate claude-api provider on the Keys tab");
    }

    [Test]
    public void Normalize_Blank_Throws() =>
        Assert.Throws<ArgumentException>(() => ProviderCatalog.Normalize(" "));

    [TestCase("alpaca-paper")]
    [TestCase("Alpaca-Live")]
    public void UnknownId_InAKnownFamily_TakesThatFamilysBucket(string id)
    {
        var p = ProviderCatalog.Get(id);
        Assert.That(p.Id, Is.EqualTo(id.ToLowerInvariant()));
        Assert.That(p.Bucket, Is.EqualTo(BrokerCredentialStore.Bucket));
        Assert.That(p.Category, Is.EqualTo(ProviderCategory.Broker));
        Assert.That(ProviderCatalog.IsKnown(id), Is.False);
    }

    [Test]
    public void FamilyPrefix_NeedsTheDashAndASuffix()
    {
        Assert.That(ProviderCatalog.Get("alpacax").Bucket, Is.EqualTo(LlmCredentialStore.Bucket));
        Assert.That(ProviderCatalog.Get("alpaca").Id, Is.EqualTo("alpaca"));
    }
}
