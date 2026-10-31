using System.Globalization;
using Azure;
using Azure.Data.AppConfiguration;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Profiles;

namespace MindAttic.Vault.Desktop.Azure.Keys;

/// <summary>
/// The shared named keys and app assignments on Azure. <b>Names live in App Configuration, values in Key Vault</b>:
/// <code>
/// MindAttic:Vault:{bucket}:{provider}:keys:{name}   Key Vault reference → secret llm-{provider}-{slug}-{hash}
/// MindAttic:Vault:{bucket}:{provider}:keyOrder:{i}  key name (shared failover order)
/// MindAttic:Vault:{bucket}:{provider}:disabledKeys:{i}
/// MindAttic:Vault:Assignments:{AppId}:{provider}:{i} key name (first = the app's active key)
/// </code>
/// exactly as <see cref="ConfigurationKeySource"/> reads them in a deployed app. Each secret is tagged
/// <c>name</c> (the readable key name) and <c>provider</c>. Every App Configuration write carries the ETag it read
/// (If-Match, or If-None-Match for a new key), so concurrent edits surface as <see cref="ConcurrencyConflictException"/>.
/// Writes go to the canonical provider id; keys stored under an alias (<c>anthropic</c>) are listed but must be
/// moved before they can be edited here.
/// </summary>
public sealed class KeyVaultKeyBackend : IKeyBackend
{
    private const int MaxSecretNameAttempts = 32;

    private readonly ConfigurationClient config;
    private readonly SecretClient secrets;
    private readonly Func<Uri, SecretClient> otherVault;
    private readonly string storeName;
    private readonly string vaultName;

    public KeyVaultKeyBackend(ConfigurationClient config, SecretClient secrets, string storeName, string vaultName,
        Func<Uri, SecretClient>? otherVault = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        this.storeName = string.IsNullOrWhiteSpace(storeName) ? "App Configuration" : storeName;
        this.vaultName = string.IsNullOrWhiteSpace(vaultName) ? secrets.VaultUri.Host : vaultName;
        this.otherVault = otherVault ?? (uri => throw new InvalidOperationException(
            $"A key references a secret in another Key Vault ({uri.Host}); only '{this.vaultName}' is configured for this environment."));
    }

    // ------------------------------------------------------------------ reads

    public async Task<IReadOnlyList<string>> ProvidersWithKeysAsync(CancellationToken cancellationToken = default)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in await ListAsync(VaultConfigurationKeys.VaultSection, cancellationToken).ConfigureAwait(false))
        {
            // MindAttic:Vault:{bucket}:{provider}:keys:{name}
            var parts = setting.Key.Split(':', 6);
            if (parts.Length < 6 || !string.Equals(parts[4], LampNames.KeysSegment, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(parts[2], "Assignments", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(parts[3])) continue;
            var canonical = ProviderCatalog.Normalize(parts[3]);
            if (string.Equals(ProviderCatalog.Get(canonical).Bucket, parts[2], StringComparison.OrdinalIgnoreCase)) ids.Add(canonical);
        }
        return ids.OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The provider's keys in failover order with their values from Key Vault (aliases merged as deployed apps see them).</summary>
    public async Task<IReadOnlyList<NamedKey>> GetKeysAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var info = ProviderCatalog.Get(providerId);
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ProviderCatalog.IdsFor(info.Id))
        {
            var section = $"{VaultConfigurationKeys.VaultSection}:{info.Bucket}:{id}";
            foreach (var setting in await ListAsync(section, cancellationToken).ConfigureAwait(false))
                data[setting.Key] = KeyVaultReference.TryParse(setting) is { } reference
                    ? await ResolveAsync(reference, cancellationToken).ConfigureAwait(false)
                    : setting.Value;
        }
        var view = new ConfigurationBuilder().AddInMemoryCollection(data).Build();
        return new ConfigurationKeySource(view).GetKeys(info.Id);
    }

    public async Task<IReadOnlyList<string>> GetAssignmentAsync(string appId, string providerId, CancellationToken cancellationToken = default)
    {
        var settings = await ListAsync(LampNames.Assignment(RequiredApp(appId), providerId), cancellationToken).ConfigureAwait(false);
        return IndexedValues(settings, LampNames.Assignment(appId, providerId))
            .Select(s => s.Value?.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> AppsUsingAsync(string providerId, string name, CancellationToken cancellationToken = default)
    {
        var provider = ProviderCatalog.Normalize(providerId);
        var trimmed = name?.Trim() ?? "";
        return (await AssignmentsAsync(cancellationToken).ConfigureAwait(false))
            .Where(a => a.Provider == provider && string.Equals(a.Setting.Value?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.App)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ------------------------------------------------------------------ writes

    public async Task AddAsync(string providerId, string name, string value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Key value is required.", nameof(value));
        var state = await LoadAsync(providerId, cancellationToken).ConfigureAwait(false);
        var clean = NamedKeyStore.ValidateName(name, state.AllNames.Select(n => new NamedKey(n, "")), except: null);

        var reference = await CreateSecretAsync(state, clean, value.Trim(), cancellationToken).ConfigureAwait(false);
        var key = LampNames.Key(state.Provider, clean);
        try
        {
            await AzureErrors.AppConfigurationAsync(storeName, key, () =>
                config.AddConfigurationSettingAsync(reference.ToSetting(key), cancellationToken)).ConfigureAwait(false);
        }
        catch
        {
            // Don't leave an orphan secret behind when the name couldn't be stored.
            await TryDeleteSecretAsync(reference.SecretName, cancellationToken).ConfigureAwait(false);
            throw;
        }

        var order = state.Order.ToList();
        order.Add(clean);
        await WriteListAsync(state.OrderPath, order, state.OrderSettings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a new version of the key's secret; its name, position and assignments stay as they are.</summary>
    public async Task SetValueAsync(string providerId, string name, string value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Key value is required.", nameof(value));
        var state = await LoadAsync(providerId, cancellationToken).ConfigureAwait(false);
        var (actual, setting) = state.Find(name);

        if (KeyVaultReference.TryParse(setting) is { } reference)
        {
            var client = ClientFor(reference);
            var secret = Tagged(new KeyVaultSecret(reference.SecretName, value.Trim()), state.Provider, actual);
            await AzureErrors.KeyVaultAsync(vaultName, () => client.SetSecretAsync(secret, cancellationToken)).ConfigureAwait(false);
            return;
        }

        // A plain value someone typed into App Configuration: move it into Key Vault behind a reference.
        var created = await CreateSecretAsync(state, actual, value.Trim(), cancellationToken).ConfigureAwait(false);
        var update = AzureErrors.Guarded(setting.Key, created.ToJson(), setting.ETag, KeyVaultReference.ContentType);
        await AzureErrors.AppConfigurationAsync(storeName, setting.Key, () =>
            config.SetConfigurationSettingAsync(update, onlyIfUnchanged: true, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Renames the key: the App Configuration key, the secret's <c>name</c> tag, the order/disabled lists and every app assignment. The secret itself is kept.</summary>
    public async Task RenameAsync(string providerId, string oldName, string newName, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(providerId, cancellationToken).ConfigureAwait(false);
        var (actual, setting) = state.Find(oldName);
        var clean = NamedKeyStore.ValidateName(newName, state.AllNames.Select(n => new NamedKey(n, "")), except: actual);
        if (string.Equals(actual, clean, StringComparison.Ordinal)) return;

        var newKey = LampNames.Key(state.Provider, clean);
        var reference = KeyVaultReference.TryParse(setting);
        var copy = reference?.ToSetting(newKey) ?? new ConfigurationSetting(newKey, setting.Value) { ContentType = setting.ContentType };
        await AzureErrors.AppConfigurationAsync(storeName, newKey, () => config.AddConfigurationSettingAsync(copy, cancellationToken)).ConfigureAwait(false);
        await AzureErrors.AppConfigurationAsync(storeName, setting.Key, () =>
            config.DeleteConfigurationSettingAsync(setting, onlyIfUnchanged: true, cancellationToken)).ConfigureAwait(false);

        if (reference is not null)
        {
            var client = ClientFor(reference);
            try
            {
                var current = await AzureErrors.KeyVaultAsync(vaultName, () => client.GetSecretAsync(reference.SecretName, cancellationToken: cancellationToken)).ConfigureAwait(false);
                var properties = current.Value.Properties;
                properties.Tags[SecretTags.Name] = clean;
                properties.Tags[SecretTags.Provider] = state.Provider;
                await AzureErrors.KeyVaultAsync(vaultName, () => client.UpdateSecretPropertiesAsync(properties, cancellationToken)).ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // The secret is gone; the reference still moved with the name.
            }
        }

        await WriteListAsync(state.OrderPath, Replace(state.Order, actual, clean), state.OrderSettings, cancellationToken).ConfigureAwait(false);
        await WriteListAsync(state.DisabledPath, Replace(state.Disabled, actual, clean), state.DisabledSettings, cancellationToken).ConfigureAwait(false);

        foreach (var a in await AssignmentsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (a.Provider != state.Provider || !string.Equals(a.Setting.Value?.Trim(), actual, StringComparison.OrdinalIgnoreCase)) continue;
            var update = AzureErrors.Guarded(a.Setting.Key, clean, a.Setting.ETag);
            await AzureErrors.AppConfigurationAsync(storeName, a.Setting.Key, () =>
                config.SetConfigurationSettingAsync(update, onlyIfUnchanged: true, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>Removes the key (App Configuration key deleted, secret soft-deleted). Refused while any app has it assigned.</summary>
    public async Task RemoveAsync(string providerId, string name, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(providerId, cancellationToken).ConfigureAwait(false);
        var (actual, setting) = state.Find(name);
        var apps = await AppsUsingAsync(state.Provider, actual, cancellationToken).ConfigureAwait(false);
        if (apps.Count > 0) throw new KeyInUseException(state.Provider, actual, apps);

        await AzureErrors.AppConfigurationAsync(storeName, setting.Key, () =>
            config.DeleteConfigurationSettingAsync(setting, onlyIfUnchanged: true, cancellationToken)).ConfigureAwait(false);
        if (KeyVaultReference.TryParse(setting) is { } reference)
        {
            var client = ClientFor(reference);
            try
            {
                await AzureErrors.KeyVaultAsync(vaultName, () => client.StartDeleteSecretAsync(reference.SecretName, cancellationToken)).ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Already gone.
            }
        }

        await WriteListAsync(state.OrderPath, state.Order.Where(n => !Same(n, actual)).ToList(), state.OrderSettings, cancellationToken).ConfigureAwait(false);
        await WriteListAsync(state.DisabledPath, state.Disabled.Where(n => !Same(n, actual)).ToList(), state.DisabledSettings, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetDisabledAsync(string providerId, string name, bool disabled, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(providerId, cancellationToken).ConfigureAwait(false);
        var (actual, _) = state.Find(name);
        var list = state.Disabled.Where(n => !Same(n, actual)).ToList();
        if (disabled) list.Add(actual);
        await WriteListAsync(state.DisabledPath, list, state.DisabledSettings, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReorderAsync(string providerId, IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        var state = await LoadAsync(providerId, cancellationToken).ConfigureAwait(false);
        if (names.Count != state.Order.Count || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Order.Count)
            throw new ArgumentException("Reorder must name every key exactly once.", nameof(names));
        var ordered = names.Select(n => state.Find(n).Name).ToList();
        await WriteListAsync(state.OrderPath, ordered, state.OrderSettings, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetAssignmentAsync(string appId, string providerId, IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        var path = LampNames.Assignment(RequiredApp(appId), providerId);
        var clean = (names ?? Array.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var existing = await ListAsync(path, cancellationToken).ConfigureAwait(false);
        await WriteListAsync(path, clean, IndexedValues(existing, path), cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ internals

    private async Task<KeyVaultReference> CreateSecretAsync(ProviderState state, string keyName, string value, CancellationToken cancellationToken)
    {
        var taken = new HashSet<string>(state.SecretNames, StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; attempt < MaxSecretNameAttempts; attempt++)
        {
            var candidate = SecretNames.Generate(state.Provider, keyName, attempt);
            if (taken.Contains(candidate)) continue;
            if (await SecretExistsAsync(candidate, cancellationToken).ConfigureAwait(false)) continue;
            try
            {
                var secret = Tagged(new KeyVaultSecret(candidate, value), state.Provider, keyName);
                await AzureErrors.KeyVaultAsync(vaultName, () => secrets.SetSecretAsync(secret, cancellationToken)).ConfigureAwait(false);
                return new KeyVaultReference(secrets.VaultUri, candidate);
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                // A soft-deleted secret holds the name until it's purged; try the next one.
            }
        }
        throw new InvalidOperationException($"Couldn't find a free Key Vault secret name for key '{keyName}' ({state.Provider}) in '{vaultName}'.");
    }

    private async Task<bool> SecretExistsAsync(string secretName, CancellationToken cancellationToken)
    {
        try
        {
            // An unreferenced secret with this name (an orphan) must not get a new version under another key.
            await AzureErrors.KeyVaultAsync(vaultName, () => secrets.GetSecretAsync(secretName, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    private async Task TryDeleteSecretAsync(string secretName, CancellationToken cancellationToken)
    {
        try { await secrets.StartDeleteSecretAsync(secretName, cancellationToken).ConfigureAwait(false); }
        catch (RequestFailedException) { /* best effort */ }
    }

    private async Task<string?> ResolveAsync(KeyVaultReference reference, CancellationToken cancellationToken)
    {
        try
        {
            var client = ClientFor(reference);
            var secret = await AzureErrors.KeyVaultAsync(vaultName, () =>
                client.GetSecretAsync(reference.SecretName, reference.Version, cancellationToken)).ConfigureAwait(false);
            return secret.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // A reference to a deleted secret: the key has no value, as a deployed app would see it.
            return null;
        }
    }

    private SecretClient ClientFor(KeyVaultReference reference) =>
        string.Equals(reference.VaultUri.Host, secrets.VaultUri.Host, StringComparison.OrdinalIgnoreCase) ? secrets : otherVault(reference.VaultUri);

    private static KeyVaultSecret Tagged(KeyVaultSecret secret, string provider, string keyName)
    {
        secret.Properties.Tags[SecretTags.Name] = keyName;
        secret.Properties.Tags[SecretTags.Provider] = provider;
        return secret;
    }

    /// <summary>Writes an indexed list (<c>{path}:0..n-1</c>), touching only entries that changed and deleting the surplus.</summary>
    private async Task WriteListAsync(string path, IReadOnlyList<string> names, IReadOnlyList<ConfigurationSetting> existing, CancellationToken cancellationToken)
    {
        var byKey = existing.ToDictionary(s => s.Key, StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
        {
            var key = $"{path}:{i.ToString(CultureInfo.InvariantCulture)}";
            if (byKey.Remove(key, out var current))
            {
                if (current.Value == names[i] && string.IsNullOrEmpty(current.ContentType)) continue;
                var update = AzureErrors.Guarded(key, names[i], current.ETag);
                await AzureErrors.AppConfigurationAsync(storeName, key, () =>
                    config.SetConfigurationSettingAsync(update, onlyIfUnchanged: true, cancellationToken)).ConfigureAwait(false);
            }
            else
            {
                await AzureErrors.AppConfigurationAsync(storeName, key, () =>
                    config.AddConfigurationSettingAsync(new ConfigurationSetting(key, names[i]), cancellationToken)).ConfigureAwait(false);
            }
        }
        foreach (var surplus in byKey.Values)
            await AzureErrors.AppConfigurationAsync(storeName, surplus.Key, () =>
                config.DeleteConfigurationSettingAsync(surplus, onlyIfUnchanged: true, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<ProviderState> LoadAsync(string providerId, CancellationToken cancellationToken)
    {
        var info = ProviderCatalog.Get(providerId);
        var section = LampNames.ProviderSection(info.Id);
        var own = await ListAsync(section, cancellationToken).ConfigureAwait(false);

        var aliasNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in info.Aliases)
        {
            var aliasSection = $"{VaultConfigurationKeys.VaultSection}:{info.Bucket}:{alias}";
            foreach (var s in await ListAsync(aliasSection, cancellationToken).ConfigureAwait(false))
                if (ChildOf(s.Key, $"{aliasSection}:{LampNames.KeysSegment}") is { } n) aliasNames.TryAdd(n, alias);
        }
        return new ProviderState(info.Id, section, own, aliasNames);
    }

    private async Task<IReadOnlyList<ConfigurationSetting>> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        var selector = new SettingSelector
        {
            KeyFilter = AzureErrors.EscapeFilter(prefix) + ":*",
            LabelFilter = Settings.AppConfigurationSettingsBackend.NullLabel,
        };
        var result = new List<ConfigurationSetting>();
        await AzureErrors.AppConfigurationAsync(storeName, prefix, async () =>
        {
            await foreach (var s in config.GetConfigurationSettingsAsync(selector, cancellationToken).ConfigureAwait(false))
                if (string.IsNullOrEmpty(s.Label)) result.Add(s);
        }).ConfigureAwait(false);
        return result;
    }

    private async Task<List<(string App, string Provider, ConfigurationSetting Setting)>> AssignmentsAsync(CancellationToken cancellationToken)
    {
        var result = new List<(string, string, ConfigurationSetting)>();
        foreach (var s in await ListAsync(LampNames.AssignmentsSection, cancellationToken).ConfigureAwait(false))
        {
            // MindAttic:Vault:Assignments:{AppId}:{provider}:{i}
            var rest = s.Key.Substring(LampNames.AssignmentsSection.Length + 1).Split(':');
            if (rest.Length != 3 || !int.TryParse(rest[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
            if (string.IsNullOrWhiteSpace(rest[0]) || string.IsNullOrWhiteSpace(rest[1])) continue;
            result.Add((rest[0], ProviderCatalog.Normalize(rest[1]), s));
        }
        return result;
    }

    /// <summary>The direct <c>{path}:{i}</c> children, in index order.</summary>
    private static List<ConfigurationSetting> IndexedValues(IEnumerable<ConfigurationSetting> settings, string path) =>
        settings
            .Select(s => (Setting: s, Index: ChildOf(s.Key, path) is { } c && int.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : -1))
            .Where(x => x.Index >= 0)
            .OrderBy(x => x.Index)
            .Select(x => x.Setting)
            .ToList();

    /// <summary>The single segment after <paramref name="path"/>, or null when the key isn't a direct child.</summary>
    private static string? ChildOf(string key, string path)
    {
        if (!key.StartsWith(path + ":", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = key.Substring(path.Length + 1);
        return rest.Length == 0 || rest.Contains(':') ? null : rest;
    }

    private static List<string> Replace(IEnumerable<string> names, string from, string to) =>
        names.Select(n => Same(n, from) ? to : n).ToList();

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string RequiredApp(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || appId.Contains(':')) throw new ArgumentException("A valid app id is required.", nameof(appId));
        return appId.Trim();
    }

    /// <summary>One provider's key-values in App Configuration, as read before a change.</summary>
    private sealed class ProviderState
    {
        public ProviderState(string provider, string section, IReadOnlyList<ConfigurationSetting> settings, IReadOnlyDictionary<string, string> aliasNames)
        {
            Provider = provider;
            OrderPath = $"{section}:{LampNames.KeyOrderSegment}";
            DisabledPath = $"{section}:{LampNames.DisabledKeysSegment}";
            var keysPath = $"{section}:{LampNames.KeysSegment}";
            foreach (var s in settings)
                if (ChildOf(s.Key, keysPath) is { } name && !string.IsNullOrWhiteSpace(s.Value)) Keys.TryAdd(name, s);
            OrderSettings = IndexedValues(settings, OrderPath);
            DisabledSettings = IndexedValues(settings, DisabledPath);
            AliasNames = aliasNames;

            // Same rule as ConfigurationKeySource: stored order first (known names only), then the rest by name.
            var order = new List<string>();
            foreach (var n in OrderSettings.Select(s => s.Value?.Trim()))
                if (n is not null && Keys.ContainsKey(n) && !order.Contains(n, StringComparer.OrdinalIgnoreCase)) order.Add(Keys.Keys.First(k => Same(k, n)));
            foreach (var n in Keys.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                if (!order.Contains(n, StringComparer.OrdinalIgnoreCase)) order.Add(n);
            Order = order;
            Disabled = DisabledSettings.Select(s => s.Value?.Trim()).Where(n => n is not null && Keys.ContainsKey(n))
                .Select(n => Keys.Keys.First(k => Same(k, n!))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public string Provider { get; }
        public string OrderPath { get; }
        public string DisabledPath { get; }
        public Dictionary<string, ConfigurationSetting> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<ConfigurationSetting> OrderSettings { get; }
        public IReadOnlyList<ConfigurationSetting> DisabledSettings { get; }
        public IReadOnlyDictionary<string, string> AliasNames { get; }
        public IReadOnlyList<string> Order { get; }
        public IReadOnlyList<string> Disabled { get; }

        public IEnumerable<string> AllNames => Keys.Keys.Concat(AliasNames.Keys);

        public IEnumerable<string> SecretNames =>
            Keys.Values.Select(KeyVaultReference.TryParse).Where(r => r is not null).Select(r => r!.SecretName);

        public (string Name, ConfigurationSetting Setting) Find(string name)
        {
            var trimmed = name?.Trim() ?? "";
            foreach (var (k, s) in Keys)
                if (Same(k, trimmed)) return (k, s);
            if (AliasNames.TryGetValue(trimmed, out var alias))
                throw new InvalidOperationException(
                    $"Key '{trimmed}' is stored under the alias '{alias}'. Move it to '{Provider}' before editing it here.");
            throw new KeyNotFoundException($"No key named '{trimmed}'.");
        }
    }
}

/// <summary>Tags every named key's secret carries (names only, never values).</summary>
public static class SecretTags
{
    public const string Name = "name";
    public const string Provider = "provider";
}
