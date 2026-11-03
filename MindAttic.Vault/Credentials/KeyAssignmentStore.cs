using System.Text.Json;
using System.Text.Json.Nodes;
using MindAttic.Vault.Paths;

namespace MindAttic.Vault.Credentials;

/// <summary>
/// Which named keys each app uses, per provider: an ordered list of key names (first = the app's
/// active key, the rest its fallbacks). Holds names only, never key values. No entry means the app
/// uses the provider's shared list in its default order.
///
/// <para>File: <c>%APPDATA%\MindAttic\Vault\key-assignments.json</c>, shape
/// <c>{ "IdiotProof": { "claude": ["2026-10-31 IdiotProof", "key1"] } }</c>; surfaced in
/// IConfiguration as <c>MindAttic:Vault:Assignments:{AppId}:{provider}:{index}</c>.</para>
/// </summary>
public sealed class KeyAssignmentStore
{
    public const string Bucket = "Vault";
    public const string FileName = "key-assignments.json";

    private readonly object gate = new();

    public KeyAssignmentStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Directory is required.", nameof(directory));
        Directory = directory;
    }

    /// <summary>The store under the current roaming root.</summary>
    public static KeyAssignmentStore ForRoamingRoot() => new(VaultPaths.RoamingBucket(Bucket));

    public string Directory { get; }
    public string FilePath => Path.Combine(Directory, FileName);

    /// <summary>The app's ordered key names for the provider; empty when it uses the shared order.</summary>
    public IReadOnlyList<string> Get(string appId, string providerId)
    {
        var all = Read();
        return all.TryGetValue(appId, out var byProvider) && byProvider.TryGetValue(ProviderCatalog.Normalize(providerId), out var names)
            ? names
            : Array.Empty<string>();
    }

    /// <summary>Every assignment: app → provider → ordered names.</summary>
    public IReadOnlyDictionary<string, Dictionary<string, List<string>>> All() => Read();

    /// <summary>Sets the app's ordered key names for the provider; an empty list clears the assignment.</summary>
    public void Set(string appId, string providerId, IEnumerable<string> names)
    {
        if (string.IsNullOrWhiteSpace(appId)) throw new ArgumentException("App id is required.", nameof(appId));
        var provider = ProviderCatalog.Normalize(providerId);
        var list = (names ?? Enumerable.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Mutate(all =>
        {
            if (!all.TryGetValue(appId, out var byProvider))
                all[appId] = byProvider = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (list.Count == 0) byProvider.Remove(provider);
            else byProvider[provider] = list;
            if (byProvider.Count == 0) all.Remove(appId);
        });
    }

    /// <summary>Apps whose assignment for the provider names <paramref name="keyName"/>.</summary>
    public IReadOnlyList<string> AppsUsing(string providerId, string keyName)
    {
        var provider = ProviderCatalog.Normalize(providerId);
        return Read()
            .Where(a => a.Value.TryGetValue(provider, out var names) && names.Contains(keyName, StringComparer.OrdinalIgnoreCase))
            .Select(a => a.Key)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Points every app's assignment at the renamed key.</summary>
    public void RenameKey(string providerId, string oldName, string newName)
    {
        var provider = ProviderCatalog.Normalize(providerId);
        Mutate(all =>
        {
            foreach (var byProvider in all.Values)
            {
                if (!byProvider.TryGetValue(provider, out var names)) continue;
                for (var i = 0; i < names.Count; i++)
                    if (string.Equals(names[i], oldName, StringComparison.OrdinalIgnoreCase)) names[i] = newName;
            }
        });
    }

    // forWrite: Mutate rewrites the whole file from this map, so an existing file that can't be read
    // (locked by another process's swap, access denied) throws instead of reading as empty, which would
    // otherwise drop every other app's assignments on the write.
    private Dictionary<string, Dictionary<string, List<string>>> Read(bool forWrite = false)
    {
        var result = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(FilePath)) return result;
            if (JsonNode.Parse(File.ReadAllText(FilePath)) is not JsonObject root) return result;
            foreach (var (app, providers) in root)
            {
                if (providers is not JsonObject byProvider) continue;
                var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (provider, names) in byProvider)
                {
                    if (names is not JsonArray arr) continue;
                    var list = arr.OfType<JsonValue>()
                        .Select(v => v.TryGetValue<string>(out var s) ? s : null)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s!)
                        .ToList();
                    if (list.Count > 0) map[ProviderCatalog.Normalize(provider)] = list;
                }
                if (map.Count > 0) result[app] = map;
            }
        }
        catch (Exception ex) when (forWrite && ex is UnauthorizedAccessException or (IOException and not (FileNotFoundException or DirectoryNotFoundException))) { throw; }
        catch
        {
            // Unreadable assignments fall back to every app using the shared order.
        }
        return result;
    }

    private void Mutate(Action<Dictionary<string, Dictionary<string, List<string>>>> change)
    {
        lock (gate)
        {
            var all = Read(forWrite: true);
            change(all);
            var root = new JsonObject();
            foreach (var (app, byProvider) in all.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase))
            {
                var obj = new JsonObject();
                foreach (var (provider, names) in byProvider.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                    obj[provider] = new JsonArray(names.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
                root[app] = obj;
            }
            AtomicFile.WriteAllText(FilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}

/// <summary>Write-temp-then-replace, so a reader never sees a half-written file (M.VALT-LAW-6).</summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}
