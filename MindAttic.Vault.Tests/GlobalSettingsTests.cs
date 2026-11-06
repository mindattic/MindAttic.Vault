using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Paths;
using MindAttic.Vault.Settings;
using MindAttic.Vault.Settings.Manifest;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

/// <summary>
/// M.VALT-T77: Global holds exactly the values every app should reach (theme, workspace root, alert recipient), and apps
/// resolve their own optional copy through <see cref="MindAtticGlobalSettings.Resolve(string?, Func{MindAtticGlobalSettings, string?})"/>:
/// blank follows Global, set overrides it.
/// </summary>
[TestFixture]
[NonParallelizable] // points the Vault's roaming root at a temp folder, which is process-wide
public sealed class GlobalSettingsTests
{
    private TempDirectory tmp = null!;
    private string? roaming, local;

    [SetUp]
    public void SetUp()
    {
        tmp = new TempDirectory("global-settings");
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
    public void DefaultToDesktop1_DefaultsOn()
    {
        Assert.That(new MindAtticGlobalSettings().DefaultToDesktop1, Is.True, "on by default: new MindAttic windows shouldn't land on whatever desktop the user is gaming on");
    }

    [Test]
    public void AlertRecipient_DefaultsBlank_AndRoundTripsUnderItsManifestName()
    {
        Assert.That(new MindAtticGlobalSettings().AlertRecipient, Is.Empty, "blank until the user sets it");
        var store = MindAtticGlobalSettings.Store();
        store.Save(new MindAtticGlobalSettings { AlertRecipient = "ryan", WorkspaceRoot = @"D:\Projects\MindAttic" });

        Assert.That(store.FilePath, Is.EqualTo(Path.Combine(tmp.Combine("roaming"), "Global", "settings.json")));
        Assert.That(File.ReadAllText(store.FilePath), Does.Contain("\"alertRecipient\": \"ryan\""));
        var loaded = store.Load();
        Assert.That((loaded.AlertRecipient, loaded.WorkspaceRoot), Is.EqualTo(("ryan", @"D:\Projects\MindAttic")));

        var config = new ConfigurationBuilder()
            .AddMindAtticVaultConfiguration(s => { s.RoamingRoot = tmp.Combine("roaming"); s.LocalRoot = tmp.Combine("local"); s.ReloadOnChange = false; s.IncludeKeys = false; })
            .Build();
        Assert.That(config["MindAttic:Settings:Global:alertRecipient"], Is.EqualTo("ryan"));
        Assert.That(config["MindAttic:Settings:Global:workspaceRoot"], Is.EqualTo(@"D:\Projects\MindAttic"));
    }

    [Test]
    public void Manifest_HasThemeWorkspaceRootAndAlertRecipient_Labeled_NoSecrets()
    {
        var manifest = SettingsManifestBuilder.Build<MindAtticGlobalSettings>();
        Assert.That((manifest.AppId, manifest.DisplayName, manifest.Scope), Is.EqualTo(("Global", "Global", SettingsManifest.RoamingScope)));
        var fields = manifest.AllFields().ToDictionary(f => f.Name);
        Assert.That(fields.Keys, Is.EquivalentTo(new[]
        {
            "MindAttic:Settings:Global:theme", "MindAttic:Settings:Global:workspaceRoot", "MindAttic:Settings:Global:alertRecipient",
            "MindAttic:Settings:Global:defaultToDesktop1",
        }));
        var alert = fields["MindAttic:Settings:Global:alertRecipient"];
        Assert.That((alert.Label, alert.Group, alert.Kind), Is.EqualTo(("Alert recipient", "Notifications", SettingKind.Text)));
        Assert.That(alert.Help, Does.Contain("Psst").And.Contain("fall back"));
        Assert.That(fields["MindAttic:Settings:Global:workspaceRoot"].Label, Is.EqualTo("Workspace root"));
        var pin = fields["MindAttic:Settings:Global:defaultToDesktop1"];
        Assert.That((pin.Label, pin.Group, pin.Kind), Is.EqualTo(("Default to Desktop 1", "Windows", SettingKind.Bool)));
        foreach (var f in fields.Values)
            Assert.That(f.Name, Does.Not.Contain("key").IgnoreCase.And.Not.Contain("password").IgnoreCase.And.Not.Contain("secret").IgnoreCase, "no secrets");
    }

    [Test]
    public void LoadOrSeed_WritesTheManifestWithTheAlertRecipient()
    {
        var store = MindAtticGlobalSettings.Store();
        store.LoadOrSeed();
        var manifest = SettingsManifestFile.TryRead(Path.Combine(store.Directory, SettingsManifest.FileName));
        Assert.That(manifest, Is.Not.Null);
        Assert.That(manifest!.AllFields().Select(f => f.Name), Does.Contain("MindAttic:Settings:Global:alertRecipient"));
    }

    [TestCase(null, "ryan", "ryan", TestName = "Resolve_NullAppValue_FollowsGlobal")]
    [TestCase("", "ryan", "ryan", TestName = "Resolve_EmptyAppValue_FollowsGlobal")]
    [TestCase("   ", "ryan", "ryan", TestName = "Resolve_WhitespaceAppValue_FollowsGlobal")]
    [TestCase("920-555-0101", "ryan", "920-555-0101", TestName = "Resolve_SetAppValue_OverridesGlobal")]
    [TestCase(" jordan ", "ryan", "jordan", TestName = "Resolve_SetAppValue_IsTrimmed")]
    [TestCase("", "", null, TestName = "Resolve_BothBlank_IsNull")]
    [TestCase(null, null, null, TestName = "Resolve_BothNull_IsNull")]
    public void Resolve_AppValueIfSet_ElseGlobal(string? appValue, string? globalValue, string? expected)
    {
        var global = new MindAtticGlobalSettings { AlertRecipient = globalValue! };
        Assert.That(MindAtticGlobalSettings.Resolve(appValue, global, g => g.AlertRecipient), Is.EqualTo(expected));
    }

    [Test]
    public void Resolve_WithNoGlobalSettings_IsTheAppValueOrNull()
    {
        Assert.That(MindAtticGlobalSettings.Resolve("x", null, g => g.AlertRecipient), Is.EqualTo("x"));
        Assert.That(MindAtticGlobalSettings.Resolve("", null, g => g.AlertRecipient), Is.Null);
    }

    [Test]
    public void Resolve_ReadsTheUsersGlobalFile_OnlyWhenTheAppValueIsBlank()
    {
        Assert.That(MindAtticGlobalSettings.Resolve("", g => g.WorkspaceRoot), Is.Null, "no Global file yet: nothing to follow");
        MindAtticGlobalSettings.Store().Save(new MindAtticGlobalSettings { WorkspaceRoot = @"D:\Projects\MindAttic", AlertRecipient = "ryan" });
        Assert.That(MindAtticGlobalSettings.Resolve(null, g => g.WorkspaceRoot), Is.EqualTo(@"D:\Projects\MindAttic"));
        Assert.That(MindAtticGlobalSettings.Resolve("", g => g.AlertRecipient), Is.EqualTo("ryan"));
        Assert.That(MindAtticGlobalSettings.Resolve(@"E:\Work", g => g.WorkspaceRoot), Is.EqualTo(@"E:\Work"), "the app's override wins");

        // A Global edit (Vault.Desktop's Save) is followed at once by every app that didn't override it.
        MindAtticGlobalSettings.Store().Update(g => g.AlertRecipient = "jordan");
        Assert.That(MindAtticGlobalSettings.Resolve(null, g => g.AlertRecipient), Is.EqualTo("jordan"));
    }

    [Test]
    public void Resolve_RefusesANullSelector()
    {
        Assert.Throws<ArgumentNullException>(() => MindAtticGlobalSettings.Resolve("x", null!));
        Assert.Throws<ArgumentNullException>(() => MindAtticGlobalSettings.Resolve("x", new MindAtticGlobalSettings(), null!));
    }
}
