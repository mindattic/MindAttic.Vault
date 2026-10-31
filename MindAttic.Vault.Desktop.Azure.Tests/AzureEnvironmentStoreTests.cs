using MindAttic.Vault.Desktop.Azure.Environments;
using MindAttic.Vault.Paths;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Azure.Tests;

[TestFixture]
public sealed class AzureEnvironmentStoreTests
{
    [Test]
    public void SaveSelectRemove_RoundTrip()
    {
        using var temp = new TempDirectory();
        var store = new AzureEnvironmentStore(temp.Path);
        Assert.That(store.All(), Is.Empty);
        Assert.That(store.Current(), Is.Null);

        store.Save(ResourcePickerTests.Env("Dev"));
        Assert.That(store.Current()?.Name, Is.EqualTo("Dev"), "a lone environment is current");
        store.Save(ResourcePickerTests.Env("Prod", "kvp.vault.azure.net"), select: false);
        Assert.That(store.Current()?.Name, Is.EqualTo("Dev"));
        store.Select("PROD");
        Assert.That(store.Current()?.KeyVaultUri, Is.EqualTo("https://kvp.vault.azure.net/"));

        store.Save(ResourcePickerTests.Env("prod", "kvq.vault.azure.net")); // replaces by name, case-insensitive
        Assert.That(store.All().Count, Is.EqualTo(2));
        Assert.That(new AzureEnvironmentStore(temp.Path).Current()?.KeyVaultUri, Is.EqualTo("https://kvq.vault.azure.net/"));

        Assert.That(store.Remove("prod"), Is.True);
        Assert.That(store.Current()?.Name, Is.EqualTo("Dev"));
        Assert.That(store.Remove("nope"), Is.False);
        Assert.Throws<KeyNotFoundException>(() => store.Select("nope"));
    }

    [Test]
    public void Save_RefusesIncompleteEnvironments()
    {
        using var temp = new TempDirectory();
        var store = new AzureEnvironmentStore(temp.Path);
        var env = ResourcePickerTests.Env("Dev");
        env.KeyVaultUri = "http://not-https.example/";
        Assert.Throws<ArgumentException>(() => store.Save(env));
        Assert.Throws<ArgumentException>(() => store.Save(ResourcePickerTests.Env("  ")));
        Assert.That(File.Exists(store.FilePath), Is.False);
    }

    [Test]
    public void CorruptFile_ReadsAsEmpty()
    {
        using var temp = new TempDirectory();
        var store = new AzureEnvironmentStore(temp.Path);
        File.WriteAllText(store.FilePath, "{ not json");
        Assert.That(store.All(), Is.Empty);
        store.Save(ResourcePickerTests.Env("Dev"));
        Assert.That(store.All().Single().Name, Is.EqualTo("Dev"));
    }

    [Test]
    public void ForRoamingRoot_IsTheVaultDesktopBucketFile()
    {
        using var temp = new TempDirectory();
        var previous = Environment.GetEnvironmentVariable(VaultPaths.RoamingRootEnvVar);
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, temp.Path);
        try
        {
            var store = AzureEnvironmentStore.ForRoamingRoot();
            Assert.That(store.FilePath, Is.EqualTo(Path.Combine(VaultPaths.RoamingBucket("Vault.Desktop"), "azure-environments.json")));
            Assert.That(store.FilePath, Does.StartWith(temp.Path));
        }
        finally
        {
            Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, previous);
        }
    }

    [Test]
    public void Changed_IsRaisedOnEveryEdit()
    {
        using var temp = new TempDirectory();
        var store = new AzureEnvironmentStore(temp.Path);
        var raised = 0;
        store.Changed += (_, _) => raised++;
        store.Save(ResourcePickerTests.Env("Dev"));
        store.Select("Dev");
        store.Remove("Dev");
        Assert.That(raised, Is.EqualTo(3));
    }
}
