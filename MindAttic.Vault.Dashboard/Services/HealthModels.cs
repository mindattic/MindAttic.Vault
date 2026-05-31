using MindAttic.Legion;

namespace MindAttic.Vault.Dashboard.Services;

/// <summary>Traffic-light status for a single provider.</summary>
public enum ProviderStatus
{
    /// <summary>Not yet probed this run.</summary>
    Unknown,
    /// <summary>Authenticated and replied correctly — green.</summary>
    Healthy,
    /// <summary>Authenticated but the reply drifted, or it self-healed this sweep — amber.</summary>
    Degraded,
    /// <summary>Unreachable / key rejected / quota / deprecated model — red.</summary>
    Down,
}

/// <summary>
/// Immutable snapshot of one provider's health at a point in time. Rendered by
/// the dashboard and compared sweep-to-sweep to detect state changes for alerting.
/// </summary>
public sealed record ProviderSnapshot
{
    public required string ProviderId { get; init; }
    public required string DisplayName { get; init; }
    public required bool IsTrusted { get; init; }
    public required ProviderStatus Status { get; init; }
    public LlmHealthDiagnosis Diagnosis { get; init; } = LlmHealthDiagnosis.Unknown;
    public int? HttpStatusCode { get; init; }
    public string Detail { get; init; } = "";
    public string? Reply { get; init; }
    public long LatencyMs { get; init; }
    public DateTimeOffset LastCheckedUtc { get; init; }
    public int ConsecutiveFailures { get; init; }
    public double UptimePercent { get; init; }
    public string? SelfHealNote { get; init; }
    public string DashboardUrl { get; init; } = "";
    public string KeysUrl { get; init; } = "";
}

/// <summary>Configuration for the scheduled monitor, bound from the <c>Monitor</c> config section.</summary>
public sealed class MonitorOptions
{
    /// <summary>How often the background sweep runs. Default hourly (your "fix itself inside an hour").</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Per-provider probe timeout.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The trusted voting panel — these gate the overall confidence verdict.</summary>
    public string[] TrustedProviders { get; set; } = { "claude", "openai", "gemini", "deepseek" };

    /// <summary>When true, also probe every other keyed provider in the Vault (shown informationally).</summary>
    public bool MonitorAllKeyed { get; set; } = true;

    /// <summary>When true, auto-repoint a provider to a live model on a NotFound (deprecated-model) diagnosis.</summary>
    public bool SelfHealModels { get; set; } = true;

    /// <summary>SMTP email alert config. Null disables email.</summary>
    public EmailOptions? Email { get; set; }

    /// <summary>Webhook URLs that receive a JSON POST on state change. Empty disables webhooks.</summary>
    public string[] Webhooks { get; set; } = Array.Empty<string>();
}

/// <summary>SMTP settings for the email alert channel.</summary>
public sealed class EmailOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "";
    public string[] To { get; set; } = Array.Empty<string>();
}

/// <summary>A provider's status changed between sweeps — the trigger for an alert.</summary>
public sealed record AlertEvent(
    string ProviderId,
    string DisplayName,
    ProviderStatus Status,
    ProviderStatus Previous,
    LlmHealthDiagnosis Diagnosis,
    string Detail,
    DateTimeOffset AtUtc);
