using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace MindAttic.Vault.Configuration;

/// <summary>
/// Reads each <c>{root}/{bucket}/providers.json</c> file and projects every leaf
/// into the
/// <c>MindAttic:Vault:&lt;bucket&gt;:&lt;providerId&gt;:&lt;field&gt;</c>
/// configuration namespace. Missing files / malformed JSON resolve to
/// "no data" rather than exceptions, matching the rest of the Vault's
/// IO posture.
/// </summary>
internal sealed class MindAtticConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly MindAtticConfigurationSource source;
    private readonly List<FileSystemWatcher> watchers = new();

    public MindAtticConfigurationProvider(MindAtticConfigurationSource source)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>
    /// Reads every configured bucket's providers.json + per-provider .key files
    /// and replaces the in-memory <see cref="ConfigurationProvider.Data"/>.
    /// Idempotent — safe to call repeatedly (e.g. from <see cref="Reload"/>).
    /// </summary>
    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var bucket in source.Buckets)
        {
            // Defensive: a misconfigured Buckets array shouldn't blow up Load.
            if (string.IsNullOrWhiteSpace(bucket)) continue;
            var bucketDir = Path.Combine(source.EffectiveRoot, bucket);
            var file = Path.Combine(bucketDir, Credentials.CredentialStore.ProvidersJsonFile);

            if (File.Exists(file))
            {
                LoadProvidersJson(file, bucket, data);
            }

            // Per-provider .key override files take highest priority — write them
            // last so they overwrite any apiKey already pulled from providers.json.
            if (Directory.Exists(bucketDir))
            {
                foreach (var keyFile in Directory.EnumerateFiles(bucketDir, "*" + Credentials.CredentialStore.KeyFileExtension))
                {
                    var providerId = Path.GetFileNameWithoutExtension(keyFile);
                    if (string.IsNullOrWhiteSpace(providerId)) continue;
                    string? raw;
                    try { raw = File.ReadAllText(keyFile); }
                    catch { raw = null; }
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    var path = $"{VaultConfigurationKeys.VaultSection}:{bucket}:{providerId}:{VaultConfigurationKeys.ApiKeyProperty}";
                    data[path] = raw.Trim();
                }
            }
        }

        Data = data;

        // Wire watchers lazily — only after the first successful Load, so a Build
        // that runs before any directory exists doesn't bind to nothing.
        if (source.ReloadOnChange)
            EnsureWatchers();
    }

    /// <summary>
    /// Parses a single bucket's providers.json into the supplied sink, projecting
    /// each leaf to its colon-delimited path. Malformed JSON / IO errors are swallowed
    /// (consistent with the file-based stores).
    /// </summary>
    private static void LoadProvidersJson(string file, string bucket, IDictionary<string, string?> sink)
    {
        try
        {
            var raw = File.ReadAllText(file);
            if (string.IsNullOrWhiteSpace(raw)) return;

            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            foreach (var providerProp in doc.RootElement.EnumerateObject())
            {
                var providerId = providerProp.Name;
                // Skip non-object provider entries — schema is { id: { ... } }.
                if (providerProp.Value.ValueKind != JsonValueKind.Object) continue;

                foreach (var fieldProp in providerProp.Value.EnumerateObject())
                {
                    var path = $"{VaultConfigurationKeys.VaultSection}:{bucket}:{providerId}:{fieldProp.Name}";
                    sink[path] = JsonValueToString(fieldProp.Value);
                }
            }
        }
        catch { /* swallow malformed JSON — same as the file-based stores */ }
    }

    /// <summary>Coerces a JSON value to the string-shaped form IConfiguration expects.</summary>
    private static string? JsonValueToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l.ToString() : element.GetDouble().ToString(),
        JsonValueKind.True   => "true",
        JsonValueKind.False  => "false",
        JsonValueKind.Null   => null,
        // Objects/arrays inside provider entries are rare; surface the raw text
        // and let the consumer parse it.
        _                    => element.GetRawText()
    };

    /// <summary>
    /// Sets up one <see cref="FileSystemWatcher"/> per existing bucket directory.
    /// Watcher creation is best-effort — a bucket whose directory doesn't yet
    /// exist (or is unwatchable) is silently skipped.
    /// </summary>
    private void EnsureWatchers()
    {
        if (watchers.Count > 0) return;
        foreach (var bucket in source.Buckets)
        {
            if (string.IsNullOrWhiteSpace(bucket)) continue;
            var bucketDir = Path.Combine(source.EffectiveRoot, bucket);
            if (!Directory.Exists(bucketDir)) continue;

            try
            {
                var watcher = new FileSystemWatcher(bucketDir)
                {
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
                };
                // Any change kicks a full reload — coarse but simple, and the
                // dataset is tiny enough that a re-parse is cheap.
                watcher.Changed += (_, _) => Reload();
                watcher.Created += (_, _) => Reload();
                watcher.Deleted += (_, _) => Reload();
                watcher.Renamed += (_, _) => Reload();
                watchers.Add(watcher);
            }
            catch { /* watching is best-effort */ }
        }
    }

    private void Reload()
    {
        Load();
        OnReload();
    }

    /// <summary>Disposes every <see cref="FileSystemWatcher"/> attached to this provider.</summary>
    public void Dispose()
    {
        foreach (var w in watchers)
        {
            try { w.Dispose(); } catch { /* best-effort cleanup */ }
        }
        watchers.Clear();
    }
}
