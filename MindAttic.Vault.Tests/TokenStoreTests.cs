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

    // ── Constructor / argument validation ───────────────────────────────────────

    [Test]
    public void Constructor_Throws_When_Directory_Null_Or_Whitespace()
    {
        Assert.Throws<ArgumentException>(() => new TokenStore(""));
        Assert.Throws<ArgumentException>(() => new TokenStore("   "));
        Assert.Throws<ArgumentException>(() => new TokenStore(null!));
    }

    [Test]
    public void Set_Throws_For_Empty_Name()
    {
        using var tmp = new TempDirectory();
        var store = new TokenStore(tmp.Path);
        Assert.Throws<ArgumentException>(() => store.Set("",    "v"));
        Assert.Throws<ArgumentException>(() => store.Set("  ",  "v"));
        Assert.Throws<ArgumentException>(() => store.Set(null!, "v"));
    }

    [Test]
    public void Set_Treats_Null_Token_As_Empty_String()
    {
        using var tmp = new TempDirectory();
        var store = new TokenStore(tmp.Path);
        store.Set("github", null!);
        // Get() filters whitespace-only values, so an empty stored token resolves to null.
        Assert.That(store.Get("github"), Is.Null);
    }

    [Test]
    public void Get_Empty_Or_Null_Name_Returns_Null()
    {
        using var tmp = new TempDirectory();
        var store = new TokenStore(tmp.Path);
        store.Set("github", "g");
        Assert.That(store.Get(""),     Is.Null);
        Assert.That(store.Get("   "),  Is.Null);
        Assert.That(store.Get(null!),  Is.Null);
    }

    [Test]
    public void Remove_Empty_Or_Null_Name_Returns_False()
    {
        using var tmp = new TempDirectory();
        var store = new TokenStore(tmp.Path);
        Assert.That(store.Remove(""),    Is.False);
        Assert.That(store.Remove("   "), Is.False);
        Assert.That(store.Remove(null!), Is.False);
    }

    // ── ForBucket factory ───────────────────────────────────────────────────────

    [Test]
    public void ForBucket_Resolves_Roaming_Bucket_Path()
    {
        using var tmp = new TempDirectory();
        var originalRoaming = Environment.GetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.RoamingRootEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.RoamingRootEnvVar, tmp.Path);
            var store = TokenStore.ForBucket("GitHub");
            Assert.That(store.Directory, Is.EqualTo(Path.Combine(tmp.Path, "GitHub")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.RoamingRootEnvVar, originalRoaming);
        }
    }

    [Test]
    public void ForBucket_Throws_For_Empty_Bucket()
    {
        Assert.Throws<ArgumentException>(() => TokenStore.ForBucket(""));
        Assert.Throws<ArgumentException>(() => TokenStore.ForBucket("   "));
    }

    // ── LoadAll edge cases ──────────────────────────────────────────────────────

    [Test]
    public void LoadAll_Empty_File_Returns_Empty_Map()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "tokens.json"), "");
        var store = new TokenStore(tmp.Path);
        Assert.That(store.LoadAll(), Is.Empty);
    }

    [Test]
    public void LoadAll_Malformed_Json_Returns_Empty_Map()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "tokens.json"), "{ broken");
        var store = new TokenStore(tmp.Path);
        Assert.That(store.LoadAll(), Is.Empty);
    }

    [Test]
    public void LoadAll_Returns_All_Stored_Tokens()
    {
        using var tmp = new TempDirectory();
        var store = new TokenStore(tmp.Path);
        store.Set("github", "g");
        store.Set("usps",   "u");

        var all = store.LoadAll();
        Assert.That(all.Keys, Is.EquivalentTo(new[] { "github", "usps" }));
        Assert.That(all["github"], Is.EqualTo("g"));
    }

    // ── Properties / paths / atomic swap ────────────────────────────────────────

    [Test]
    public void TokensFilePath_Combines_Directory_And_Filename()
    {
        using var tmp = new TempDirectory();
        var store = new TokenStore(tmp.Path);
        Assert.That(store.TokensFilePath, Is.EqualTo(Path.Combine(tmp.Path, "tokens.json")));
    }

    [Test]
    public void Set_Creates_Backup_File_When_Replacing_Existing_Tokens_Json()
    {
        using var tmp = new TempDirectory();
        var store = new TokenStore(tmp.Path);

        store.Set("github", "g1");        // first write — no backup yet.
        Assert.That(File.Exists(Path.Combine(tmp.Path, "tokens.json.bak")), Is.False);

        store.Set("github", "g2");        // overwrite — File.Replace produces a .bak.
        Assert.That(File.Exists(Path.Combine(tmp.Path, "tokens.json.bak")), Is.True);
    }

    [Test]
    public void Multiple_Set_Remove_Cycles_Round_Trip_Correctly()
    {
        using var tmp = new TempDirectory();
        var store = new TokenStore(tmp.Path);

        store.Set("a", "1");
        store.Set("b", "2");
        store.Set("c", "3");
        Assert.That(store.Remove("b"), Is.True);
        store.Set("d", "4");

        var all = store.LoadAll();
        Assert.That(all.Keys, Is.EquivalentTo(new[] { "a", "c", "d" }));
    }

    [Test]
    public void TokensJsonFile_Constant_Is_Stable()
    {
        Assert.That(TokenStore.TokensJsonFile, Is.EqualTo("tokens.json"));
    }
}
