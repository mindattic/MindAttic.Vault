using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MindAttic.Vault.Paths;

namespace MindAttic.Vault.Settings.Manifest;

/// <summary>Reads and writes <c>settings.manifest.json</c>.</summary>
public static class SettingsManifestFile
{
    /// <summary>Manifests larger than this are refused on read (they are a few KB in practice).</summary>
    public const int MaxBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The manifest as the JSON text written to disk.</summary>
    public static string Serialize(SettingsManifest manifest) => JsonSerializer.Serialize(manifest, Options);

    /// <summary>
    /// Writes the manifest into <paramref name="directory"/> atomically, only when its content changed.
    /// Returns true when the file was (re)written. Never throws for I/O: a manifest is a convenience
    /// for Vault.Desktop and must never stop the app from starting.
    /// </summary>
    public static bool WriteIfChanged(string directory, SettingsManifest manifest)
    {
        try
        {
            var path = Path.Combine(directory, SettingsManifest.FileName);
            var json = Serialize(manifest);
            if (File.Exists(path) && File.ReadAllText(path) == json) return false;

            Directory.CreateDirectory(directory);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, json);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads a manifest, or null when it is missing, too large, unparseable or of an unknown schema.</summary>
    public static SettingsManifest? TryRead(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes) return null;
            var manifest = JsonSerializer.Deserialize<SettingsManifest>(File.ReadAllText(path), Options);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.AppId)) return null;
            if (manifest.SchemaVersion < 1 || manifest.SchemaVersion > SettingsManifest.CurrentSchemaVersion) return null;
            return manifest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Every manifest under the roaming and local roots (one per app folder), with the folder it was found in,
    /// ordered by display name. An app id found in both keeps the one whose folder matches its declared scope.
    /// </summary>
    public static IReadOnlyList<(string Directory, SettingsManifest Manifest)> Discover(string? roamingRoot = null, string? localRoot = null)
    {
        var found = new Dictionary<string, (string Dir, SettingsManifest Manifest)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, scope) in new[] { (roamingRoot ?? VaultPaths.RoamingRoot, SettingsManifest.RoamingScope), (localRoot ?? VaultPaths.LocalRoot, SettingsManifest.LocalScope) })
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root).ToList(); }
            catch { continue; }
            foreach (var dir in dirs)
            {
                var manifest = TryRead(Path.Combine(dir, SettingsManifest.FileName));
                if (manifest is null) continue;
                var matchesScope = string.Equals(manifest.Scope, scope, StringComparison.OrdinalIgnoreCase);
                if (found.TryGetValue(manifest.AppId, out var existing)
                    && string.Equals(existing.Manifest.Scope, ScopeOf(existing.Dir, roamingRoot), StringComparison.OrdinalIgnoreCase)
                    && !matchesScope)
                    continue;
                found[manifest.AppId] = (dir, manifest);
            }
        }
        return found.Values.Select(v => (v.Dir, v.Manifest))
            .OrderBy(f => f.Manifest.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ScopeOf(string dir, string? roamingRoot)
    {
        var roaming = Path.GetFullPath(roamingRoot ?? VaultPaths.RoamingRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(dir).StartsWith(roaming, StringComparison.OrdinalIgnoreCase) ? SettingsManifest.RoamingScope : SettingsManifest.LocalScope;
    }
}

/// <summary>
/// The <c>--write-settings-manifest</c> switch every app's run script calls once on install: seeds
/// the app's defaults, writes its manifest, and exits, so Vault.Desktop shows the app before it
/// has ever been run.
/// </summary>
public static class SettingsManifestCommand
{
    public const string Switch = "--write-settings-manifest";

    /// <summary>
    /// When <paramref name="args"/> contains <see cref="Switch"/>: seeds and writes the manifest for
    /// <typeparamref name="T"/> in its app folder, prints the folder, and returns true (the caller exits).
    /// Otherwise returns false and does nothing.
    /// </summary>
    public static bool TryRun<T>(string[] args, TextWriter? output = null) where T : class, new()
    {
        if (args is null || !args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase))) return false;
        var store = JsonSettingsStore<T>.ForSettingsApp();
        store.LoadOrSeed();
        (output ?? Console.Out).WriteLine(Path.Combine(store.Directory, SettingsManifest.FileName));
        return true;
    }

    /// <summary>The <see cref="SettingsAppAttribute"/> id of <typeparamref name="T"/>.</summary>
    public static string AppIdOf<T>() =>
        typeof(T).GetCustomAttribute<SettingsAppAttribute>()?.Id
        ?? throw new SettingsManifestException($"{typeof(T).FullName} has no [SettingsApp] attribute.");
}
