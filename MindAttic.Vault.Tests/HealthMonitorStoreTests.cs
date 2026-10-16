using MindAttic.Vault.Dashboard.Services;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public class HealthMonitorStoreTests
{
    private static ProviderSnapshot Snapshot(string providerId, bool isTrusted, ProviderStatus status, string? displayName = null) =>
        new()
        {
            ProviderId = providerId,
            DisplayName = displayName ?? providerId,
            IsTrusted = isTrusted,
            Status = status,
        };

    [Test]
    public void RecordUptime_Returns_Running_Percentage_Per_Provider()
    {
        var store = new HealthMonitorStore();

        Assert.That(store.RecordUptime("claude", true), Is.EqualTo(100.0));
        Assert.That(store.RecordUptime("claude", true), Is.EqualTo(100.0));
        Assert.That(store.RecordUptime("claude", false), Is.EqualTo(100.0 * 2 / 3).Within(0.0001));

        // A different provider has its own independent counter.
        Assert.That(store.RecordUptime("openai", false), Is.EqualTo(0.0));
    }

    [Test]
    public void RecordUptime_Treats_Provider_Ids_Case_Insensitively()
    {
        var store = new HealthMonitorStore();

        store.RecordUptime("Claude", true);
        var pct = store.RecordUptime("claude", false);

        Assert.That(pct, Is.EqualTo(50.0));
    }

    [Test]
    public void Previous_Returns_Null_Before_First_Commit()
    {
        var store = new HealthMonitorStore();

        Assert.That(store.Previous("claude"), Is.Null);
    }

    [Test]
    public void Commit_Replaces_Snapshots_And_Evicts_Providers_Missing_From_Fresh_Set()
    {
        var store = new HealthMonitorStore();
        store.Commit(new[]
        {
            Snapshot("claude", true, ProviderStatus.Healthy),
            Snapshot("openai", true, ProviderStatus.Healthy),
        });

        store.Commit(new[]
        {
            Snapshot("claude", true, ProviderStatus.Degraded),
        });

        Assert.That(store.Previous("claude")?.Status, Is.EqualTo(ProviderStatus.Degraded));
        Assert.That(store.Previous("openai"), Is.Null);
        Assert.That(store.Snapshots, Has.Count.EqualTo(1));
    }

    [Test]
    public void Commit_Sets_LastSweepUtc_Clears_SweepInProgress_And_Fires_OnChanged()
    {
        var store = new HealthMonitorStore();
        store.SweepInProgress = true;
        var changed = false;
        store.OnChanged += () => changed = true;

        Assert.That(store.LastSweepUtc, Is.Null);

        var before = DateTimeOffset.UtcNow;
        store.Commit(new[] { Snapshot("claude", true, ProviderStatus.Healthy) });
        var after = DateTimeOffset.UtcNow;

        Assert.That(store.LastSweepUtc, Is.Not.Null);
        Assert.That(store.LastSweepUtc!.Value, Is.InRange(before, after));
        Assert.That(store.SweepInProgress, Is.False);
        Assert.That(changed, Is.True);
    }

    [Test]
    public void Snapshots_Are_Ordered_Trusted_First_Then_Alphabetical()
    {
        var store = new HealthMonitorStore();
        store.Commit(new[]
        {
            Snapshot("zeta", false, ProviderStatus.Healthy),
            Snapshot("beta", true, ProviderStatus.Healthy),
            Snapshot("alpha", false, ProviderStatus.Healthy),
            Snapshot("gamma", true, ProviderStatus.Healthy),
        });

        Assert.That(store.Snapshots.Select(s => s.ProviderId), Is.EqualTo(new[] { "beta", "gamma", "alpha", "zeta" }));
    }

    [Test]
    public void TrustedPanelHealthy_Is_False_When_No_Trusted_Snapshots_Exist()
    {
        var store = new HealthMonitorStore();
        store.Commit(new[] { Snapshot("untrusted", false, ProviderStatus.Healthy) });

        Assert.That(store.TrustedPanelHealthy, Is.False);
    }

    [Test]
    public void TrustedPanelHealthy_Is_False_When_No_Snapshots_Exist_At_All()
    {
        var store = new HealthMonitorStore();

        Assert.That(store.TrustedPanelHealthy, Is.False);
    }

    [Test]
    public void TrustedPanelHealthy_Is_False_When_Any_Trusted_Snapshot_Is_Not_Healthy()
    {
        var store = new HealthMonitorStore();
        store.Commit(new[]
        {
            Snapshot("claude", true, ProviderStatus.Healthy),
            Snapshot("openai", true, ProviderStatus.Degraded),
        });

        Assert.That(store.TrustedPanelHealthy, Is.False);
    }

    [Test]
    public void TrustedPanelHealthy_Is_True_When_All_Trusted_Snapshots_Are_Healthy()
    {
        var store = new HealthMonitorStore();
        store.Commit(new[]
        {
            Snapshot("claude", true, ProviderStatus.Healthy),
            Snapshot("openai", true, ProviderStatus.Healthy),
            Snapshot("untrusted", false, ProviderStatus.Down),
        });

        Assert.That(store.TrustedPanelHealthy, Is.True);
    }
}
