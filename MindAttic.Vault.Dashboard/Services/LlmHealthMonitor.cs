using Microsoft.Extensions.Options;
using MindAttic.Legion;

namespace MindAttic.Vault.Dashboard.Services;

/// <summary>
/// Runs one full health sweep: probe every monitored provider through Legion's
/// <see cref="LlmHealthCheck"/>, auto-repoint deprecated models, raise alerts on
/// state changes, and commit fresh snapshots to the <see cref="HealthMonitorStore"/>.
/// Invoked by the hourly <see cref="MonitorBackgroundService"/> and by the
/// dashboard's "Re-check now" button.
/// </summary>
public sealed class LlmHealthMonitor
{
    private readonly MonitorOptions options;
    private readonly HealthMonitorStore store;
    private readonly SelfHealer selfHealer;
    private readonly AlertDispatcher alerts;
    private readonly IHttpClientFactory httpFactory;
    private readonly ILogger<LlmHealthMonitor> log;
    private readonly SemaphoreSlim gate = new(1, 1);

    public LlmHealthMonitor(
        IOptions<MonitorOptions> options,
        HealthMonitorStore store,
        SelfHealer selfHealer,
        AlertDispatcher alerts,
        IHttpClientFactory httpFactory,
        ILogger<LlmHealthMonitor> log)
    {
        this.options = options.Value;
        this.store = store;
        this.selfHealer = selfHealer;
        this.alerts = alerts;
        this.httpFactory = httpFactory;
        this.log = log;
    }

    /// <summary>Probe everything once. Serialized — overlapping sweeps wait their turn.</summary>
    public async Task RunSweepAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            store.SweepInProgress = true;
            store.NotifyChanged();

            var trusted = new HashSet<string>(options.TrustedProviders, StringComparer.OrdinalIgnoreCase);
            var ids = ResolveProviderIds(trusted);

            var http = httpFactory.CreateClient();
            var client = new LegionClient(http, LegionClientOptions.NoResilience);
            var health = new LlmHealthCheck(client);

            var results = await health.CheckAsync(ids, options.ProbeTimeout, ct);
            var fresh = new List<ProviderSnapshot>(results.Count);

            foreach (var r in results)
            {
                var isTrusted = trusted.Contains(r.ProviderId);
                string? selfHealNote = null;

                // Self-heal the one safely-fixable failure: a deprecated model.
                if (options.SelfHealModels && r.Diagnosis == LlmHealthDiagnosis.NotFound)
                    selfHealNote = await selfHealer.TryRepointModelAsync(r.ProviderId, options.ProbeTimeout, ct);

                var status = Classify(r, selfHealNote is not null);
                var uptime = store.RecordUptime(r.ProviderId, r.IsHealthy);

                var snapshot = new ProviderSnapshot
                {
                    ProviderId          = r.ProviderId,
                    DisplayName         = r.DisplayName,
                    IsTrusted           = isTrusted,
                    Status              = status,
                    Diagnosis           = r.Diagnosis,
                    HttpStatusCode      = r.HttpStatusCode,
                    Detail              = r.IsHealthy ? (r.RespondedCorrectly ? "Online and responding." : r.ActionableMessage)
                                                      : r.ActionableMessage,
                    Reply               = r.Response?.Trim(),
                    LatencyMs           = r.ElapsedMilliseconds,
                    LastCheckedUtc      = DateTimeOffset.UtcNow,
                    ConsecutiveFailures = NextFailureCount(r.ProviderId, r.IsHealthy),
                    UptimePercent       = uptime,
                    SelfHealNote        = selfHealNote,
                    DashboardUrl        = r.DashboardUrl,
                    KeysUrl             = r.KeysUrl,
                };

                await MaybeAlertAsync(snapshot, ct);
                fresh.Add(snapshot);
            }

            store.Commit(fresh);
            log.LogInformation("sweep complete: {Healthy}/{Total} healthy, trusted-panel {Verdict}",
                fresh.Count(s => s.Status == ProviderStatus.Healthy), fresh.Count,
                store.TrustedPanelHealthy ? "GREEN" : "DEGRADED");
        }
        finally
        {
            store.SweepInProgress = false;
            gate.Release();
        }
    }

    /// <summary>Trusted four always; plus every other keyed+supported provider when enabled.</summary>
    private IReadOnlyList<string> ResolveProviderIds(HashSet<string> trusted)
    {
        var ids = new List<string>(trusted);
        if (options.MonitorAllKeyed)
        {
            var keyed = MindAtticCredentialStore.ListProviders()
                .Where(LegionClient.IsSupported)
                .Where(id => !trusted.Contains(id));
            ids.AddRange(keyed);
        }
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static ProviderStatus Classify(LlmHealthResult r, bool selfHealedThisSweep)
    {
        if (!r.IsHealthy) return ProviderStatus.Down;
        if (selfHealedThisSweep || !r.RespondedCorrectly) return ProviderStatus.Degraded;
        return ProviderStatus.Healthy;
    }

    private int NextFailureCount(string providerId, bool healthy)
    {
        if (healthy) return 0;
        var prev = store.Previous(providerId)?.ConsecutiveFailures ?? 0;
        return prev + 1;
    }

    /// <summary>Alert only on a transition into or out of <see cref="ProviderStatus.Down"/>.</summary>
    private async Task MaybeAlertAsync(ProviderSnapshot now, CancellationToken ct)
    {
        var prev = store.Previous(now.ProviderId)?.Status ?? ProviderStatus.Unknown;
        var wasDown = prev == ProviderStatus.Down;
        var isDown = now.Status == ProviderStatus.Down;

        // Fire on down-transition and on recovery; stay quiet while steady.
        if (prev != ProviderStatus.Unknown && wasDown != isDown)
        {
            var e = new AlertEvent(now.ProviderId, now.DisplayName, now.Status, prev,
                now.Diagnosis, now.Detail, now.LastCheckedUtc);
            await alerts.DispatchAsync(e, ct);
        }
    }
}
