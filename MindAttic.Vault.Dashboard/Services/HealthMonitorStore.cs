using System.Collections.Concurrent;

namespace MindAttic.Vault.Dashboard.Services;

/// <summary>
/// In-memory store of the latest per-provider snapshots plus lightweight uptime
/// counters. Singleton; written by the background monitor, read by the dashboard
/// UI. Raises <see cref="OnChanged"/> after each sweep so live components refresh.
/// </summary>
public sealed class HealthMonitorStore
{
    private readonly ConcurrentDictionary<string, ProviderSnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (long checks, long healthy)> uptime = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fired (best-effort) after a sweep updates the store.</summary>
    public event Action? OnChanged;

    /// <summary>UTC time the most recent sweep completed, or null before the first sweep.</summary>
    public DateTimeOffset? LastSweepUtc { get; private set; }

    /// <summary>True while a sweep is in flight (drives the UI spinner / disables the button).</summary>
    public bool SweepInProgress { get; set; }

    /// <summary>All current snapshots, trusted first then alphabetical.</summary>
    public IReadOnlyList<ProviderSnapshot> Snapshots =>
        snapshots.Values
            .OrderByDescending(s => s.IsTrusted)
            .ThenBy(s => s.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The headline verdict: are ALL trusted providers green? This is the
    /// "can I trust the panel right now" signal the dashboard leads with.
    /// </summary>
    public bool TrustedPanelHealthy =>
        snapshots.Values.Where(s => s.IsTrusted).All(s => s.Status == ProviderStatus.Healthy)
        && snapshots.Values.Any(s => s.IsTrusted);

    /// <summary>Records one probe outcome into the uptime counters and returns the running uptime %.</summary>
    public double RecordUptime(string providerId, bool healthy)
    {
        var updated = uptime.AddOrUpdate(providerId,
            _ => (1, healthy ? 1 : 0),
            (_, cur) => (cur.checks + 1, cur.healthy + (healthy ? 1 : 0)));
        return 100.0 * updated.healthy / updated.checks;
    }

    /// <summary>Previous snapshot for a provider (used to detect state changes), or null.</summary>
    public ProviderSnapshot? Previous(string providerId) =>
        snapshots.TryGetValue(providerId, out var s) ? s : null;

    /// <summary>Replace the full snapshot set after a sweep and notify subscribers.</summary>
    public void Commit(IEnumerable<ProviderSnapshot> fresh)
    {
        var freshList = fresh.ToList();
        var freshIds = new HashSet<string>(freshList.Select(s => s.ProviderId), StringComparer.OrdinalIgnoreCase);
        foreach (var s in freshList)
            snapshots[s.ProviderId] = s;
        foreach (var staleId in snapshots.Keys.Where(k => !freshIds.Contains(k)).ToList())
            snapshots.TryRemove(staleId, out _);
        LastSweepUtc = DateTimeOffset.UtcNow;
        SweepInProgress = false;
        OnChanged?.Invoke();
    }

    /// <summary>Notify subscribers without changing data (e.g. when a sweep starts).</summary>
    public void NotifyChanged() => OnChanged?.Invoke();
}
