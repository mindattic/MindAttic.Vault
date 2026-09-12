using MindAttic.Vault.Credentials;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public class AppScopedCredentialStoreTests
{
    [Test]
    public void SetKey_Writes_Under_Prefixed_Provider_Id()
    {
        using var tmp = new TempDirectory();
        var inner = new CredentialStore(tmp.Path);
        var scoped = new AppScopedCredentialStore("automata", inner);

        scoped.SetKey("claude", "own-key");

        Assert.That(inner.GetKey("automata-claude"), Is.EqualTo("own-key"));
        Assert.That(inner.GetKey("claude"), Is.Null);
    }

    [Test]
    public void GetKey_Reads_Only_This_Apps_Own_Prefixed_Entry()
    {
        using var tmp = new TempDirectory();
        var inner = new CredentialStore(tmp.Path);
        inner.SetKey("claude", "shared-key");
        inner.SetKey("tutor-claude", "tutor-key");

        var scoped = new AppScopedCredentialStore("automata", inner);

        Assert.That(scoped.GetKey("claude"), Is.Null);
    }

    [Test]
    public void Composite_Of_Scoped_Then_Shared_Prefers_Own_Key()
    {
        using var tmp = new TempDirectory();
        var inner = new CredentialStore(tmp.Path);
        inner.SetKey("claude", "shared-key");
        inner.SetKey("automata-claude", "own-key");

        var resolver = new CompositeCredentialStore(
            new AppScopedCredentialStore("automata", inner),
            inner);

        Assert.That(resolver.GetKey("claude"), Is.EqualTo("own-key"));
    }

    [Test]
    public void Composite_Of_Scoped_Then_Shared_Falls_Back_When_No_Own_Key()
    {
        using var tmp = new TempDirectory();
        var inner = new CredentialStore(tmp.Path);
        inner.SetKey("claude", "shared-key");

        var resolver = new CompositeCredentialStore(
            new AppScopedCredentialStore("automata", inner),
            inner);

        Assert.That(resolver.GetKey("claude"), Is.EqualTo("shared-key"));
    }

    [Test]
    public void Composite_Write_Lands_In_Scoped_Store_Not_Shared()
    {
        using var tmp = new TempDirectory();
        var inner = new CredentialStore(tmp.Path);

        var resolver = new CompositeCredentialStore(
            new AppScopedCredentialStore("automata", inner),
            inner);

        resolver.SetKey("claude", "new-key");

        Assert.That(inner.GetKey("automata-claude"), Is.EqualTo("new-key"));
        Assert.That(inner.GetKey("claude"), Is.Null);
    }

    [Test]
    public void LoadAll_Returns_Only_Own_Entries_With_Prefix_Stripped()
    {
        using var tmp = new TempDirectory();
        var inner = new CredentialStore(tmp.Path);
        inner.SetKey("claude", "shared-key");
        inner.SetKey("automata-claude", "own-claude");
        inner.SetKey("automata-openai", "own-openai");
        inner.SetKey("tutor-claude", "tutor-key");

        var scoped = new AppScopedCredentialStore("automata", inner);
        var all = scoped.LoadAll();

        Assert.That(all, Is.EquivalentTo(new Dictionary<string, string>
        {
            ["claude"] = "own-claude",
            ["openai"] = "own-openai",
        }));
    }

    [Test]
    public void ListProviders_Returns_Unprefixed_Own_Ids()
    {
        using var tmp = new TempDirectory();
        var inner = new CredentialStore(tmp.Path);
        inner.SetKey("automata-claude", "own-claude");
        inner.SetKey("tutor-claude", "tutor-key");

        var scoped = new AppScopedCredentialStore("automata", inner);

        Assert.That(scoped.ListProviders(), Is.EquivalentTo(new[] { "claude" }));
    }

    [Test]
    public void SaveAllRaw_Replaces_Only_Own_Entries_Leaving_Others_Untouched()
    {
        using var tmp = new TempDirectory();
        var inner = new CredentialStore(tmp.Path);
        inner.SetKey("claude", "shared-key");
        inner.SetKey("automata-claude", "stale-own-claude");
        inner.SetKey("tutor-claude", "tutor-key");

        var scoped = new AppScopedCredentialStore("automata", inner);
        scoped.SaveAllRaw(new Dictionary<string, string>
        {
            ["openai"] = "{\"apiKey\":\"fresh-openai\"}",
        });

        Assert.That(inner.GetKey("claude"), Is.EqualTo("shared-key"));
        Assert.That(inner.GetKey("tutor-claude"), Is.EqualTo("tutor-key"));
        Assert.That(inner.GetKey("automata-claude"), Is.Null);
        Assert.That(inner.GetKey("automata-openai"), Is.EqualTo("fresh-openai"));
    }

    [Test]
    public void GetKey_Returns_Null_For_Blank_Provider_Id()
    {
        using var tmp = new TempDirectory();
        var scoped = new AppScopedCredentialStore("automata", new CredentialStore(tmp.Path));

        Assert.That(scoped.GetKey(""), Is.Null);
        Assert.That(scoped.GetKey("   "), Is.Null);
    }

    [Test]
    public void SetKey_Throws_For_Blank_Provider_Id()
    {
        using var tmp = new TempDirectory();
        var scoped = new AppScopedCredentialStore("automata", new CredentialStore(tmp.Path));

        Assert.Throws<ArgumentException>(() => scoped.SetKey("", "k"));
    }

    [Test]
    public void Constructor_Throws_For_Blank_AppId()
    {
        using var tmp = new TempDirectory();
        Assert.Throws<ArgumentException>(() => new AppScopedCredentialStore("", new CredentialStore(tmp.Path)));
    }

    [Test]
    public void Constructor_Throws_For_Null_Inner()
    {
        Assert.Throws<ArgumentNullException>(() => new AppScopedCredentialStore("automata", null!));
    }

    [Test]
    public void Directory_And_ProvidersFilePath_Delegate_To_Inner()
    {
        using var tmp = new TempDirectory();
        var inner = new CredentialStore(tmp.Path);
        var scoped = new AppScopedCredentialStore("automata", inner);

        Assert.That(scoped.Directory, Is.EqualTo(inner.Directory));
        Assert.That(scoped.ProvidersFilePath, Is.EqualTo(inner.ProvidersFilePath));
    }
}
