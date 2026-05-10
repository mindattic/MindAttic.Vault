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
}
