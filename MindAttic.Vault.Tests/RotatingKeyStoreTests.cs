using MindAttic.Vault.Credentials;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

/// <summary>
/// Covers <see cref="IRotatingKeyStore"/> support (multi-key pools) across
/// <see cref="CredentialStore"/>, <see cref="AppScopedCredentialStore"/>, and
/// <see cref="CompositeCredentialStore"/>.
/// </summary>
[TestFixture]
public class RotatingKeyStoreTests
{
    [Test]
    public void GetKeys_Returns_Empty_When_Directory_Missing()
    {
        using var tmp = new TempDirectory();
        var store = new CredentialStore(Path.Combine(tmp.Path, "absent"));
        Assert.That(store.GetKeys("claude"), Is.Empty);
    }

    [Test]
    public void GetKeys_Synthesizes_One_Element_Pool_From_Plain_ApiKey()
    {
        using var tmp = new TempDirectory();
        var store = new CredentialStore(tmp.Path);
        store.SetKey("claude", "sk-single");

        var pool = store.GetKeys("claude");
        Assert.That(pool, Has.Count.EqualTo(1));
        Assert.That(pool[0].Key, Is.EqualTo("sk-single"));
        Assert.That(pool[0].Label, Is.Null);
    }

    [Test]
    public void SetKeys_Then_GetKeys_Round_Trips_Multiple_Keys_In_Order()
    {
        using var tmp = new TempDirectory();
        var store = new CredentialStore(tmp.Path);

        store.SetKeys("claude", new[]
        {
            new CredentialPoolEntry("sk-primary", "Primary"),
            new CredentialPoolEntry("sk-secondary", "Secondary"),
        });

        var pool = store.GetKeys("claude");
        Assert.That(pool, Has.Count.EqualTo(2));
        Assert.That(pool[0], Is.EqualTo(new CredentialPoolEntry("sk-primary", "Primary")));
        Assert.That(pool[1], Is.EqualTo(new CredentialPoolEntry("sk-secondary", "Secondary")));
    }

    [Test]
    public void SetKeys_Mirrors_First_Entry_Into_Plain_ApiKey_For_Back_Compat()
    {
        using var tmp = new TempDirectory();
        var store = new CredentialStore(tmp.Path);

        store.SetKeys("claude", new[]
        {
            new CredentialPoolEntry("sk-primary"),
            new CredentialPoolEntry("sk-secondary"),
        });

        // A caller that only knows the single-key API must see the pool's head.
        Assert.That(store.GetKey("claude"), Is.EqualTo("sk-primary"));

        var raw = File.ReadAllText(Path.Combine(tmp.Path, "providers.json"));
        Assert.That(raw, Does.Contain("\"apiKey\": \"sk-primary\""));
        Assert.That(raw, Does.Contain("\"apiKeys\""));
    }

    [Test]
    public void SetKeys_With_Single_Entry_Omits_ApiKeys_Array_On_Disk()
    {
        using var tmp = new TempDirectory();
        var store = new CredentialStore(tmp.Path);

        store.SetKeys("claude", new[] { new CredentialPoolEntry("sk-only") });

        var raw = File.ReadAllText(Path.Combine(tmp.Path, "providers.json"));
        Assert.That(raw, Does.Not.Contain("apiKeys"));
        Assert.That(store.GetKeys("claude"), Has.Count.EqualTo(1));
    }

    [Test]
    public void SetKeys_Collapsing_From_Pool_To_Single_Key_Removes_ApiKeys_Array()
    {
        using var tmp = new TempDirectory();
        var store = new CredentialStore(tmp.Path);

        store.SetKeys("claude", new[] { new CredentialPoolEntry("sk-a"), new CredentialPoolEntry("sk-b") });
        store.SetKey("claude", "sk-solo"); // plain single-key write after a pool existed

        var raw = File.ReadAllText(Path.Combine(tmp.Path, "providers.json"));
        Assert.That(raw, Does.Not.Contain("apiKeys"));
        var pool = store.GetKeys("claude");
        Assert.That(pool, Has.Count.EqualTo(1));
        Assert.That(pool[0].Key, Is.EqualTo("sk-solo"));
    }

    [Test]
    public void SetKeys_Drops_Blank_Entries_And_Trims_Whitespace()
    {
        using var tmp = new TempDirectory();
        var store = new CredentialStore(tmp.Path);

        store.SetKeys("claude", new[]
        {
            new CredentialPoolEntry("  sk-a  ", "  Primary  "),
            new CredentialPoolEntry("   "),
            new CredentialPoolEntry("sk-b"),
        });

        var pool = store.GetKeys("claude");
        Assert.That(pool, Has.Count.EqualTo(2));
        Assert.That(pool[0], Is.EqualTo(new CredentialPoolEntry("sk-a", "Primary")));
        Assert.That(pool[1], Is.EqualTo(new CredentialPoolEntry("sk-b")));
    }

    [Test]
    public void SetKeys_With_Empty_List_Clears_The_Provider()
    {
        using var tmp = new TempDirectory();
        var store = new CredentialStore(tmp.Path);
        store.SetKeys("claude", new[] { new CredentialPoolEntry("sk-a") });

        store.SetKeys("claude", Array.Empty<CredentialPoolEntry>());

        Assert.That(store.GetKeys("claude"), Is.Empty);
        Assert.That(store.GetKey("claude"), Is.Null);
    }

    [Test]
    public void SetKeys_Preserves_Sibling_Fields_Like_Llm_Model()
    {
        using var tmp = new TempDirectory();
        var store = new LlmCredentialStore(tmp.Path);
        store.SetKey("claude", "sk-a");
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"),
            File.ReadAllText(Path.Combine(tmp.Path, "providers.json"))
                .Replace("\"type\": \"anthropic\"", "\"type\": \"anthropic\",\n  \"model\": \"claude-sonnet-5\""));

        store.SetKeys("claude", new[] { new CredentialPoolEntry("sk-a"), new CredentialPoolEntry("sk-b") });

        var raw = File.ReadAllText(Path.Combine(tmp.Path, "providers.json"));
        Assert.That(raw, Does.Contain("\"model\": \"claude-sonnet-5\""));
        Assert.That(raw, Does.Contain("\"type\": \"anthropic\""));
    }

    [Test]
    public void AppScopedCredentialStore_Scopes_Pool_Reads_And_Writes()
    {
        using var tmp = new TempDirectory();
        var inner = new CredentialStore(tmp.Path);
        var scoped = new AppScopedCredentialStore("automata", inner);

        scoped.SetKeys("claude", new[] { new CredentialPoolEntry("sk-own-1"), new CredentialPoolEntry("sk-own-2") });
        inner.SetKeys("claude", new[] { new CredentialPoolEntry("sk-shared") });

        Assert.That(scoped.GetKeys("claude").Select(k => k.Key), Is.EqualTo(new[] { "sk-own-1", "sk-own-2" }));
        Assert.That(inner.GetKeys("automata-claude").Select(k => k.Key), Is.EqualTo(new[] { "sk-own-1", "sk-own-2" }));
        Assert.That(inner.GetKeys("claude").Select(k => k.Key), Is.EqualTo(new[] { "sk-shared" }));
    }

    [Test]
    public void AppScopedCredentialStore_Throws_When_Inner_Store_Does_Not_Support_Pools()
    {
        var scoped = new AppScopedCredentialStore("automata", NonRotatingStore.Instance);
        Assert.Throws<NotSupportedException>(() => scoped.GetKeys("claude"));
        Assert.Throws<NotSupportedException>(() => scoped.SetKeys("claude", new[] { new CredentialPoolEntry("x") }));
    }

    [Test]
    public void CompositeCredentialStore_GetKeys_Returns_First_Non_Empty_Pool()
    {
        using var tmp = new TempDirectory();
        var own = new AppScopedCredentialStore("automata", new CredentialStore(tmp.Path));
        var shared = new CredentialStore(tmp.Path);
        shared.SetKeys("claude", new[] { new CredentialPoolEntry("sk-shared") });

        var composite = new CompositeCredentialStore(own, shared);
        Assert.That(composite.GetKeys("claude").Select(k => k.Key), Is.EqualTo(new[] { "sk-shared" }));

        own.SetKeys("claude", new[] { new CredentialPoolEntry("sk-own-1"), new CredentialPoolEntry("sk-own-2") });
        Assert.That(composite.GetKeys("claude").Select(k => k.Key), Is.EqualTo(new[] { "sk-own-1", "sk-own-2" }));
    }

    [Test]
    public void CompositeCredentialStore_SetKeys_Writes_To_Writable_Store()
    {
        using var tmp = new TempDirectory();
        var store = new CredentialStore(tmp.Path);
        var composite = new CompositeCredentialStore(store);

        composite.SetKeys("claude", new[] { new CredentialPoolEntry("sk-a"), new CredentialPoolEntry("sk-b") });

        Assert.That(store.GetKeys("claude").Select(k => k.Key), Is.EqualTo(new[] { "sk-a", "sk-b" }));
    }

    [Test]
    public void CompositeCredentialStore_GetKeys_Skips_Throwing_Inner_Store()
    {
        using var tmp = new TempDirectory();
        var shared = new CredentialStore(tmp.Path);
        shared.SetKeys("claude", new[] { new CredentialPoolEntry("sk-shared") });

        var composite = new CompositeCredentialStore(new ThrowingRotatingStore(), shared);
        Assert.That(composite.GetKeys("claude").Select(k => k.Key), Is.EqualTo(new[] { "sk-shared" }));
    }

    private sealed class NonRotatingStore : ICredentialStore
    {
        public static readonly NonRotatingStore Instance = new();
        public string Directory => "(none)";
        public string ProvidersFilePath => "(none)";
        public bool ProvidersFileExists() => false;
        public string? GetKey(string providerId) => null;
        public void SetKey(string providerId, string apiKey) { }
        public Dictionary<string, string> LoadAll() => new();
        public List<string> ListProviders() => new();
        public Dictionary<string, string> LoadAllRaw() => new();
        public void SaveAllRaw(IDictionary<string, string> providers) { }
        public void SaveRaw(string providerId, string rawProviderJson) { }
    }

    private sealed class ThrowingRotatingStore : ICredentialStore, IRotatingKeyStore
    {
        public string Directory => throw new InvalidOperationException("boom");
        public string ProvidersFilePath => throw new InvalidOperationException("boom");
        public bool ProvidersFileExists() => throw new InvalidOperationException("boom");
        public string? GetKey(string providerId) => throw new InvalidOperationException("boom");
        public void SetKey(string providerId, string apiKey) => throw new InvalidOperationException("boom");
        public Dictionary<string, string> LoadAll() => throw new InvalidOperationException("boom");
        public List<string> ListProviders() => throw new InvalidOperationException("boom");
        public Dictionary<string, string> LoadAllRaw() => throw new InvalidOperationException("boom");
        public void SaveAllRaw(IDictionary<string, string> providers) => throw new InvalidOperationException("boom");
        public void SaveRaw(string providerId, string rawProviderJson) => throw new InvalidOperationException("boom");
        public IReadOnlyList<CredentialPoolEntry> GetKeys(string providerId) => throw new InvalidOperationException("boom");
        public void SetKeys(string providerId, IReadOnlyList<CredentialPoolEntry> keys) => throw new InvalidOperationException("boom");
    }
}
