using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Launch;
using MindAttic.Vault.Paths;
using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Vault.Desktop.Profiles;

/// <summary>
/// The <c>Desktop</c> Profile: this PC's AppData. Settings are each app folder's <c>settings.json</c> under the root its
/// manifest's <see cref="SettingsManifest.Scope"/> names (roaming by default, local for per-machine apps), read and
/// written as JSON, atomically; keys are the shared named lists and app assignments in the local
/// credential stores. Always ready.
/// </summary>
public sealed class DesktopProfile : IProfile
{
    public const string ProfileId = "Desktop";

    /// <param name="roamingRoot">The roaming root; null resolves <see cref="VaultPaths.RoamingRoot"/> on every call.</param>
    /// <param name="keys">The named key store; null uses the one under the roaming root.</param>
    /// <param name="localRoot">The local root for <c>Local</c>-scope apps; null resolves <see cref="VaultPaths.LocalRoot"/> on every call.</param>
    public DesktopProfile(string? roamingRoot = null, NamedKeyStore? keys = null, string? localRoot = null)
    {
        Settings = new DesktopSettingsBackend(roamingRoot, localRoot);
        Keys = new DesktopKeyBackend(keys ?? (roamingRoot is null
            ? NamedKeyStore.ForRoamingRoot()
            : new NamedKeyStore(roamingRoot, new KeyAssignmentStore(Path.Combine(roamingRoot, KeyAssignmentStore.Bucket)))));
    }

    public string Id => ProfileId;
    public string DisplayName => "Desktop (this PC)";
    public ISettingsBackend Settings { get; }
    public IKeyBackend Keys { get; }
    public string Actor { get; } = $"{Environment.UserDomainName}\\{Environment.UserName}";

    public Task<ProfileReadiness> GetReadinessAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ProfileReadiness.Ready);

    public override string ToString() => DisplayName;
}

/// <summary>
/// Each app's <c>%APPDATA%\MindAttic\{AppId}\settings.json</c> (or <c>%LOCALAPPDATA%</c> for a <c>Local</c>-scope
/// manifest: the folder its manifest is discovered in). The version is a SHA-256 of the file's bytes, so any
/// change by the app or another editor since Load is caught on Save. Save re-reads the file and writes only the changed
/// fields into it, so values the form never touched (and properties the manifest doesn't know) stay as they are.
/// </summary>
public sealed class DesktopSettingsBackend : ISettingsBackend
{
    /// <summary>The version of an app whose settings file doesn't exist yet.</summary>
    public const string AbsentVersion = "absent";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? roamingRoot;
    private readonly string? localRoot;

    public DesktopSettingsBackend(string? roamingRoot = null, string? localRoot = null)
    {
        this.roamingRoot = roamingRoot;
        this.localRoot = localRoot;
    }

    public string WhenAppsSeeChanges => "on the app's next start (apps that reload settings see it within seconds)";

    /// <summary>The settings file for <paramref name="manifest"/>; refuses ids and file names that would leave the app's folder.</summary>
    public string PathFor(SettingsManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var id = manifest.AppId;
        if (!VaultOpenRequest.IsValidAppId(id) || id is "." or "..")
            throw new InvalidOperationException("The manifest's app id isn't a plain folder name.");
        var file = string.IsNullOrWhiteSpace(manifest.SettingsFile) ? "settings.json" : manifest.SettingsFile;
        if (file is "." or ".." || Path.GetFileName(file) != file || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException($"{id}'s manifest names a settings file outside its folder.");
        var root = string.Equals(manifest.Scope, SettingsManifest.LocalScope, StringComparison.OrdinalIgnoreCase)
            ? localRoot ?? VaultPaths.LocalRoot
            : roamingRoot ?? VaultPaths.RoamingRoot;
        return Path.Combine(root, id, file);
    }

    public async Task<SettingsDocument> LoadAsync(SettingsManifest manifest, CancellationToken cancellationToken = default)
    {
        var path = PathFor(manifest);
        if (!File.Exists(path)) return new SettingsDocument(Defaults(manifest), AbsentVersion);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return new SettingsDocument(Parse(bytes) ?? Defaults(manifest), Hash(bytes));
    }

    public async Task<string?> SaveAsync(SettingsManifest manifest, SettingsDocument document, IReadOnlyCollection<string> changedFields,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(changedFields);
        var path = PathFor(manifest);
        var gate = Gates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[]? current = File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : null;
            var currentVersion = current is null ? AbsentVersion : Hash(current);
            if (!string.Equals(document.Version, currentVersion, StringComparison.Ordinal))
                throw new ConcurrencyConflictException($"{Name(manifest)}'s settings changed on disk since you opened them.");

            var target = (current is null ? null : Parse(current)) ?? new JsonObject();
            var prefix = string.IsNullOrEmpty(manifest.ConfigurationPrefix) ? SettingsManifest.PrefixFor(manifest.AppId) : manifest.ConfigurationPrefix;
            foreach (var name in changedFields) SettingsDocumentMerge.Apply(document.Values, target, prefix, name);

            var bytes = Encoding.UTF8.GetBytes(target.ToJsonString(WriteOptions));
            WriteAtomically(path, bytes);
            return Hash(bytes);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A document of each top-level field's default: what the app's settings class starts with.</summary>
    public static JsonObject Defaults(SettingsManifest manifest)
    {
        var values = new JsonObject();
        foreach (var field in manifest.Fields)
            if (!string.IsNullOrEmpty(field.Property) && field.Default is not null)
                values[field.Property] = field.Default.DeepClone();
        return values;
    }

    /// <summary>The version of file content: SHA-256, hex.</summary>
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string Name(SettingsManifest manifest) => string.IsNullOrWhiteSpace(manifest.DisplayName) ? manifest.AppId : manifest.DisplayName;

    private static JsonObject? Parse(byte[] bytes)
    {
        try { return JsonNode.Parse(bytes) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }
}

/// <summary>Copies changed fields, by full name, from an edited document into the stored one.</summary>
public static class SettingsDocumentMerge
{
    /// <summary>
    /// Copies the field <paramref name="fieldName"/> (a full name under <paramref name="prefix"/>, e.g.
    /// <c>MindAttic:Settings:App:connection:host</c>) from <paramref name="source"/> into <paramref name="target"/>,
    /// creating parent objects. A list (collection, string list) is copied whole, so added, removed and reordered items
    /// land together. A property missing from the source is removed from the target. Returns false for a name outside
    /// the prefix (ignored).
    /// </summary>
    public static bool Apply(JsonObject source, JsonObject target, string prefix, string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName) || !fieldName.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase)) return false;
        var segments = fieldName[(prefix.Length + 1)..].Split(':');
        if (segments.Any(string.IsNullOrEmpty)) return false;

        JsonObject s = source, t = target;
        for (var i = 0; i < segments.Length; i++)
        {
            var sourceKey = KeyOf(s, segments[i]);
            var targetKey = KeyOf(t, segments[i]) ?? sourceKey ?? segments[i];
            if (sourceKey is null)
            {
                t.Remove(targetKey);
                return true;
            }
            var node = s[sourceKey];
            if (i == segments.Length - 1 || node is not JsonObject childSource)
            {
                if (targetKey != sourceKey) t.Remove(targetKey);
                t[sourceKey] = node?.DeepClone();
                return true;
            }
            if (t[targetKey] is not JsonObject childTarget)
            {
                childTarget = new JsonObject();
                t[targetKey] = childTarget;
            }
            s = childSource;
            t = childTarget;
        }
        return true;
    }

    private static string? KeyOf(JsonObject o, string name)
    {
        if (o.ContainsKey(name)) return name;
        foreach (var (key, _) in o)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return key;
        return null;
    }
}

/// <summary>The local named key lists (<see cref="NamedKeyStore"/>) and app assignments.</summary>
public sealed class DesktopKeyBackend : IKeyBackend
{
    private readonly NamedKeyStore keys;

    public DesktopKeyBackend(NamedKeyStore keys) => this.keys = keys ?? throw new ArgumentNullException(nameof(keys));

    public Task<IReadOnlyList<string>> ProvidersWithKeysAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(keys.ProvidersWithKeys());

    public Task<IReadOnlyList<NamedKey>> GetKeysAsync(string providerId, CancellationToken cancellationToken = default) =>
        Task.FromResult(keys.GetKeys(providerId));

    public Task AddAsync(string providerId, string name, string value, CancellationToken cancellationToken = default) =>
        Run(() => keys.Add(providerId, name, value));

    public Task SetValueAsync(string providerId, string name, string value, CancellationToken cancellationToken = default) =>
        Run(() => keys.SetValue(providerId, name, value));

    public Task RenameAsync(string providerId, string oldName, string newName, CancellationToken cancellationToken = default) =>
        Run(() => keys.Rename(providerId, oldName, newName));

    public Task RemoveAsync(string providerId, string name, CancellationToken cancellationToken = default) =>
        Run(() => keys.Remove(providerId, name));

    public Task SetDisabledAsync(string providerId, string name, bool disabled, CancellationToken cancellationToken = default) =>
        Run(() => keys.SetDisabled(providerId, name, disabled));

    public Task ReorderAsync(string providerId, IReadOnlyList<string> names, CancellationToken cancellationToken = default) =>
        Run(() => keys.Reorder(providerId, names));

    public Task<IReadOnlyList<string>> GetAssignmentAsync(string appId, string providerId, CancellationToken cancellationToken = default) =>
        Task.FromResult(keys.Assignments.Get(appId, providerId));

    public Task SetAssignmentAsync(string appId, string providerId, IReadOnlyList<string> names, CancellationToken cancellationToken = default) =>
        Run(() => keys.Assignments.Set(appId, providerId, names));

    public Task<IReadOnlyList<string>> AppsUsingAsync(string providerId, string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(keys.Assignments.AppsUsing(providerId, name));

    private static Task Run(Action action)
    {
        try
        {
            action();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }
}
