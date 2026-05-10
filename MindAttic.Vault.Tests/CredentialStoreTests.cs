using System.Text.Json;
using MindAttic.Vault.Credentials;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public class CredentialStoreTests
{
    [Test]
    public void GetKey_Returns_Null_When_Directory_Missing()
    {
        using var tmp = new TempDirectory();
        var store = new CredentialStore(Path.Combine(tmp.Path, "absent"));
        Assert.That(store.GetKey("anything"), Is.Null);
    }

    [Test]
    public void GetKey_Reads_Per_Provider_Key_File_With_Highest_Priority()
    {
        using var tmp = new TempDirectory();
        Directory.CreateDirectory(tmp.Path);

        File.WriteAllText(Path.Combine(tmp.Path, "claude.key"), "  override-from-key-file  \n");
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"),
            "{ \"claude\": { \"type\": \"anthropic\", \"apiKey\": \"from-providers\" } }");
        File.WriteAllText(Path.Combine(tmp.Path, "credentials.json"),
            "{ \"claude\": \"from-credentials\" }");

        var store = new CredentialStore(tmp.Path);
        Assert.That(store.GetKey("claude"), Is.EqualTo("override-from-key-file"));
    }

    [Test]
    public void GetKey_Falls_Back_To_Providers_Json_When_No_Key_File()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"),
            "{ \"claude\": { \"type\": \"anthropic\", \"apiKey\": \"from-providers\" } }");
        File.WriteAllText(Path.Combine(tmp.Path, "credentials.json"),
            "{ \"claude\": \"from-credentials\" }");

        var store = new CredentialStore(tmp.Path);
        Assert.That(store.GetKey("claude"), Is.EqualTo("from-providers"));
    }

    [Test]
    public void GetKey_Falls_Back_To_Credentials_Json_Last()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "credentials.json"),
            "{ \"openai\": \"sk-legacy-12345\" }");

        var store = new CredentialStore(tmp.Path);
        Assert.That(store.GetKey("openai"), Is.EqualTo("sk-legacy-12345"));
    }

    [Test]
    public void GetKey_Returns_Null_When_Provider_Not_Found()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"), "{ }");
        var store = new CredentialStore(tmp.Path);
        Assert.That(store.GetKey("anyone"), Is.Null);
    }

    [Test]
    public void GetKey_Survives_Malformed_Providers_Json()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"), "{ this is not json");
        var store = new CredentialStore(tmp.Path);
        Assert.That(store.GetKey("claude"), Is.Null);
    }

    [Test]
    public void SetKey_Creates_Directory_And_Writes_Pretty_Json()
    {
        using var tmp = new TempDirectory();
        var dir = Path.Combine(tmp.Path, "fresh");
        var store = new CredentialStore(dir);

        store.SetKey("openai", "sk-test-789");

        var path = Path.Combine(dir, "providers.json");
        Assert.That(File.Exists(path), Is.True);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.That(doc.RootElement.TryGetProperty("openai", out var entry), Is.True);
        Assert.That(entry.GetProperty("apiKey").GetString(), Is.EqualTo("sk-test-789"));
    }

    [Test]
    public void SetKey_Preserves_Sibling_Fields_On_Existing_Provider()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"),
            "{ \"acme\": { \"type\": \"bearer\", \"apiKey\": \"old\", \"region\": \"us\" } }");

        var store = new CredentialStore(tmp.Path);
        store.SetKey("acme", "new-rotated-key");

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "providers.json")));
        var entry = doc.RootElement.GetProperty("acme");
        Assert.That(entry.GetProperty("apiKey").GetString(), Is.EqualTo("new-rotated-key"));
        Assert.That(entry.GetProperty("type").GetString(),   Is.EqualTo("bearer"));
        Assert.That(entry.GetProperty("region").GetString(), Is.EqualTo("us"));
    }

    [Test]
    public void LoadAll_Layers_Sources_With_Correct_Priority()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "credentials.json"),
            "{ \"claude\": \"flat\", \"shared\": \"flat-shared\" }");
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"),
            "{ \"claude\": { \"apiKey\": \"providers\" }, \"shared\": { \"apiKey\": \"providers-shared\" } }");
        File.WriteAllText(Path.Combine(tmp.Path, "claude.key"), "key-file-wins");

        var store = new CredentialStore(tmp.Path);
        var all = store.LoadAll();

        Assert.That(all["claude"], Is.EqualTo("key-file-wins"));
        Assert.That(all["shared"], Is.EqualTo("providers-shared"));
    }

    [Test]
    public void ListProviders_Returns_All_Layered_Provider_Ids()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "credentials.json"), "{ \"a\": \"1\" }");
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"),  "{ \"b\": { \"apiKey\": \"2\" } }");
        File.WriteAllText(Path.Combine(tmp.Path, "c.key"), "3");

        var store = new CredentialStore(tmp.Path);
        Assert.That(store.ListProviders(), Is.EquivalentTo(new[] { "a", "b", "c" }));
    }

    [Test]
    public void SaveAllRaw_Replaces_Providers_Json_Atomically()
    {
        using var tmp = new TempDirectory();
        var store = new CredentialStore(tmp.Path);

        store.SaveAllRaw(new Dictionary<string, string>
        {
            ["zeta"]  = "{ \"apiKey\": \"z\" }",
            ["alpha"] = "{ \"apiKey\": \"a\" }",
        });

        var json = File.ReadAllText(Path.Combine(tmp.Path, "providers.json"));
        // Sorted alphabetically.
        Assert.That(json.IndexOf("alpha"), Is.LessThan(json.IndexOf("zeta")));
    }

    [Test]
    public void SaveRaw_Upserts_Single_Entry_Without_Touching_Others()
    {
        using var tmp = new TempDirectory();
        var store = new CredentialStore(tmp.Path);
        store.SaveAllRaw(new Dictionary<string, string>
        {
            ["one"] = "{ \"apiKey\": \"k1\" }",
            ["two"] = "{ \"apiKey\": \"k2\" }",
        });

        store.SaveRaw("one", "{ \"apiKey\": \"k1-rotated\" }");

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "providers.json")));
        Assert.That(doc.RootElement.GetProperty("one").GetProperty("apiKey").GetString(), Is.EqualTo("k1-rotated"));
        Assert.That(doc.RootElement.GetProperty("two").GetProperty("apiKey").GetString(), Is.EqualTo("k2"));
    }
}
