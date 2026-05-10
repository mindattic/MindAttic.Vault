using MindAttic.Vault.Paths;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public class VaultPathsTests
{
    private string? originalRoaming;
    private string? originalLocal;

    [SetUp]
    public void SetUp()
    {
        originalRoaming = Environment.GetEnvironmentVariable(VaultPaths.RoamingRootEnvVar);
        originalLocal   = Environment.GetEnvironmentVariable(VaultPaths.LocalRootEnvVar);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, originalRoaming);
        Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar,   originalLocal);
    }

    [Test]
    public void RoamingRoot_Defaults_To_AppData_MindAttic_When_Env_Unset()
    {
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, null);
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            VaultPaths.MindAtticFolder);
        Assert.That(VaultPaths.RoamingRoot, Is.EqualTo(expected));
    }

    [Test]
    public void RoamingRoot_Honours_Override_Env_Var()
    {
        using var tmp = new TempDirectory();
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, tmp.Path);
        Assert.That(VaultPaths.RoamingRoot, Is.EqualTo(tmp.Path));
    }

    [Test]
    public void LocalRoot_Honours_Override_Env_Var()
    {
        using var tmp = new TempDirectory();
        Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, tmp.Path);
        Assert.That(VaultPaths.LocalRoot, Is.EqualTo(tmp.Path));
    }

    [Test]
    public void RoamingBucket_Combines_Root_And_Bucket()
    {
        using var tmp = new TempDirectory();
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, tmp.Path);
        Assert.That(VaultPaths.RoamingBucket("LLM"), Is.EqualTo(Path.Combine(tmp.Path, "LLM")));
    }

    [Test]
    public void LocalApp_Combines_Root_And_App()
    {
        using var tmp = new TempDirectory();
        Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, tmp.Path);
        Assert.That(VaultPaths.LocalApp("IdiotProof"), Is.EqualTo(Path.Combine(tmp.Path, "IdiotProof")));
    }

    [Test]
    public void Ensure_Creates_Directory_And_Returns_Path()
    {
        using var tmp = new TempDirectory();
        var target = Path.Combine(tmp.Path, "nested", "deep");
        var returned = VaultPaths.Ensure(target);
        Assert.That(returned, Is.EqualTo(target));
        Assert.That(Directory.Exists(target), Is.True);
    }

    [Test]
    public void RoamingBucket_Throws_For_Empty_Bucket()
    {
        Assert.Throws<ArgumentException>(() => VaultPaths.RoamingBucket(""));
        Assert.Throws<ArgumentException>(() => VaultPaths.RoamingBucket("   "));
    }
}
