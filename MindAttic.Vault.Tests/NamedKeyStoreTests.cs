using System.Text.Json.Nodes;
using MindAttic.Vault.Credentials;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public sealed class NamedKeyStoreTests
{
    private TempDirectory tmp = null!;
    private NamedKeyStore store = null!;
    private string ProvidersJson => tmp.Combine("LLM", "providers.json");

    [SetUp]
    public void SetUp()
    {
        tmp = new TempDirectory("named-keys");
        store = new NamedKeyStore(tmp.Path, new KeyAssignmentStore(tmp.Combine("Vault")));
    }

    [TearDown] public void TearDown() => tmp.Dispose();

    private void WriteProviders(string json)
    {
        Directory.CreateDirectory(tmp.Combine("LLM"));
        File.WriteAllText(ProvidersJson, json);
    }

    [Test]
    public void OldSingleKeyFile_LoadsAsKey1()
    {
        WriteProviders("""{ "claude": { "type": "anthropic", "apiKey": "sk-1" } }""");
        var keys = store.GetKeys("claude");
        Assert.That(keys.Select(k => (k.Name, k.Value)), Is.EqualTo(new[] { ("key1", "sk-1") }));
    }

    [Test]
    public void OldApiKeysArray_LabelsBecomeNamesAndUnlabeledAreNumbered()
    {
        WriteProviders("""{ "claude": { "apiKey": "a", "apiKeys": [ { "key": "a" }, { "key": "b", "label": "Team" }, { "key": "c" } ] } }""");
        Assert.That(store.GetKeys("claude").Select(k => k.Name), Is.EqualTo(new[] { "key1", "Team", "key2" }));
    }

    [Test]
    public void AliasEntriesMergeIntoOneListWithoutDuplicates()
    {
        WriteProviders("""{ "claude": { "apiKey": "a" }, "anthropic": { "apiKey": "b" }, "Anthropic2": { "apiKey": "z" } }""");
        Assert.That(store.GetKeys("anthropic").Select(k => k.Value), Is.EqualTo(new[] { "a", "b" }));
        WriteProviders("""{ "claude": { "apiKey": "a" }, "anthropic": { "apiKey": "a" } }""");
        Assert.That(store.GetKeys("claude").Count, Is.EqualTo(1));
    }

    [Test]
    public void Add_PersistsNameIntoTheIdAlreadyOnDisk()
    {
        WriteProviders("""{ "anthropic": { "apiKey": "old" } }""");
        store.Add("claude", "2026-10-31 IdiotProof", "new");
        var root = JsonNode.Parse(File.ReadAllText(ProvidersJson))!.AsObject();
        Assert.That(root.ContainsKey("claude"), Is.False);
        Assert.That(store.GetKeys("claude").Select(k => k.Name), Is.EqualTo(new[] { "key1", "2026-10-31 IdiotProof" }));
        Assert.That(root["anthropic"]!["apiKey"]!.GetValue<string>(), Is.EqualTo("old"), "first key stays the single-key mirror");
    }

    [Test]
    public void Add_ForANewProvider_UsesTheCanonicalId()
    {
        store.Add("anthropic", "first", "v");
        Assert.That(JsonNode.Parse(File.ReadAllText(ProvidersJson))!.AsObject().ContainsKey("claude"), Is.True);
    }

    [Test]
    public void SingleUnnamedKey_KeepsTheOldFileShape()
    {
        new LlmCredentialStore(tmp.Combine("LLM")).SetKey("claude", "only");
        Assert.That(File.ReadAllText(ProvidersJson), Does.Not.Contain("apiKeys"));
        Assert.That(new LlmCredentialStore(tmp.Combine("LLM")).GetKey("claude"), Is.EqualTo("only"));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("has:colon")]
    public void InvalidNames_AreRejected(string name) =>
        Assert.Throws<ArgumentException>(() => store.Add("claude", name, "v"));

    [Test]
    public void NamesAreTrimmedAndUniqueCaseInsensitively()
    {
        store.Add("claude", "  Main  ", "v1");
        Assert.That(store.GetKeys("claude")[0].Name, Is.EqualTo("Main"));
        Assert.Throws<ArgumentException>(() => store.Add("claude", "main", "v2"));
    }

    [Test]
    public void TooLongName_IsRejected() =>
        Assert.Throws<ArgumentException>(() => store.Add("claude", new string('x', NamedKeyStore.MaxNameLength + 1), "v"));

    [Test]
    public void Rename_MovesEveryAppAssignmentAndRaisesEvent()
    {
        store.Add("claude", "old", "v");
        store.Assignments.Set("IdiotProof", "claude", new[] { "old" });
        (string, string, string)? raised = null;
        store.KeyRenamed += (p, o, n) => raised = (p, o, n);
        store.Rename("anthropic", "OLD", "new");
        Assert.That(store.GetKeys("claude").Single().Name, Is.EqualTo("new"));
        Assert.That(store.Assignments.Get("idiotproof", "claude"), Is.EqualTo(new[] { "new" }));
        Assert.That(raised, Is.EqualTo(("claude", "old", "new")));
    }

    [Test]
    public void Rename_ToAnExistingName_IsRejected()
    {
        store.Add("claude", "a", "1");
        store.Add("claude", "b", "2");
        Assert.Throws<ArgumentException>(() => store.Rename("claude", "a", "B"));
    }

    [Test]
    public void Remove_IsBlockedWhileAssigned_ThenAllowed()
    {
        store.Add("claude", "k", "v");
        store.Assignments.Set("IdiotProof", "claude", new[] { "k" });
        store.Assignments.Set("Tutor", "claude", new[] { "k" });
        var ex = Assert.Throws<KeyInUseException>(() => store.Remove("claude", "k"))!;
        Assert.That(ex.Apps, Is.EqualTo(new[] { "IdiotProof", "Tutor" }));
        Assert.That(ex.Message, Does.Not.Contain("v\""));
        store.Assignments.Set("IdiotProof", "claude", Array.Empty<string>());
        store.Assignments.Set("Tutor", "claude", Array.Empty<string>());
        store.Remove("claude", "k");
        Assert.That(store.GetKeys("claude"), Is.Empty);
    }

    [Test]
    public void Disabled_RoundTripsAndOldGetKeySkipsIt()
    {
        store.Add("claude", "first", "v1");
        store.Add("claude", "second", "v2");
        store.SetDisabled("claude", "first", true);
        Assert.That(store.GetKeys("claude")[0].Disabled, Is.True);
        Assert.That(new LlmCredentialStore(tmp.Combine("LLM")).GetKey("claude"), Is.EqualTo("v2"));
        store.SetDisabled("claude", "first", false);
        Assert.That(new LlmCredentialStore(tmp.Combine("LLM")).GetKey("claude"), Is.EqualTo("v1"));
    }

    [Test]
    public void SetValue_KeepsNameAndPosition()
    {
        store.Add("claude", "a", "1");
        store.Add("claude", "b", "2");
        store.SetValue("claude", "a", "1b");
        Assert.That(store.GetKeys("claude").Select(k => (k.Name, k.Value)), Is.EqualTo(new[] { ("a", "1b"), ("b", "2") }));
    }

    [Test]
    public void Reorder_SetsFailoverOrderAndRequiresEveryName()
    {
        store.Add("claude", "a", "1");
        store.Add("claude", "b", "2");
        store.Reorder("claude", new[] { "b", "a" });
        Assert.That(store.GetKeys("claude").Select(k => k.Name), Is.EqualTo(new[] { "b", "a" }));
        Assert.Throws<ArgumentException>(() => store.Reorder("claude", new[] { "a" }));
    }

    [Test]
    public void ServiceKeysShareTheLlmBucketWhereTheyAreToday()
    {
        WriteProviders("""{ "elevenlabs": { "type": "bearer", "apiKey": "el-1" } }""");
        Assert.That(store.GetKeys("elevenlabs").Single().Value, Is.EqualTo("el-1"));
    }

    [Test]
    public void AliasEntriesOnDisk_AreKeptAndMirroredSoOldReadersSeeTheSameKeys()
    {
        WriteProviders("""{ "xai": { "type": "bearer", "apiKey": "x-1" } }""");
        store.Add("grok", "second", "x-2");
        store.SetDisabled("grok", "key1", true);
        var llm = new LlmCredentialStore(tmp.Combine("LLM"));
        Assert.That(llm.GetKey("xai"), Is.EqualTo("x-2"), "app still reading xai follows the list");
        Assert.That(llm.LoadAllRaw().ContainsKey("grok"), Is.False, "no new id is invented when an alias already holds the keys");
        Assert.That(store.GetKeys("grok").Select(k => k.Name), Is.EqualTo(new[] { "key1", "second" }));
    }

    [Test]
    public void BothCanonicalAndAliasOnDisk_BothGetTheMergedList()
    {
        WriteProviders("""{ "claude": { "apiKey": "a" }, "anthropic": { "apiKey": "b" } }""");
        store.Rename("claude", "key2", "Team");
        var llm = new LlmCredentialStore(tmp.Combine("LLM"));
        Assert.That(llm.GetKeys("claude").Select(k => k.Label), Is.EqualTo(new[] { "key1", "Team" }));
        Assert.That(llm.GetKeys("anthropic").Select(k => k.Label), Is.EqualTo(new[] { "key1", "Team" }));
        Assert.That(store.GetKeys("claude").Count, Is.EqualTo(2));
    }

    [Test]
    public void KeyOverrideFile_LeadsTheListAndIsFoldedInOnFirstEdit()
    {
        WriteProviders("""{ "fal": { "apiKey": "pool-1" } }""");
        File.WriteAllText(tmp.Combine("LLM", "fal.key"), "file-key\n");
        Assert.That(store.GetKeys("fal").Select(k => k.Value), Is.EqualTo(new[] { "file-key", "pool-1" }));
        store.Add("fal", "extra", "pool-2");
        Assert.That(File.Exists(tmp.Combine("LLM", "fal.key")), Is.False);
        Assert.That(File.ReadAllText(tmp.Combine("LLM", "fal.key.imported")).Trim(), Is.EqualTo("file-key"));
        Assert.That(new LlmCredentialStore(tmp.Combine("LLM")).GetKey("fal"), Is.EqualTo("file-key"), "apps keep reading the same key");
        Assert.That(store.GetKeys("fal").Select(k => k.Value), Is.EqualTo(new[] { "file-key", "pool-1", "pool-2" }));
    }

    [Test]
    public void KeyFileOnlyProvider_IsListed()
    {
        Directory.CreateDirectory(tmp.Combine("LLM"));
        File.WriteAllText(tmp.Combine("LLM", "ideogram.key"), "i-1");
        Assert.That(store.ProvidersWithKeys(), Does.Contain("ideogram"));
    }

    [Test]
    public void ProvidersWithKeys_ListsCanonicalIds()
    {
        WriteProviders("""{ "anthropic": { "apiKey": "a" }, "openai": { "apiKey": "" } }""");
        Assert.That(store.ProvidersWithKeys(), Is.EqualTo(new[] { "claude" }));
    }

    [Test]
    public void ToString_NeverContainsTheValue()
    {
        Assert.That(new NamedKey("n", "sk-secret").ToString(), Does.Not.Contain("sk-secret"));
        Assert.That(new CredentialPoolEntry("sk-secret", "n").ToString(), Does.Not.Contain("sk-secret"));
    }
}

[TestFixture]
public sealed class KeyAssignmentStoreTests
{
    private TempDirectory tmp = null!;
    private KeyAssignmentStore store = null!;

    [SetUp]
    public void SetUp()
    {
        tmp = new TempDirectory("assignments");
        store = new KeyAssignmentStore(tmp.Path);
    }

    [TearDown] public void TearDown() => tmp.Dispose();

    [Test]
    public void MissingAssignment_IsEmpty() => Assert.That(store.Get("IdiotProof", "claude"), Is.Empty);

    [Test]
    public void SetGet_PreservesOrderNormalizesProviderAndDedupes()
    {
        store.Set("IdiotProof", "anthropic", new[] { "2026-10-31 IdiotProof", "key1", "KEY1", " " });
        Assert.That(store.Get("idiotproof", "claude"), Is.EqualTo(new[] { "2026-10-31 IdiotProof", "key1" }));
    }

    [Test]
    public void EmptyList_ClearsAndRemovesTheApp()
    {
        store.Set("Tutor", "claude", new[] { "a" });
        store.Set("Tutor", "claude", Array.Empty<string>());
        Assert.That(store.All().ContainsKey("Tutor"), Is.False);
    }

    [Test]
    public void FileHoldsNamesOnly()
    {
        store.Set("Tutor", "claude", new[] { "a" });
        var root = JsonNode.Parse(File.ReadAllText(store.FilePath))!;
        Assert.That(root["Tutor"]!["claude"]!.AsArray().Single()!.GetValue<string>(), Is.EqualTo("a"));
        Assert.That(Directory.GetFiles(tmp.Path, "*.tmp"), Is.Empty);
    }

    [Test]
    public void CorruptFile_ReadsAsNoAssignments()
    {
        File.WriteAllText(store.FilePath, "nope");
        Assert.That(store.Get("Tutor", "claude"), Is.Empty);
        store.Set("Tutor", "claude", new[] { "a" });
        Assert.That(store.Get("Tutor", "claude"), Is.EqualTo(new[] { "a" }));
    }

    [Test]
    public void AppsUsing_ListsAppsSorted()
    {
        store.Set("Tutor", "claude", new[] { "x", "shared" });
        store.Set("IdiotProof", "claude", new[] { "shared" });
        store.Set("Prose", "openai", new[] { "shared" });
        Assert.That(store.AppsUsing("anthropic", "SHARED"), Is.EqualTo(new[] { "IdiotProof", "Tutor" }));
    }
}
