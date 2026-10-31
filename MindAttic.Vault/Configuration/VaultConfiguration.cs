using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Paths;
using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Vault.Configuration;

/// <summary>
/// The names every LAMP value has, identical in local files, App Configuration and IConfiguration.
/// <code>
/// MindAttic:Settings:{AppId}:{property}[:{child}...]       app settings (Global for cross-app ones)
/// MindAttic:Vault:{bucket}:{provider}:keys:{keyName}       a named key's secret value
/// MindAttic:Vault:{bucket}:{provider}:keyOrder:{i}         the shared failover order (key names)
/// MindAttic:Vault:{bucket}:{provider}:disabledKeys:{i}     disabled key names
/// MindAttic:Vault:Assignments:{AppId}:{provider}:{i}       an app's key names, first = active
/// </code>
/// </summary>
public static class LampNames
{
    public const string SettingsRoot = SettingsManifest.ConfigurationRoot;
    public const string GlobalApp = "Global";
    public const string AssignmentsSection = VaultConfigurationKeys.VaultSection + ":Assignments";
    public const string KeysSegment = "keys";
    public const string KeyOrderSegment = "keyOrder";
    public const string DisabledKeysSegment = "disabledKeys";

    public static string AppSettings(string appId) => $"{SettingsRoot}:{appId}";
    public static string ProviderSection(string providerId)
    {
        var info = ProviderCatalog.Get(providerId);
        return $"{VaultConfigurationKeys.VaultSection}:{info.Bucket}:{info.Id}";
    }
    public static string Key(string providerId, string keyName) => $"{ProviderSection(providerId)}:{KeysSegment}:{keyName}";
    public static string Assignment(string appId, string providerId) => $"{AssignmentsSection}:{appId}:{ProviderCatalog.Normalize(providerId)}";
}

/// <summary>
/// Presents the whole local store as one IConfiguration tree: every app folder's <c>settings.json</c>
/// (including <c>Global</c>) under <c>MindAttic:Settings</c>, every provider's named keys, and the
/// per-app key assignments. Read it whole or bind any part (<c>GetSection("MindAttic:Settings:IdiotProof")</c>).
/// Per-app files keep apps from racing each other's writes and keep one corrupt file from hiding the rest.
/// </summary>
public sealed class VaultConfigurationSource : IConfigurationSource
{
    /// <summary>Roaming root override (tests). Defaults to <see cref="VaultPaths.RoamingRoot"/>.</summary>
    public string? RoamingRoot { get; set; }

    /// <summary>Reload when any settings, key or assignment file changes.</summary>
    public bool ReloadOnChange { get; set; }

    /// <summary>Include the named-key projection (secret values). Off for hosts that only want settings.</summary>
    public bool IncludeKeys { get; set; } = true;

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new VaultConfigurationProvider(this);

    /// <summary>Local root override (tests). Defaults to <see cref="VaultPaths.LocalRoot"/>; scanned for per-machine apps.</summary>
    public string? LocalRoot { get; set; }

    internal string EffectiveRoot => RoamingRoot ?? VaultPaths.RoamingRoot;
    internal string EffectiveLocalRoot => LocalRoot ?? VaultPaths.LocalRoot;
}

internal sealed class VaultConfigurationProvider : ConfigurationProvider, IDisposable
{
    private static readonly string[] KeyBuckets = { LlmCredentialStore.Bucket, BrokerCredentialStore.Bucket };
    private readonly VaultConfigurationSource source;
    private readonly object gate = new();
    private FileSystemWatcher? watcher;
    private Timer? debounce;
    private bool disposed;

    public VaultConfigurationProvider(VaultConfigurationSource source) => this.source = source;

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var root = source.EffectiveRoot;
        if (Directory.Exists(root))
        {
            LoadSettings(root, data, localOnly: false);
            if (source.IncludeKeys) LoadKeys(root, data);
            LoadAssignments(root, data);
        }
        if (Directory.Exists(source.EffectiveLocalRoot)) LoadSettings(source.EffectiveLocalRoot, data, localOnly: true);
        Data = data;
        if (source.ReloadOnChange) EnsureWatcher(root);
    }

    private static void LoadSettings(string root, IDictionary<string, string?> data, bool localOnly)
    {
        foreach (var dir in SafeDirectories(root))
        {
            var file = Path.Combine(dir, "settings.json");
            if (!File.Exists(file)) continue;
            // Local AppData holds many apps' caches; only folders a per-machine manifest claims are settings.
            if (localOnly && SettingsManifestFile.TryRead(Path.Combine(dir, SettingsManifest.FileName))?.Scope != SettingsManifest.LocalScope)
                continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                var prefix = LampNames.AppSettings(Path.GetFileName(dir));
                foreach (var prop in doc.RootElement.EnumerateObject())
                    MindAtticConfigurationProvider.FlattenInto(data, $"{prefix}:{prop.Name}", prop.Value);
            }
            catch
            {
                // One unreadable app file never hides the others.
            }
        }
    }

    private static void LoadKeys(string root, IDictionary<string, string?> data)
    {
        var store = new NamedKeyStore(root, new KeyAssignmentStore(Path.Combine(root, KeyAssignmentStore.Bucket)));
        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bucket in KeyBuckets)
        {
            var dir = Path.Combine(root, bucket);
            if (!Directory.Exists(dir)) continue;
            foreach (var id in new CredentialStore(dir).LoadAllRaw().Keys) providers.Add(ProviderCatalog.Normalize(id));
            foreach (var keyFile in Directory.EnumerateFiles(dir, "*" + CredentialStore.KeyFileExtension))
                providers.Add(ProviderCatalog.Normalize(Path.GetFileNameWithoutExtension(keyFile)));
        }

        foreach (var provider in providers)
        {
            var keys = store.GetKeys(provider);
            var section = LampNames.ProviderSection(provider);
            var disabled = 0;
            for (var i = 0; i < keys.Count; i++)
            {
                data[$"{section}:{LampNames.KeysSegment}:{keys[i].Name}"] = keys[i].Value;
                data[$"{section}:{LampNames.KeyOrderSegment}:{i}"] = keys[i].Name;
                if (keys[i].Disabled) data[$"{section}:{LampNames.DisabledKeysSegment}:{disabled++}"] = keys[i].Name;
            }
        }
    }

    private static void LoadAssignments(string root, IDictionary<string, string?> data)
    {
        var assignments = new KeyAssignmentStore(Path.Combine(root, KeyAssignmentStore.Bucket));
        foreach (var (app, byProvider) in assignments.All())
            foreach (var (provider, names) in byProvider)
                for (var i = 0; i < names.Count; i++)
                    data[$"{LampNames.AssignmentsSection}:{app}:{provider}:{i}"] = names[i];
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private void EnsureWatcher(string root)
    {
        lock (gate)
        {
            if (disposed || watcher is not null || !Directory.Exists(root)) return;
            try
            {
                watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                FileSystemEventHandler changed = (_, e) => { if (IsRelevant(e.FullPath)) Schedule(); };
                watcher.Changed += changed;
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Renamed += (_, e) => { if (IsRelevant(e.FullPath) || IsRelevant(e.OldFullPath)) Schedule(); };
                watcher.EnableRaisingEvents = true;
            }
            catch
            {
                watcher?.Dispose();
                watcher = null;
            }
        }
    }

    private static bool IsRelevant(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("settings.json", StringComparison.OrdinalIgnoreCase)
               || name.Equals(CredentialStore.ProvidersJsonFile, StringComparison.OrdinalIgnoreCase)
               || name.Equals(KeyAssignmentStore.FileName, StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(CredentialStore.KeyFileExtension, StringComparison.OrdinalIgnoreCase);
    }

    private void Schedule()
    {
        lock (gate)
        {
            if (disposed) return;
            debounce ??= new Timer(_ =>
            {
                lock (gate) { if (disposed) return; }
                Load();
                OnReload();
            });
            debounce.Change(TimeSpan.FromMilliseconds(250), Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            debounce?.Dispose();
            watcher?.Dispose();
        }
    }
}

/// <summary>Registration helpers for the merged tree.</summary>
public static class VaultConfigurationExtensions
{
    /// <summary>Adds <see cref="VaultConfigurationSource"/>: every app's settings, the named keys and assignments as one tree.</summary>
    public static IConfigurationBuilder AddMindAtticVaultConfiguration(this IConfigurationBuilder builder,
        Action<VaultConfigurationSource>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var source = new VaultConfigurationSource();
        configure?.Invoke(source);
        builder.Add(source);
        return builder;
    }
}
