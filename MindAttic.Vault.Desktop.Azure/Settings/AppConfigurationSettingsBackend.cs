using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Data.AppConfiguration;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Vault.Desktop.Azure.Settings;

/// <summary>
/// An app's settings in Azure App Configuration: every leaf of the document is one unlabeled key-value named
/// exactly as the deployed app's IConfiguration sees it (<see cref="SettingsKeyMap"/>). Save writes only the keys
/// whose value changed inside the edited fields, deletes the keys of removed list/collection items, and guards
/// every write with the ETag seen at Load (If-Match / If-None-Match), so a concurrent edit becomes a
/// <see cref="ConcurrencyConflictException"/> instead of a silent overwrite.
/// </summary>
public sealed class AppConfigurationSettingsBackend : ISettingsBackend
{
    /// <summary>App Configuration's "no label" filter.</summary>
    internal const string NullLabel = "\0";

    private readonly ConfigurationClient client;
    private readonly string storeName;
    private readonly object gate = new();
    private readonly Dictionary<string, Snapshot> lastLoaded = new(StringComparer.OrdinalIgnoreCase);

    public AppConfigurationSettingsBackend(ConfigurationClient client, string storeName)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.storeName = string.IsNullOrWhiteSpace(storeName) ? "App Configuration" : storeName;
    }

    public string WhenAppsSeeChanges => "after the app's App Configuration refresh interval, or its next restart";

    public async Task<SettingsDocument> LoadAsync(SettingsManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var snapshot = await ListAsync(SettingsKeyMap.PrefixOf(manifest), cancellationToken).ConfigureAwait(false);
        Remember(manifest, snapshot);
        var values = snapshot.Settings.ToDictionary(s => s.Key, s => (string?)s.Value.Value, StringComparer.Ordinal);
        return new SettingsDocument(SettingsKeyMap.Read(manifest, values), snapshot.Version);
    }

    public async Task<string?> SaveAsync(SettingsManifest manifest, SettingsDocument document, IReadOnlyCollection<string> changedFields,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(document);
        var prefix = SettingsKeyMap.PrefixOf(manifest);

        var baseline = Recall(manifest, document.Version);
        if (baseline is null)
        {
            // Not the snapshot this backend handed out (another instance, or a restart): compare versions instead.
            baseline = await ListAsync(prefix, cancellationToken).ConfigureAwait(false);
            if (document.Version is not null && document.Version != baseline.Version)
                throw new ConcurrencyConflictException(
                    $"{manifest.DisplayName}'s settings were changed in App Configuration store '{storeName}' since they were loaded. Reload to see the latest values, then save again.");
        }

        var scope = ScopeOf(manifest, changedFields ?? Array.Empty<string>());
        if (scope.Count == 0) return baseline.Version;

        foreach (var field in scope)
        {
            var path = SettingsKeyMap.PathOf(manifest, field);
            if (!SettingsKeyMap.TryGet(document.Values, field.Property, out var value)) continue;
            var desired = new Dictionary<string, string?>(StringComparer.Ordinal);
            SettingsKeyMap.Flatten(field, value, path, desired);
            var existing = baseline.Settings
                .Where(s => Under(s.Key, path))
                .ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);

            foreach (var (key, newValue) in desired)
            {
                if (existing.TryGetValue(key, out var current))
                {
                    if (current.Value == newValue && string.IsNullOrEmpty(current.ContentType)) continue;
                    var update = AzureErrors.Guarded(key, newValue, current.ETag);
                    await AzureErrors.AppConfigurationAsync(storeName, key, () =>
                        client.SetConfigurationSettingAsync(update, onlyIfUnchanged: true, cancellationToken)).ConfigureAwait(false);
                }
                else
                {
                    // Absent at Load: If-None-Match * so a key someone else just created is a conflict, not an overwrite.
                    await AzureErrors.AppConfigurationAsync(storeName, key, () =>
                        client.AddConfigurationSettingAsync(new ConfigurationSetting(key, newValue), cancellationToken)).ConfigureAwait(false);
                }
            }

            foreach (var (key, current) in existing)
            {
                if (desired.ContainsKey(key)) continue;
                await AzureErrors.AppConfigurationAsync(storeName, key, () =>
                    client.DeleteConfigurationSettingAsync(current, onlyIfUnchanged: true, cancellationToken)).ConfigureAwait(false);
            }
        }

        var after = await ListAsync(prefix, cancellationToken).ConfigureAwait(false);
        Remember(manifest, after);
        return after.Version;
    }

    /// <summary>The top-level fields that hold any of the changed names (a nested or item name selects its top field).</summary>
    internal static List<SettingField> ScopeOf(SettingsManifest manifest, IReadOnlyCollection<string> changedFields)
    {
        var scope = new List<SettingField>();
        foreach (var field in manifest.Fields)
        {
            var name = SettingsKeyMap.PathOf(manifest, field);
            if (changedFields.Any(c => c is not null && (string.Equals(c, name, StringComparison.OrdinalIgnoreCase)
                                                         || c.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))))
                scope.Add(field);
        }
        return scope;
    }

    private static bool Under(string key, string path) =>
        string.Equals(key, path, StringComparison.OrdinalIgnoreCase) || key.StartsWith(path + ":", StringComparison.OrdinalIgnoreCase);

    private async Task<Snapshot> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        var selector = new SettingSelector { KeyFilter = AzureErrors.EscapeFilter(prefix) + ":*", LabelFilter = NullLabel };
        var settings = new Dictionary<string, ConfigurationSetting>(StringComparer.Ordinal);
        await AzureErrors.AppConfigurationAsync(storeName, prefix, async () =>
        {
            await foreach (var s in client.GetConfigurationSettingsAsync(selector, cancellationToken).ConfigureAwait(false))
                if (string.IsNullOrEmpty(s.Label)) settings[s.Key] = s;
        }).ConfigureAwait(false);
        return new Snapshot(settings, VersionOf(settings.Values));
    }

    /// <summary>A short hash of every key and ETag: changes whenever any of the app's key-values changes.</summary>
    internal static string VersionOf(IEnumerable<ConfigurationSetting> settings)
    {
        var text = new StringBuilder();
        foreach (var s in settings.OrderBy(s => s.Key, StringComparer.Ordinal))
            text.Append(s.Key).Append('\0').Append(s.ETag.ToString()).Append('\n');
        return "appconfig:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    private void Remember(SettingsManifest manifest, Snapshot snapshot)
    {
        lock (gate) lastLoaded[manifest.AppId] = snapshot;
    }

    private Snapshot? Recall(SettingsManifest manifest, string? version)
    {
        if (version is null) return null;
        lock (gate) return lastLoaded.TryGetValue(manifest.AppId, out var s) && s.Version == version ? s : null;
    }

    private sealed record Snapshot(IReadOnlyDictionary<string, ConfigurationSetting> Settings, string Version);
}
