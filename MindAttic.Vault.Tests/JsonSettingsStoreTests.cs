using MindAttic.Vault.Settings;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public class JsonSettingsStoreTests
{
    public sealed class FakeSettings
    {
        public string ClaudeApiKey { get; set; } = "";
        public bool LlmVotingEnabled { get; set; }
        public int Threshold { get; set; } = 50;
    }

    [Test]
    public void Load_Returns_Defaults_When_File_Missing()
    {
        using var tmp = new TempDirectory();
        var store = new JsonSettingsStore<FakeSettings>(Path.Combine(tmp.Path, "missing"));
        var loaded = store.Load();

        Assert.That(loaded.ClaudeApiKey,     Is.EqualTo(""));
        Assert.That(loaded.LlmVotingEnabled, Is.False);
        Assert.That(loaded.Threshold,        Is.EqualTo(50));
    }

    [Test]
    public void Save_Then_Load_Roundtrips()
    {
        using var tmp = new TempDirectory();
        var store = new JsonSettingsStore<FakeSettings>(tmp.Path);

        store.Save(new FakeSettings { ClaudeApiKey = "sk", LlmVotingEnabled = true, Threshold = 90 });

        var loaded = store.Load();
        Assert.That(loaded.ClaudeApiKey,     Is.EqualTo("sk"));
        Assert.That(loaded.LlmVotingEnabled, Is.True);
        Assert.That(loaded.Threshold,        Is.EqualTo(90));
    }

    [Test]
    public void Load_Returns_Defaults_For_Malformed_Json()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, JsonSettingsStore<FakeSettings>.DefaultFileName),
            "{ this is not valid json");

        var store = new JsonSettingsStore<FakeSettings>(tmp.Path);
        var loaded = store.Load();
        Assert.That(loaded.ClaudeApiKey, Is.EqualTo(""));
    }

    [Test]
    public void LoadWithOverlay_Applies_Overlay_After_Load()
    {
        using var tmp = new TempDirectory();
        var store = new JsonSettingsStore<FakeSettings>(tmp.Path);
        store.Save(new FakeSettings { ClaudeApiKey = "from-file" });

        var loaded = store.LoadWithOverlay(s =>
        {
            s.ClaudeApiKey = "from-overlay";
            s.Threshold = 99;
        });

        Assert.That(loaded.ClaudeApiKey, Is.EqualTo("from-overlay"));
        Assert.That(loaded.Threshold,    Is.EqualTo(99));
    }

    [Test]
    public void Update_Read_Modify_Writes_To_Disk()
    {
        using var tmp = new TempDirectory();
        var store = new JsonSettingsStore<FakeSettings>(tmp.Path);
        store.Save(new FakeSettings { Threshold = 10 });

        var updated = store.Update(s => s.Threshold = 75);
        Assert.That(updated.Threshold, Is.EqualTo(75));

        // Round-trip from a fresh store to confirm it persisted.
        var fresh = new JsonSettingsStore<FakeSettings>(tmp.Path).Load();
        Assert.That(fresh.Threshold, Is.EqualTo(75));
    }

    [Test]
    public void Save_Creates_Missing_Directory()
    {
        using var tmp = new TempDirectory();
        var dir = Path.Combine(tmp.Path, "deep", "tree");
        var store = new JsonSettingsStore<FakeSettings>(dir);

        store.Save(new FakeSettings { Threshold = 1 });

        Assert.That(File.Exists(Path.Combine(dir, JsonSettingsStore<FakeSettings>.DefaultFileName)), Is.True);
    }

    // ── Constructor / argument validation ───────────────────────────────────────

    [Test]
    public void Constructor_Throws_For_Empty_Directory()
    {
        Assert.Throws<ArgumentException>(() => new JsonSettingsStore<FakeSettings>(""));
        Assert.Throws<ArgumentException>(() => new JsonSettingsStore<FakeSettings>("   "));
        Assert.Throws<ArgumentException>(() => new JsonSettingsStore<FakeSettings>(null!));
    }

    [Test]
    public void Constructor_Throws_For_Empty_FileName()
    {
        using var tmp = new TempDirectory();
        Assert.Throws<ArgumentException>(() => new JsonSettingsStore<FakeSettings>(tmp.Path, ""));
        Assert.Throws<ArgumentException>(() => new JsonSettingsStore<FakeSettings>(tmp.Path, "   "));
        Assert.Throws<ArgumentException>(() => new JsonSettingsStore<FakeSettings>(tmp.Path, null!));
    }

    [Test]
    public void Save_Throws_For_Null_Settings()
    {
        using var tmp = new TempDirectory();
        var store = new JsonSettingsStore<FakeSettings>(tmp.Path);
        Assert.Throws<ArgumentNullException>(() => store.Save(null!));
    }

    [Test]
    public void Update_Throws_For_Null_Mutator()
    {
        using var tmp = new TempDirectory();
        var store = new JsonSettingsStore<FakeSettings>(tmp.Path);
        Assert.Throws<ArgumentNullException>(() => store.Update(null!));
    }

    // ── Properties / factories ──────────────────────────────────────────────────

    [Test]
    public void Properties_Reflect_Constructor_Args()
    {
        using var tmp = new TempDirectory();
        var store = new JsonSettingsStore<FakeSettings>(tmp.Path, "custom.json");
        Assert.That(store.Directory, Is.EqualTo(tmp.Path));
        Assert.That(store.FileName,  Is.EqualTo("custom.json"));
        Assert.That(store.FilePath,  Is.EqualTo(Path.Combine(tmp.Path, "custom.json")));
    }

    [Test]
    public void Exists_Reflects_File_Presence()
    {
        using var tmp = new TempDirectory();
        var store = new JsonSettingsStore<FakeSettings>(tmp.Path);
        Assert.That(store.Exists(), Is.False);

        store.Save(new FakeSettings());
        Assert.That(store.Exists(), Is.True);
    }

    [Test]
    public void ForApp_Resolves_Roaming_Path()
    {
        using var tmp = new TempDirectory();
        var original = Environment.GetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.RoamingRootEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.RoamingRootEnvVar, tmp.Path);
            var store = JsonSettingsStore<FakeSettings>.ForApp("Sample");
            Assert.That(store.Directory, Is.EqualTo(Path.Combine(tmp.Path, "Sample")));
            Assert.That(store.FileName,  Is.EqualTo(JsonSettingsStore<FakeSettings>.DefaultFileName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.RoamingRootEnvVar, original);
        }
    }

    [Test]
    public void ForLocalApp_Resolves_Local_Path()
    {
        using var tmp = new TempDirectory();
        var original = Environment.GetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.LocalRootEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.LocalRootEnvVar, tmp.Path);
            var store = JsonSettingsStore<FakeSettings>.ForLocalApp("Sample");
            Assert.That(store.Directory, Is.EqualTo(Path.Combine(tmp.Path, "Sample")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.LocalRootEnvVar, original);
        }
    }

    [Test]
    public void ForBucket_Resolves_Roaming_Bucket_Path()
    {
        using var tmp = new TempDirectory();
        var original = Environment.GetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.RoamingRootEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.RoamingRootEnvVar, tmp.Path);
            var store = JsonSettingsStore<FakeSettings>.ForBucket("LLM");
            Assert.That(store.Directory, Is.EqualTo(Path.Combine(tmp.Path, "LLM")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(MindAttic.Vault.Paths.VaultPaths.RoamingRootEnvVar, original);
        }
    }

    // ── Custom JSON options + overlay ───────────────────────────────────────────

    [Test]
    public void Custom_JsonOptions_Are_Honoured_For_Round_Trip()
    {
        using var tmp = new TempDirectory();
        // Default options camelCase serialize "ClaudeApiKey" to "claudeApiKey".
        // Override to PascalCase to confirm the option flows through.
        var pascal = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        var store  = new JsonSettingsStore<FakeSettings>(tmp.Path, jsonOptions: pascal);

        store.Save(new FakeSettings { ClaudeApiKey = "x" });

        var raw = File.ReadAllText(Path.Combine(tmp.Path, JsonSettingsStore<FakeSettings>.DefaultFileName));
        Assert.That(raw, Does.Contain("\"ClaudeApiKey\""));
    }

    [Test]
    public void LoadWithOverlay_Null_Overlay_Returns_Plain_Loaded_Settings()
    {
        using var tmp = new TempDirectory();
        var store = new JsonSettingsStore<FakeSettings>(tmp.Path);
        store.Save(new FakeSettings { Threshold = 42 });

        var loaded = store.LoadWithOverlay(null!);
        Assert.That(loaded.Threshold, Is.EqualTo(42));
    }

    [Test]
    public void DefaultFileName_Is_Stable()
    {
        Assert.That(JsonSettingsStore<FakeSettings>.DefaultFileName, Is.EqualTo("settings.json"));
    }
}
