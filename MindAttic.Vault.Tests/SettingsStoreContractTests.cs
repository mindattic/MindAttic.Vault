using MindAttic.Vault.Settings;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

public sealed class ContractSettings
{
    public string Name { get; set; } = "default-name";
    public int Count { get; set; } = 7;
    public List<string> Tags { get; set; } = new() { "a" };
}

/// <summary>Behavior every <see cref="ISettingsStore{T}"/> backend must share.</summary>
public abstract class SettingsStoreContract
{
    protected abstract ISettingsStore<ContractSettings> CreateStore();
    protected abstract void Corrupt();

    [Test]
    public void Load_WhenNothingSaved_ReturnsClassDefaults()
    {
        var store = CreateStore();
        Assert.That(store.Exists(), Is.False);
        var s = store.Load();
        Assert.That(s.Name, Is.EqualTo("default-name"));
        Assert.That(s.Count, Is.EqualTo(7));
        Assert.That(store.Exists(), Is.False, "Load must not write");
    }

    [Test]
    public void LoadOrSeed_WhenNothingSaved_SavesDefaultsOnce()
    {
        var store = CreateStore();
        var seeded = store.LoadOrSeed();
        Assert.That(seeded.Count, Is.EqualTo(7));
        Assert.That(store.Exists(), Is.True);
    }

    [Test]
    public void LoadOrSeed_NeverOverwritesSavedSettings()
    {
        var store = CreateStore();
        store.Save(new ContractSettings { Name = "mine", Count = 42 });
        var loaded = store.LoadOrSeed();
        Assert.That(loaded.Name, Is.EqualTo("mine"));
        Assert.That(CreateStore().Load().Count, Is.EqualTo(42));
    }

    [Test]
    public async Task SaveThenLoad_RoundTrips()
    {
        var store = CreateStore();
        await store.SaveAsync(new ContractSettings { Name = "x", Count = 1, Tags = new() { "p", "q" } });
        var loaded = await CreateStore().LoadAsync();
        Assert.That(loaded.Name, Is.EqualTo("x"));
        Assert.That(loaded.Tags, Is.EqualTo(new[] { "p", "q" }));
    }

    [Test]
    public void Corrupt_LoadsDefaults()
    {
        var store = CreateStore();
        store.Save(new ContractSettings { Name = "x" });
        Corrupt();
        Assert.That(store.Load().Name, Is.EqualTo("default-name"));
    }

    [Test]
    public void LoadStrict_WhenNothingSaved_ReturnsDefaults()
    {
        var store = CreateStore();
        Assert.That(store.LoadStrict().Name, Is.EqualTo("default-name"));
        Assert.That(store.TryLoad(out var value), Is.True);
        Assert.That(value.Name, Is.EqualTo("default-name"));
    }

    [Test]
    public async Task LoadStrict_ReturnsSavedSettings()
    {
        var store = CreateStore();
        store.Save(new ContractSettings { Name = "saved" });
        Assert.That(store.LoadStrict().Name, Is.EqualTo("saved"));
        Assert.That((await store.LoadStrictAsync()).Name, Is.EqualTo("saved"));
        Assert.That(store.TryLoad(out var value), Is.True);
        Assert.That(value.Name, Is.EqualTo("saved"));
    }

    [Test]
    public void LoadStrict_OnCorruptSettings_ThrowsInsteadOfReturningDefaults()
    {
        var store = CreateStore();
        store.Save(new ContractSettings { Name = "x" });
        Corrupt();
        Assert.Throws<InvalidDataException>(() => store.LoadStrict());
        Assert.ThrowsAsync<InvalidDataException>(() => store.LoadStrictAsync());
        Assert.That(store.TryLoad(out var value), Is.False);
        Assert.That(value.Name, Is.EqualTo("default-name"));
    }

    [Test]
    public void Update_AppliesMutationAndPersists()
    {
        var store = CreateStore();
        store.Update(s => s.Count = 99);
        Assert.That(CreateStore().Load().Count, Is.EqualTo(99));
    }
}

[TestFixture]
public sealed class JsonSettingsStoreContractTests : SettingsStoreContract
{
    private TempDirectory tmp = null!;

    [SetUp] public void SetUp() => tmp = new TempDirectory("settings-contract");
    [TearDown] public void TearDown() => tmp.Dispose();

    protected override ISettingsStore<ContractSettings> CreateStore() => new JsonSettingsStore<ContractSettings>(tmp.Path);
    protected override void Corrupt() => File.WriteAllText(tmp.Combine("settings.json"), "{ not json");

    [Test]
    public void LoadOrSeed_OnAnUnwritableFolder_ReturnsDefaultsInsteadOfThrowing()
    {
        var blocker = tmp.Combine("blocker");
        File.WriteAllText(blocker, "a file where the settings folder should be");
        var store = new JsonSettingsStore<ContractSettings>(Path.Combine(blocker, "app"));
        Assert.That(store.LoadOrSeed().Name, Is.EqualTo("default-name"));
    }

    [Test]
    public void LoadOrSeed_LeavesNoTempFilesBehind()
    {
        CreateStore().LoadOrSeed();
        Assert.That(Directory.GetFiles(tmp.Path, "*.tmp"), Is.Empty);
    }
}
