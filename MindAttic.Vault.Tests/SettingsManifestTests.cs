using System.Text.Json.Nodes;
using MindAttic.Vault.Settings;
using MindAttic.Vault.Settings.Manifest;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

public enum SampleMode { Paper, Live }

public sealed class SampleProfile
{
    [Setting("Profile name")] public string Name { get; set; } = "New profile";
    public bool Enabled { get; set; } = true;
}

public sealed class SampleNested
{
    public int Port { get; set; } = 8080;
}

[SettingsApp("SampleApp", "Sample App", Icon = "cog")]
[UsesCredential("anthropic", Required = true, Purpose = "Writes summaries")]
[UsesCredential("alpaca")]
public sealed class SampleSettings
{
    [Setting("Max loss per trade", Group = "Risk", Help = "Dollars", Order = 1)]
    [SettingRange(0, 10000)]
    public decimal MaxLossPerTrade { get; set; } = 50m;

    [Setting("Mode", Group = "Risk", Order = 2)] public SampleMode Mode { get; set; } = SampleMode.Paper;
    public bool DarkTheme { get; set; } = true;
    public int RetryCount { get; set; } = 3;
    [PathPicker(PathKind.Folder)] public string DataFolder { get; set; } = @"C:\Data";
    [UrlSetting] public string Endpoint { get; set; } = "https://example.com";
    [Multiline] public string Notes { get; set; } = "";
    [SettingChoices("small", "large")] public string Size { get; set; } = "small";
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);
    public List<string> Symbols { get; set; } = new() { "SPY" };
    public Dictionary<string, string> Aliases { get; set; } = new();
    public SampleNested Server { get; set; } = new();
    [SettingCollection(ItemName = "profile")] public List<SampleProfile> Profiles { get; set; } = new();
    [SettingIgnore] public string Hidden { get; set; } = "";
    public string Computed => "x";
    [Setting("Local db", NotSecret = true)] public string ConnectionString { get; set; } = "Data Source=app.db";
}

[SettingsApp("Leaky", "Leaky")]
public sealed class LeakySettings
{
    public string ClaudeApiKey { get; set; } = "";
}

public sealed class NoAttributeSettings { public int X { get; set; } }

[TestFixture]
public sealed class SettingsManifestTests
{
    private static SettingsManifest Build() => SettingsManifestBuilder.Build<SampleSettings>();
    private static SettingField F(string prop) => Build().Fields.Single(f => f.Property == prop);

    [Test]
    public void AppMetadataAndPrefix()
    {
        var m = Build();
        Assert.That(m.AppId, Is.EqualTo("SampleApp"));
        Assert.That(m.DisplayName, Is.EqualTo("Sample App"));
        Assert.That(m.Icon, Is.EqualTo("cog"));
        Assert.That(m.SchemaVersion, Is.EqualTo(SettingsManifest.CurrentSchemaVersion));
        Assert.That(m.ConfigurationPrefix, Is.EqualTo("MindAttic:Settings:SampleApp"));
    }

    [Test]
    public void FieldNamesAreFullConfigurationPathsInCamelCase()
    {
        Assert.That(F("maxLossPerTrade").Name, Is.EqualTo("MindAttic:Settings:SampleApp:maxLossPerTrade"));
        Assert.That(F("server").Fields!.Single().Name, Is.EqualTo("MindAttic:Settings:SampleApp:server:port"));
        Assert.That(F("profiles").Fields![0].Name, Is.EqualTo("MindAttic:Settings:SampleApp:profiles:*:name"));
    }

    [TestCase("maxLossPerTrade", SettingKind.Decimal)]
    [TestCase("mode", SettingKind.Choice)]
    [TestCase("darkTheme", SettingKind.Bool)]
    [TestCase("retryCount", SettingKind.Integer)]
    [TestCase("dataFolder", SettingKind.Path)]
    [TestCase("endpoint", SettingKind.Url)]
    [TestCase("notes", SettingKind.Multiline)]
    [TestCase("size", SettingKind.Choice)]
    [TestCase("pollInterval", SettingKind.TimeSpan)]
    [TestCase("symbols", SettingKind.StringList)]
    [TestCase("aliases", SettingKind.Dictionary)]
    [TestCase("server", SettingKind.Object)]
    [TestCase("profiles", SettingKind.Collection)]
    [TestCase("connectionString", SettingKind.Text)]
    public void KindIsInferred(string prop, SettingKind kind) => Assert.That(F(prop).Kind, Is.EqualTo(kind));

    [Test]
    public void IgnoredAndComputedPropertiesAreLeftOut()
    {
        var props = Build().Fields.Select(f => f.Property).ToList();
        Assert.That(props, Does.Not.Contain("hidden"));
        Assert.That(props, Does.Not.Contain("computed"));
    }

    [Test]
    public void DefaultsComeFromTheClassInitializers()
    {
        Assert.That(F("maxLossPerTrade").Default!.GetValue<decimal>(), Is.EqualTo(50m));
        Assert.That(F("retryCount").Default!.GetValue<int>(), Is.EqualTo(3));
        Assert.That(F("symbols").Default!.AsArray().Select(n => n!.GetValue<string>()), Is.EqualTo(new[] { "SPY" }));
        Assert.That(F("server").Fields!.Single().Default!.GetValue<int>(), Is.EqualTo(8080));
    }

    [Test]
    public void LabelsGroupsHelpOrderAndRange()
    {
        var f = F("maxLossPerTrade");
        Assert.That((f.Label, f.Group, f.Help, f.Order), Is.EqualTo(("Max loss per trade", "Risk", "Dollars", 1)));
        Assert.That((f.Min, f.Max), Is.EqualTo(((double?)0, (double?)10000)));
        Assert.That(F("darkTheme").Label, Is.EqualTo("Dark theme"));
        Assert.That(F("darkTheme").Group, Is.EqualTo(SettingsManifest.DefaultGroup));
    }

    [Test]
    public void EnumChoicesStoreTheSerializedValue()
    {
        var choices = F("mode").Choices!;
        Assert.That(choices.Select(c => c.Label), Is.EqualTo(new[] { "Paper", "Live" }));
        Assert.That(choices[1].Value!.ToJsonString(), Is.EqualTo("1"));
    }

    [Test]
    public void CollectionDescribesItemSchemaLabelAndDefault()
    {
        var f = F("profiles");
        Assert.That(f.ItemName, Is.EqualTo("profile"));
        Assert.That(f.ItemLabel, Is.EqualTo("name"));
        Assert.That(f.Fields!.Select(x => x.Property), Is.EqualTo(new[] { "name", "enabled" }));
        Assert.That(f.ItemDefault!["name"]!.GetValue<string>(), Is.EqualTo("New profile"));
    }

    [Test]
    public void CredentialsAreNormalizedAndCarryNoValues()
    {
        var creds = Build().Credentials;
        Assert.That(creds.Select(c => c.ProviderId), Is.EqualTo(new[] { "claude", "alpaca" }));
        Assert.That(creds[0].Required, Is.True);
        Assert.That(creds[0].Purpose, Is.EqualTo("Writes summaries"));
    }

    [Test]
    public void SecretLookingPropertyIsRefused() =>
        Assert.Throws<SettingsManifestException>(() => SettingsManifestBuilder.Build<LeakySettings>());

    [Test]
    public void ClassWithoutAttributeIsRefused() =>
        Assert.Throws<SettingsManifestException>(() => SettingsManifestBuilder.Build<NoAttributeSettings>());

    [Test]
    public void FindLocatesNestedFieldsCaseInsensitively() =>
        Assert.That(Build().Find("mindattic:settings:sampleapp:SERVER:port")?.Property, Is.EqualTo("port"));

    [TestCase("MaxLossPerTrade", "Max loss per trade")]
    [TestCase("UseHTTPProxy", "Use HTTP proxy")]
    [TestCase("retry_count", "Retry count")]
    public void Humanize(string input, string expected) =>
        Assert.That(SettingsManifestBuilder.Humanize(input), Is.EqualTo(expected));

    [Test]
    public void WriteIfChanged_WritesOnceThenSkipsUnchanged()
    {
        using var tmp = new TempDirectory("manifest");
        Assert.That(SettingsManifestFile.WriteIfChanged(tmp.Path, Build()), Is.True);
        Assert.That(SettingsManifestFile.WriteIfChanged(tmp.Path, Build()), Is.False);
        var read = SettingsManifestFile.TryRead(tmp.Combine(SettingsManifest.FileName));
        Assert.That(read!.Fields.Count, Is.EqualTo(Build().Fields.Count));
        Assert.That(Directory.GetFiles(tmp.Path, "*.tmp"), Is.Empty);
    }

    [Test]
    public void TryRead_RejectsGarbageOversizeAndFutureSchema()
    {
        using var tmp = new TempDirectory("manifest");
        var path = tmp.Combine(SettingsManifest.FileName);
        File.WriteAllText(path, "{ nope");
        Assert.That(SettingsManifestFile.TryRead(path), Is.Null);
        File.WriteAllText(path, new string(' ', SettingsManifestFile.MaxBytes + 1));
        Assert.That(SettingsManifestFile.TryRead(path), Is.Null);
        var future = Build();
        future.SchemaVersion = 99;
        File.WriteAllText(path, SettingsManifestFile.Serialize(future));
        Assert.That(SettingsManifestFile.TryRead(path), Is.Null);
    }

    [Test]
    public void LoadOrSeed_WritesManifestBesideSettings()
    {
        using var tmp = new TempDirectory("manifest");
        var store = new JsonSettingsStore<SampleSettings>(tmp.Path);
        store.LoadOrSeed();
        Assert.That(File.Exists(tmp.Combine("settings.json")), Is.True);
        Assert.That(SettingsManifestFile.TryRead(tmp.Combine(SettingsManifest.FileName))!.AppId, Is.EqualTo("SampleApp"));
    }

    [Test]
    public void LoadOrSeed_WithoutAttribute_WritesNoManifest()
    {
        using var tmp = new TempDirectory("manifest");
        new JsonSettingsStore<ContractSettings>(tmp.Path).LoadOrSeed();
        Assert.That(File.Exists(tmp.Combine(SettingsManifest.FileName)), Is.False);
    }

    [Test]
    public void SettingsJsonPathsMatchManifestNames()
    {
        // Every manifest leaf name, minus the prefix, is a path that exists in the seeded settings.json.
        using var tmp = new TempDirectory("manifest");
        new JsonSettingsStore<SampleSettings>(tmp.Path).LoadOrSeed();
        var json = JsonNode.Parse(File.ReadAllText(tmp.Combine("settings.json")))!;
        var m = Build();
        foreach (var f in m.Fields)
        {
            var relative = f.Name[(m.ConfigurationPrefix.Length + 1)..];
            Assert.That(json.AsObject().ContainsKey(relative), Is.True, relative);
        }
    }

    [Test]
    public void DiscoverFindsEveryAppFolderWithAManifest()
    {
        using var tmp = new TempDirectory("manifest");
        SettingsManifestFile.WriteIfChanged(tmp.Combine("SampleApp"), Build());
        Directory.CreateDirectory(tmp.Combine("NoManifest"));
        var found = SettingsManifestFile.Discover(tmp.Path);
        Assert.That(found.Select(f => f.Manifest.AppId), Is.EqualTo(new[] { "SampleApp" }));
    }

    [Test]
    public void Command_IgnoresArgsWithoutSwitch() =>
        Assert.That(SettingsManifestCommand.TryRun<SampleSettings>(new[] { "--other" }), Is.False);

    [Test]
    public void Command_SeedsAndWritesIntoTheAppFolder()
    {
        using var tmp = new TempDirectory("manifest");
        var original = Environment.GetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.RoamingRootEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.RoamingRootEnvVar, tmp.Path);
            var output = new StringWriter();
            Assert.That(SettingsManifestCommand.TryRun<SampleSettings>(new[] { SettingsManifestCommand.Switch }, output), Is.True);
            Assert.That(File.Exists(tmp.Combine("SampleApp", SettingsManifest.FileName)), Is.True);
            Assert.That(File.Exists(tmp.Combine("SampleApp", "settings.json")), Is.True);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.RoamingRootEnvVar, original);
        }
    }
}
