using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Credentials;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public class CompositeCredentialStoreTests
{
    private static IConfiguration ConfigWith(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Test]
    public void Read_Returns_Configuration_Value_When_Both_Layers_Have_It()
    {
        using var tmp = new TempDirectory();
        var file = new CredentialStore(tmp.Path);
        file.SetKey("claude", "from-file");

        var config = ConfigWith(new Dictionary<string, string?>
        {
            ["MindAttic:Vault:LLM:claude:apiKey"] = "from-config"
        });

        var composite = new CompositeCredentialStore(
            ConfigurationCredentialStore.ForLlm(config),
            file);

        Assert.That(composite.GetKey("claude"), Is.EqualTo("from-config"));
    }

    [Test]
    public void Read_Falls_Back_To_File_Store_When_Configuration_Empty()
    {
        using var tmp = new TempDirectory();
        var file = new CredentialStore(tmp.Path);
        file.SetKey("claude", "from-file");

        var config = ConfigWith(new Dictionary<string, string?>());

        var composite = new CompositeCredentialStore(
            ConfigurationCredentialStore.ForLlm(config),
            file);

        Assert.That(composite.GetKey("claude"), Is.EqualTo("from-file"));
    }

    [Test]
    public void Write_Lands_In_First_Writable_Store_Skipping_Configuration()
    {
        using var tmp = new TempDirectory();
        var file = new CredentialStore(tmp.Path);
        var config = ConfigWith(new Dictionary<string, string?>());

        var composite = new CompositeCredentialStore(
            ConfigurationCredentialStore.ForLlm(config),
            file);

        composite.SetKey("openai", "k");

        Assert.That(file.GetKey("openai"), Is.EqualTo("k"));
    }

    [Test]
    public void LoadAll_Walks_Stores_So_Earliest_Wins_On_Collision()
    {
        using var tmp = new TempDirectory();
        var file = new CredentialStore(tmp.Path);
        file.SetKey("claude", "F");
        file.SetKey("gemini", "G-file");

        var config = ConfigWith(new Dictionary<string, string?>
        {
            ["MindAttic:Vault:LLM:claude:apiKey"] = "C-config"
        });

        var composite = new CompositeCredentialStore(
            ConfigurationCredentialStore.ForLlm(config),
            file);

        var all = composite.LoadAll();
        Assert.That(all["claude"], Is.EqualTo("C-config"));
        Assert.That(all["gemini"], Is.EqualTo("G-file"));
    }

    [Test]
    public void ListProviders_Unions_All_Layers()
    {
        using var tmp = new TempDirectory();
        var file = new CredentialStore(tmp.Path);
        file.SetKey("only-in-file", "F");

        var config = ConfigWith(new Dictionary<string, string?>
        {
            ["MindAttic:Vault:LLM:only-in-config:apiKey"] = "C"
        });

        var composite = new CompositeCredentialStore(
            ConfigurationCredentialStore.ForLlm(config),
            file);

        Assert.That(composite.ListProviders(), Is.EquivalentTo(new[] { "only-in-file", "only-in-config" }));
    }

    [Test]
    public void WritableStore_Skips_ConfigurationCredentialStore()
    {
        using var tmp = new TempDirectory();
        var file = new CredentialStore(tmp.Path);
        var config = ConfigWith(new Dictionary<string, string?>());

        var composite = new CompositeCredentialStore(
            ConfigurationCredentialStore.ForLlm(config),
            file);

        Assert.That(composite.WritableStore, Is.SameAs(file));
        Assert.That(composite.Directory,     Is.EqualTo(file.Directory));
    }

    [Test]
    public void Constructor_Throws_When_No_Stores_Supplied()
    {
        Assert.Throws<ArgumentException>(() => new CompositeCredentialStore());
    }

    [Test]
    public void Constructor_Throws_When_Stores_Enumerable_Is_Null()
    {
        Assert.Throws<ArgumentNullException>(
            () => new CompositeCredentialStore((IEnumerable<ICredentialStore>)null!));
    }

    [Test]
    public void Constructor_Filters_Null_Inner_Stores()
    {
        using var tmp = new TempDirectory();
        var file = new CredentialStore(tmp.Path);

        var composite = new CompositeCredentialStore(null!, file, null!);
        Assert.That(composite.WritableStore, Is.SameAs(file));
    }

    [Test]
    public void GetKey_Returns_Null_For_Empty_Provider_Id()
    {
        using var tmp = new TempDirectory();
        var composite = new CompositeCredentialStore(new CredentialStore(tmp.Path));
        Assert.That(composite.GetKey(""),    Is.Null);
        Assert.That(composite.GetKey("   "), Is.Null);
    }

    [Test]
    public void GetKey_Survives_Throwing_Inner_Store()
    {
        using var tmp = new TempDirectory();
        var file = new CredentialStore(tmp.Path);
        file.SetKey("claude", "from-file");

        var composite = new CompositeCredentialStore(new ThrowingStore(), file);
        Assert.That(composite.GetKey("claude"), Is.EqualTo("from-file"));
    }

    [Test]
    public void LoadAll_Survives_Throwing_Inner_Store()
    {
        using var tmp = new TempDirectory();
        var file = new CredentialStore(tmp.Path);
        file.SetKey("a", "1");

        var composite = new CompositeCredentialStore(new ThrowingStore(), file);
        Assert.That(composite.LoadAll(), Has.Count.EqualTo(1));
    }

    [Test]
    public void LoadAllRaw_Layers_With_Earlier_Stores_Winning()
    {
        using var tmp = new TempDirectory();
        var file = new CredentialStore(tmp.Path);
        file.SetKey("claude", "F");          // file gets a 'claude' entry.

        var config = ConfigWith(new Dictionary<string, string?>
        {
            ["MindAttic:Vault:LLM:claude:type"]   = "anthropic",
            ["MindAttic:Vault:LLM:claude:apiKey"] = "C",
            ["MindAttic:Vault:LLM:gemini:apiKey"] = "G",
        });

        var composite = new CompositeCredentialStore(
            ConfigurationCredentialStore.ForLlm(config),
            file);

        var raw = composite.LoadAllRaw();
        Assert.That(raw.Keys, Is.EquivalentTo(new[] { "claude", "gemini" }));
        // The earlier (config) store wins on collision: claude entry is the config one.
        using var claudeDoc = System.Text.Json.JsonDocument.Parse(raw["claude"]);
        Assert.That(claudeDoc.RootElement.GetProperty("type").GetString(), Is.EqualTo("anthropic"));
    }

    [Test]
    public void SaveAllRaw_Routes_To_Writable_Store()
    {
        using var tmp = new TempDirectory();
        var file = new CredentialStore(tmp.Path);
        var config = ConfigWith(new Dictionary<string, string?>());

        var composite = new CompositeCredentialStore(
            ConfigurationCredentialStore.ForLlm(config),
            file);

        composite.SaveAllRaw(new Dictionary<string, string>
        {
            ["acme"] = "{ \"apiKey\": \"k\" }"
        });

        Assert.That(file.GetKey("acme"), Is.EqualTo("k"));
    }

    [Test]
    public void SaveRaw_Routes_To_Writable_Store()
    {
        using var tmp = new TempDirectory();
        var file = new CredentialStore(tmp.Path);
        var config = ConfigWith(new Dictionary<string, string?>());

        var composite = new CompositeCredentialStore(
            ConfigurationCredentialStore.ForLlm(config),
            file);

        composite.SaveRaw("acme", "{ \"apiKey\": \"k\" }");
        Assert.That(file.GetKey("acme"), Is.EqualTo("k"));
    }

    [Test]
    public void ProvidersFileExists_Returns_True_When_Any_Inner_Store_Has_Data()
    {
        using var tmp = new TempDirectory();
        var file = new CredentialStore(tmp.Path);
        var emptyConfig = ConfigWith(new Dictionary<string, string?>());

        var composite = new CompositeCredentialStore(
            ConfigurationCredentialStore.ForLlm(emptyConfig),
            file);

        Assert.That(composite.ProvidersFileExists(), Is.False);

        file.SetKey("a", "b");
        Assert.That(composite.ProvidersFileExists(), Is.True);
    }

    [Test]
    public void Directory_And_ProvidersFilePath_Match_The_Writable_Store()
    {
        using var tmp = new TempDirectory();
        var file = new CredentialStore(tmp.Path);
        var config = ConfigWith(new Dictionary<string, string?>());

        var composite = new CompositeCredentialStore(
            ConfigurationCredentialStore.ForLlm(config),
            file);

        Assert.That(composite.Directory,         Is.EqualTo(file.Directory));
        Assert.That(composite.ProvidersFilePath, Is.EqualTo(file.ProvidersFilePath));
    }

    /// <summary>Test double — every operation throws, used to verify chain robustness.</summary>
    private sealed class ThrowingStore : ICredentialStore
    {
        public string Directory          => throw new InvalidOperationException();
        public string ProvidersFilePath  => throw new InvalidOperationException();
        public bool ProvidersFileExists()                          => throw new InvalidOperationException();
        public string? GetKey(string providerId)                   => throw new InvalidOperationException();
        public void SetKey(string providerId, string apiKey)       => throw new InvalidOperationException();
        public Dictionary<string, string> LoadAll()                => throw new InvalidOperationException();
        public List<string> ListProviders()                        => throw new InvalidOperationException();
        public Dictionary<string, string> LoadAllRaw()             => throw new InvalidOperationException();
        public void SaveAllRaw(IDictionary<string, string> p)      => throw new InvalidOperationException();
        public void SaveRaw(string p, string j)                    => throw new InvalidOperationException();
    }
}
