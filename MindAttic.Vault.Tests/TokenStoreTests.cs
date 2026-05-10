using MindAttic.Vault.Credentials;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public class TokenStoreTests
{
    [Test]
    public void Get_Returns_Null_When_File_Missing()
    {
        using var tmp = new TempDirectory();
        var store = new TokenStore(Path.Combine(tmp.Path, "nope"));
        Assert.That(store.Get("github"), Is.Null);
    }

    [Test]
    public void Set_Then_Get_Roundtrips()
    {
        using var tmp = new TempDirectory();
        var store = new TokenStore(tmp.Path);

        store.Set("github", "ghp_abc");
        Assert.That(store.Get("github"), Is.EqualTo("ghp_abc"));
    }

    [Test]
    public void Set_Trims_Whitespace()
    {
        using var tmp = new TempDirectory();
        var store = new TokenStore(tmp.Path);
        store.Set("usps", "  USPS-XYZ \n");
        Assert.That(store.Get("usps"), Is.EqualTo("USPS-XYZ"));
    }

    [Test]
    public void LoadAll_Is_Case_Insensitive()
    {
        using var tmp = new TempDirectory();
        var store = new TokenStore(tmp.Path);
        store.Set("GitHub", "g");
        Assert.That(store.Get("github"), Is.EqualTo("g"));
        Assert.That(store.Get("GITHUB"), Is.EqualTo("g"));
    }

    [Test]
    public void Remove_Returns_False_When_Missing_True_When_Removed()
    {
        using var tmp = new TempDirectory();
        var store = new TokenStore(tmp.Path);
        Assert.That(store.Remove("ghost"), Is.False);

        store.Set("github", "g");
        Assert.That(store.Remove("github"), Is.True);
        Assert.That(store.Get("github"), Is.Null);
    }
}
