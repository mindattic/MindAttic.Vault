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
}
