using MindAttic.Vault.Credentials;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public sealed class KeyImporterTests
{
    private TempDirectory tmp = null!;
    private NamedKeyStore keys = null!;
    private Dictionary<string, string?> env = null!;
    private KeyImporter importer = null!;
    private LlmCredentialStore Llm => new(tmp.Combine("LLM"));

    [SetUp]
    public void SetUp()
    {
        tmp = new TempDirectory("import");
        keys = new NamedKeyStore(tmp.Path, new KeyAssignmentStore(tmp.Combine("Vault")));
        env = new Dictionary<string, string?>();
        importer = new KeyImporter(keys, v => env.TryGetValue(v, out var x) ? x : null);
    }

    [TearDown] public void TearDown() => tmp.Dispose();

    [Test]
    public void AppScopedCopy_BecomesASharedNamedKeyAndThatAppsActiveKey()
    {
        keys.Add("claude", "key1", "sk-shared");
        new AppScopedCredentialStore("automata", Llm).SetKey("claude", "sk-automata");

        var candidates = importer.FindAppScoped();
        Assert.That(candidates.Select(c => (c.Source, c.ProviderId, c.AppId)), Is.EqualTo(new[] { ("automata-claude", "claude", (string?)"Automata") }));

        var result = importer.Import(candidates[0]);
        Assert.That(result.Added, Is.EqualTo(new[] { "Automata (imported)" }));
        Assert.That(keys.GetKeys("claude").Select(k => k.Name), Is.EqualTo(new[] { "key1", "Automata (imported)" }));
        Assert.That(keys.Assignments.Get("Automata", "claude"), Is.EqualTo(new[] { "Automata (imported)", "key1" }));
        Assert.That(Llm.LoadAllRaw().ContainsKey("automata-claude"), Is.False, "no per-app copy remains");
    }

    [Test]
    public void AppScopedValueAlreadyShared_IsReusedNotDuplicated()
    {
        keys.Add("claude", "main", "sk-same");
        new AppScopedCredentialStore("tutor", Llm).SetKey("anthropic", "sk-same");
        var result = importer.ImportAllAppScoped();
        Assert.That(result.Added, Is.Empty);
        Assert.That(result.Reused, Is.EqualTo(new[] { "main" }));
        Assert.That(keys.GetKeys("claude").Count, Is.EqualTo(1));
        Assert.That(keys.Assignments.Get("Tutor", "claude"), Is.EqualTo(new[] { "main" }));
    }

    [Test]
    public void AppScopedPool_KeepsTheAppsOrder()
    {
        new AppScopedCredentialStore("mediabutler", Llm).SetKeys("claude",
            new[] { new CredentialPoolEntry("sk-1"), new CredentialPoolEntry("sk-2") });
        importer.ImportAllAppScoped();
        var assigned = keys.Assignments.Get("MediaButler", "claude");
        Assert.That(assigned.Select(n => keys.Find("claude", n)!.Value), Is.EqualTo(new[] { "sk-1", "sk-2" }));
    }

    [Test]
    public void ImportTwice_ChangesNothing()
    {
        new AppScopedCredentialStore("jobhunt", Llm).SetKey("openai", "sk-o");
        importer.ImportAllAppScoped();
        var before = File.ReadAllText(tmp.Combine("LLM", "providers.json")) + File.ReadAllText(keys.Assignments.FilePath);
        var second = importer.ImportAllAppScoped();
        Assert.That(second.Added.Concat(second.Reused), Is.Empty);
        Assert.That(File.ReadAllText(tmp.Combine("LLM", "providers.json")) + File.ReadAllText(keys.Assignments.FilePath), Is.EqualTo(before));
    }

    [Test]
    public void UnrelatedDashedIds_AreNotAppScoped()
    {
        Llm.SetKey("google-maps", "m");
        Llm.SetKey("claude-api", "c");
        Llm.SetKey("unknownapp-claude", "u");
        Assert.That(importer.FindAppScoped(), Is.Empty);
    }

    [Test]
    public void EnvironmentVariables_AreFoundAndImportedOnce()
    {
        env["ELEVENLABS_API_KEY"] = "el-1";
        env["JF_API_KEY"] = " jf-1 ";
        var found = importer.FindEnvironment();
        Assert.That(found.Select(c => (c.Source, c.ProviderId)), Is.EqualTo(new[] { ("ELEVENLABS_API_KEY", "elevenlabs"), ("JF_API_KEY", "jellyfin") }));
        foreach (var c in found) importer.Import(c);
        Assert.That(keys.GetKeys("jellyfin").Single().Value, Is.EqualTo("jf-1"));
        Assert.That(keys.GetKeys("elevenlabs").Single().Name, Is.EqualTo("env ELEVENLABS_API_KEY"));
        Assert.That(importer.FindEnvironment(), Is.Empty, "already imported values aren't offered again");
    }

    [Test]
    public void LegionJson_ApiKeysAreOffered()
    {
        Directory.CreateDirectory(tmp.Combine("ThinkTank"));
        var path = tmp.Combine("ThinkTank", "legion.json");
        File.WriteAllText(path, """{ "voters": ["claude"], "apiKeys": { "anthropic": "sk-l", "openai": "" } }""");
        var found = importer.FindLegionJson(path, "ThinkTank");
        Assert.That(found.Single().ProviderId, Is.EqualTo("claude"));
        importer.Import(found[0]);
        Assert.That(keys.GetKeys("claude").Single().Name, Is.EqualTo("ThinkTank legion.json (imported)"));
        Assert.That(keys.Assignments.Get("ThinkTank", "claude").First(), Is.EqualTo("ThinkTank legion.json (imported)"));
        Assert.That(importer.FindLegionJson(path), Is.Empty);
    }

    [Test]
    public void NameClash_WithADifferentValue_GetsANumber()
    {
        keys.Add("claude", "Tutor (imported)", "sk-other");
        var (name, added) = importer.ImportValue("claude", "Tutor (imported)", "sk-new", null);
        Assert.That((name, added), Is.EqualTo(("Tutor (imported) 2", true)));
    }

    [Test]
    public void MissingLegionFileOrGarbage_OffersNothing()
    {
        Assert.That(importer.FindLegionJson(tmp.Combine("nope.json")), Is.Empty);
        File.WriteAllText(tmp.Combine("bad.json"), "not json");
        Assert.That(importer.FindLegionJson(tmp.Combine("bad.json")), Is.Empty);
    }
}
