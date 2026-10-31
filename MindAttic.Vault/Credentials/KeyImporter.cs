using System.Text.Json;

namespace MindAttic.Vault.Credentials;

/// <summary>Where an importable key was found.</summary>
public enum ImportSourceKind { AppScoped, Environment, LegionJson }

/// <summary>A key that can be moved into the shared named lists. Holds no key value.</summary>
public sealed record ImportCandidate(ImportSourceKind Kind, string Source, string ProviderId, string? AppId, string SuggestedName);

/// <summary>What an import did: key names added and names reused because the value was already there.</summary>
public sealed record ImportResult(IReadOnlyList<string> Added, IReadOnlyList<string> Reused)
{
    public static ImportResult operator +(ImportResult a, ImportResult b) =>
        new(a.Added.Concat(b.Added).ToList(), a.Reused.Concat(b.Reused).ToList());

    public static ImportResult Empty { get; } = new(Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// Moves keys that live outside the shared named lists today into them: per-app copies written by
/// <see cref="AppScopedCredentialStore"/> (<c>{app}-{provider}</c> entries), the environment variables
/// apps read, and a project's <c>legion.json</c> <c>apiKeys</c>. A value already in the list is reused,
/// never duplicated; an app's own copy becomes that app's active key. Running an import twice changes nothing.
/// Offered from Vault.Desktop's Get Started, never run silently.
/// </summary>
public sealed class KeyImporter
{
    /// <summary>Environment variables apps read today, and the provider each one holds.</summary>
    public static IReadOnlyDictionary<string, string> EnvironmentVariables { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ANTHROPIC_API_KEY"] = "claude",
        ["CLAUDE_API_KEY"] = "claude",
        ["OPENAI_API_KEY"] = "openai",
        ["GEMINI_API_KEY"] = "gemini",
        ["GOOGLE_API_KEY"] = "gemini",
        ["DEEPSEEK_API_KEY"] = "deepseek",
        ["MOONSHOT_API_KEY"] = "kimi",
        ["KIMI_API_KEY"] = "kimi",
        ["MISTRAL_API_KEY"] = "mistral",
        ["XAI_API_KEY"] = "grok",
        ["GROQ_API_KEY"] = "groq",
        ["OPENROUTER_API_KEY"] = "openrouter",
        ["ELEVENLABS_API_KEY"] = "elevenlabs",
        ["JF_API_KEY"] = "jellyfin",
    };

    /// <summary>App-scoped prefixes in use and the settings app id each belongs to.</summary>
    public static IReadOnlyDictionary<string, string> AppPrefixes { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["automata"] = "Automata",
        ["tutor"] = "Tutor",
        ["mediabutler"] = "MediaButler",
        ["idiotproof"] = "IdiotProof",
        ["jobhunt"] = "JobHunt",
        ["taxratecollector"] = "TaxRateCollector",
        ["thinktank"] = "ThinkTank",
        ["prose"] = "Prose",
    };

    private readonly NamedKeyStore keys;
    private readonly Func<string, string?> getEnvironmentVariable;

    public KeyImporter(NamedKeyStore keys, Func<string, string?>? getEnvironmentVariable = null)
    {
        this.keys = keys ?? throw new ArgumentNullException(nameof(keys));
        this.getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    /// <summary><c>{app}-{provider}</c> entries in the LLM bucket for a known provider.</summary>
    public IReadOnlyList<ImportCandidate> FindAppScoped()
    {
        var store = keys.StoreFor("claude");
        var ids = store.LoadAllRaw().Keys
            .Concat(Directory.Exists(store.Directory)
                ? Directory.EnumerateFiles(store.Directory, "*" + CredentialStore.KeyFileExtension).Select(f => Path.GetFileNameWithoutExtension(f)!)
                : Enumerable.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var found = new List<ImportCandidate>();
        foreach (var id in ids)
        {
            var dash = id.IndexOf('-');
            if (dash <= 0 || dash == id.Length - 1) continue;
            var prefix = id[..dash];
            var provider = id[(dash + 1)..];
            if (!AppPrefixes.TryGetValue(prefix, out var appId) || !ProviderCatalog.IsKnown(provider)) continue;
            if (store.GetOverrideKey(id) is null && store.GetPoolKeys(id).Count == 0) continue;
            found.Add(new ImportCandidate(ImportSourceKind.AppScoped, id, ProviderCatalog.Normalize(provider), appId, $"{appId} (imported)"));
        }
        return found.OrderBy(c => c.Source, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Known environment variables that are set and whose value isn't already in the list.</summary>
    public IReadOnlyList<ImportCandidate> FindEnvironment()
    {
        var found = new List<ImportCandidate>();
        foreach (var (variable, provider) in EnvironmentVariables)
        {
            var value = getEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (keys.GetKeys(provider).Any(k => k.Value == value.Trim())) continue;
            found.Add(new ImportCandidate(ImportSourceKind.Environment, variable, provider, null, $"env {variable}"));
        }
        return found.OrderBy(c => c.Source, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A <c>legion.json</c>'s <c>apiKeys</c> entries whose value isn't already in the list.</summary>
    public IReadOnlyList<ImportCandidate> FindLegionJson(string path, string? appId = null)
    {
        var found = new List<ImportCandidate>();
        foreach (var (provider, value) in ReadLegionKeys(path))
        {
            var canonical = ProviderCatalog.Normalize(provider);
            if (keys.GetKeys(canonical).Any(k => k.Value == value)) continue;
            var project = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))) ?? "legion";
            found.Add(new ImportCandidate(ImportSourceKind.LegionJson, path + "#" + provider, canonical, appId, $"{project} legion.json (imported)"));
        }
        return found;
    }

    /// <summary>Imports one candidate.</summary>
    public ImportResult Import(ImportCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        switch (candidate.Kind)
        {
            case ImportSourceKind.Environment:
            {
                var value = getEnvironmentVariable(candidate.Source);
                return string.IsNullOrWhiteSpace(value)
                    ? ImportResult.Empty
                    : One(ImportValue(candidate.ProviderId, candidate.SuggestedName, value, candidate.AppId));
            }
            case ImportSourceKind.LegionJson:
            {
                var hash = candidate.Source.LastIndexOf('#');
                var path = candidate.Source[..hash];
                var provider = candidate.Source[(hash + 1)..];
                var value = ReadLegionKeys(path).FirstOrDefault(kv => string.Equals(kv.Key, provider, StringComparison.OrdinalIgnoreCase)).Value;
                return value is null ? ImportResult.Empty : One(ImportValue(candidate.ProviderId, candidate.SuggestedName, value, candidate.AppId));
            }
            default:
                return ImportAppScoped(candidate);
        }
    }

    /// <summary>Imports every app-scoped copy.</summary>
    public ImportResult ImportAllAppScoped() =>
        FindAppScoped().Aggregate(ImportResult.Empty, (acc, c) => acc + Import(c));

    /// <summary>
    /// Puts <paramref name="value"/> in the provider's list (reusing the name of an identical value) and,
    /// when <paramref name="appId"/> is given, makes it that app's active key ahead of its existing order.
    /// Returns the key's name and whether it was added.
    /// </summary>
    public (string Name, bool Added) ImportValue(string providerId, string name, string value, string? appId)
    {
        var provider = ProviderCatalog.Normalize(providerId);
        var clean = value.Trim();
        var existing = keys.GetKeys(provider);
        var match = existing.FirstOrDefault(k => k.Value == clean);
        var added = match is null;
        var keyName = match?.Name ?? UniqueName(existing, name);
        if (added) keys.Add(provider, keyName, clean);

        if (!string.IsNullOrWhiteSpace(appId))
        {
            var current = keys.Assignments.Get(appId!, provider);
            var order = current.Count > 0 ? current : keys.GetKeys(provider).Select(k => k.Name).ToList();
            keys.Assignments.Set(appId!, provider,
                new[] { keyName }.Concat(order.Where(n => !string.Equals(n, keyName, StringComparison.OrdinalIgnoreCase))));
        }
        return (keyName, added);
    }

    private ImportResult ImportAppScoped(ImportCandidate candidate)
    {
        var store = keys.StoreFor(candidate.ProviderId);
        var values = new List<string>();
        if (store.GetOverrideKey(candidate.Source) is { } overrideKey) values.Add(overrideKey);
        values.AddRange(store.GetPoolKeys(candidate.Source).Where(e => !e.Disabled).Select(e => e.Key));

        var result = ImportResult.Empty;
        // Import in reverse so the app's own first key ends up as its active key.
        foreach (var value in values.Distinct().Reverse())
            result += One(ImportValue(candidate.ProviderId, candidate.SuggestedName, value, candidate.AppId));

        var raw = store.LoadAllRaw();
        if (raw.Remove(candidate.Source)) store.SaveAllRaw(raw);
        var keyFile = Path.Combine(store.Directory, candidate.Source + CredentialStore.KeyFileExtension);
        if (File.Exists(keyFile)) File.Move(keyFile, keyFile + ".imported", overwrite: true);
        return result;
    }

    private static ImportResult One((string Name, bool Added) r) =>
        r.Added ? new ImportResult(new[] { r.Name }, Array.Empty<string>()) : new ImportResult(Array.Empty<string>(), new[] { r.Name });

    private static string UniqueName(IReadOnlyList<NamedKey> existing, string name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "imported" : name.Trim().Replace(':', ' ');
        if (baseName.Length > NamedKeyStore.MaxNameLength - 4) baseName = baseName[..(NamedKeyStore.MaxNameLength - 4)];
        var candidate = baseName;
        for (var n = 2; existing.Any(k => string.Equals(k.Name, candidate, StringComparison.OrdinalIgnoreCase)); n++)
            candidate = $"{baseName} {n}";
        return candidate;
    }

    private static IEnumerable<KeyValuePair<string, string>> ReadLegionKeys(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return Array.Empty<KeyValuePair<string, string>>();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("apiKeys", out var apiKeys) || apiKeys.ValueKind != JsonValueKind.Object)
                return Array.Empty<KeyValuePair<string, string>>();
            return apiKeys.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                .Select(p => new KeyValuePair<string, string>(p.Name, p.Value.GetString()!.Trim()))
                .ToList();
        }
        catch
        {
            return Array.Empty<KeyValuePair<string, string>>();
        }
    }
}
