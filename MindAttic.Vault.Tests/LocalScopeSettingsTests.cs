using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Paths;
using MindAttic.Vault.Settings;
using MindAttic.Vault.Settings.Manifest;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[SettingsApp("MachineApp", "Machine App", Local = true)]
public sealed class MachineSettings
{
    [Setting("Hotkey")] public string? Hotkey { get; set; }
    public bool StartWithWindows { get; set; } = true;
}

[TestFixture]
[NonParallelizable]
public sealed class LocalScopeSettingsTests
{
    private TempDirectory tmp = null!;
    private string? roaming, local;

    [SetUp]
    public void SetUp()
    {
        tmp = new TempDirectory("local-scope");
        roaming = Environment.GetEnvironmentVariable(VaultPaths.RoamingRootEnvVar);
        local = Environment.GetEnvironmentVariable(VaultPaths.LocalRootEnvVar);
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, tmp.Combine("roaming"));
        Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, tmp.Combine("local"));
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, roaming);
        Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, local);
        tmp.Dispose();
    }

    [Test]
    public void ManifestCarriesScope()
    {
        Assert.That(SettingsManifestBuilder.Build<MachineSettings>().Scope, Is.EqualTo(SettingsManifest.LocalScope));
        Assert.That(SettingsManifestBuilder.Build<SampleSettings>().Scope, Is.EqualTo(SettingsManifest.RoamingScope));
    }

    [Test]
    public void ForSettingsApp_UsesTheLocalFolder()
    {
        var store = JsonSettingsStore<MachineSettings>.ForSettingsApp();
        Assert.That(store.Directory, Is.EqualTo(Path.Combine(tmp.Combine("local"), "MachineApp")));
        store.LoadOrSeed();
        Assert.That(File.Exists(tmp.Combine("local", "MachineApp", SettingsManifest.FileName)), Is.True);
    }

    [Test]
    public void Discover_FindsBothRootsWithTheirFolders()
    {
        JsonSettingsStore<MachineSettings>.ForSettingsApp().LoadOrSeed();
        JsonSettingsStore<SampleSettings>.ForSettingsApp().LoadOrSeed();
        var found = SettingsManifestFile.Discover();
        Assert.That(found.Select(f => (f.Manifest.AppId, f.Directory)), Is.EquivalentTo(new[]
        {
            ("MachineApp", tmp.Combine("local", "MachineApp")),
            ("SampleApp", tmp.Combine("roaming", "SampleApp")),
        }));
    }

    [Test]
    public void Discover_PrefersTheFolderMatchingTheDeclaredScope()
    {
        JsonSettingsStore<MachineSettings>.ForSettingsApp().LoadOrSeed();
        SettingsManifestFile.WriteIfChanged(tmp.Combine("roaming", "MachineApp"), SettingsManifestBuilder.Build<MachineSettings>());
        Assert.That(SettingsManifestFile.Discover().Single().Directory, Is.EqualTo(tmp.Combine("local", "MachineApp")));
    }

    [Test]
    public void VaultConfiguration_ReadsLocalAppsButNotOtherLocalFolders()
    {
        JsonSettingsStore<MachineSettings>.ForSettingsApp().Save(new MachineSettings { Hotkey = "Ctrl+Alt+U" });
        JsonSettingsStore<MachineSettings>.ForSettingsApp().LoadOrSeed();
        Directory.CreateDirectory(tmp.Combine("local", "SomeCache"));
        File.WriteAllText(tmp.Combine("local", "SomeCache", "settings.json"), """{ "x": 1 }""");
        var config = new ConfigurationBuilder().AddMindAtticVaultConfiguration().Build();
        Assert.That(config["MindAttic:Settings:MachineApp:hotkey"], Is.EqualTo("Ctrl+Alt+U"));
        Assert.That(config["MindAttic:Settings:SomeCache:x"], Is.Null);
    }
}
