using System.Text.Json;

namespace MindAttic.Vault.Credentials;

/// <summary>
/// Generic 3-tier credential store. Used directly for ad-hoc keyrings (e.g. GitHub tokens),
/// or wrapped by a domain-specific store (<see cref="LlmCredentialStore"/>,
/// <see cref="BrokerCredentialStore"/>) when callers need richer per-provider payloads.
///
/// All file I/O is best-effort: parse failures, missing files, or transient IO errors
/// surface as "no credential found" rather than exceptions, mirroring the swallow-and-skip
/// behavior the rest of the MindAttic family already relies on.
/// </summary>
public class CredentialStore : ICredentialStore
{
    public const string ProvidersJsonFile   = "providers.json";
    public const string CredentialsJsonFile = "credentials.json";
    public const string KeyFileExtension    = ".key";

    private readonly object writeLock = new();

    /// <summary>The bucket directory on disk (does not have to exist yet).</summary>
    public string Directory { get; }

    public string ProvidersFilePath => Path.Combine(Directory, ProvidersJsonFile);

    /// <summary>Construct a credential store rooted at <paramref name="directory"/>.</summary>
    public CredentialStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Directory is required.", nameof(directory));
        Directory = directory;
    }

    public bool ProvidersFileExists() => File.Exists(ProvidersFilePath);

    public string? GetKey(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;
        if (!System.IO.Directory.Exists(Directory)) return null;

        // 1. Per-provider .key file (highest priority — manual override).
        var keyFile = Path.Combine(Directory, providerId + KeyFileExtension);
        if (File.Exists(keyFile))
        {
            var raw = ReadFileSafe(keyFile);
            if (!string.IsNullOrWhiteSpace(raw)) return raw.Trim();
        }

        // 2. providers.json (canonical rich format).
        var fromProviders = TryReadProvidersJsonKey(providerId);
        if (!string.IsNullOrWhiteSpace(fromProviders)) return fromProviders.Trim();

        // 3. credentials.json (legacy flat format).
        var jsonFile = Path.Combine(Directory, CredentialsJsonFile);
        if (File.Exists(jsonFile))
        {
            var all = ParseFlatJsonSafe(jsonFile);
            if (all.TryGetValue(providerId, out var key) && !string.IsNullOrWhiteSpace(key))
                return key.Trim();
        }

        return null;
    }

    public void SetKey(string providerId, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("Provider ID is required.", nameof(providerId));

        var trimmed = apiKey?.Trim() ?? "";

        lock (writeLock)
        {
            System.IO.Directory.CreateDirectory(Directory);

            var providers = LoadProvidersRawSafe();
            providers[providerId] = MergeApiKeyIntoProviderJson(
                existingJson: providers.TryGetValue(providerId, out var existing) ? existing : null,
                providerId: providerId,
                apiKey: trimmed);

            WriteProvidersJson(providers);
        }
    }

    public Dictionary<string, string> LoadAll()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!System.IO.Directory.Exists(Directory)) return result;

        // Lowest priority: credentials.json.
        var jsonFile = Path.Combine(Directory, CredentialsJsonFile);
        if (File.Exists(jsonFile))
        {
            foreach (var kv in ParseFlatJsonSafe(jsonFile))
                if (!string.IsNullOrWhiteSpace(kv.Value))
                    result[kv.Key] = kv.Value.Trim();
        }

        // Middle priority: providers.json.
        foreach (var kv in LoadProvidersRawSafe())
        {
            var key = ExtractApiKeyFromProviderJson(kv.Value);
            if (!string.IsNullOrWhiteSpace(key))
                result[kv.Key] = key.Trim();
        }

        // Highest priority: .key files.
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*" + KeyFileExtension))
        {
            var providerId = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(providerId)) continue;
            var raw = ReadFileSafe(file);
            if (!string.IsNullOrWhiteSpace(raw))
                result[providerId] = raw.Trim();
        }

        return result;
    }

    public List<string> ListProviders() => LoadAll().Keys.ToList();

    public Dictionary<string, string> LoadAllRaw() => LoadProvidersRawSafe();

    public void SaveAllRaw(IDictionary<string, string> providers)
    {
        if (providers is null) return;
        lock (writeLock)
        {
            System.IO.Directory.CreateDirectory(Directory);
            WriteProvidersJson(providers);
        }
    }

    public void SaveRaw(string providerId, string rawProviderJson)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;
        lock (writeLock)
        {
            var providers = LoadProvidersRawSafe();
            providers[providerId] = string.IsNullOrWhiteSpace(rawProviderJson) ? "{}" : rawProviderJson;
            System.IO.Directory.CreateDirectory(Directory);
            WriteProvidersJson(providers);
        }
    }

    // ── providers.json helpers ──────────────────────────────────────────────────

    private Dictionary<string, string> LoadProvidersRawSafe()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(ProvidersFilePath)) return result;

            var raw = File.ReadAllText(ProvidersFilePath);
            if (string.IsNullOrWhiteSpace(raw)) return result;

            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                result[prop.Name] = prop.Value.GetRawText();
            }
        }
        catch { }
        return result;
    }

    private string? TryReadProvidersJsonKey(string providerId)
    {
        var providers = LoadProvidersRawSafe();
        return providers.TryGetValue(providerId, out var json)
            ? ExtractApiKeyFromProviderJson(json)
            : null;
    }

    private static string? ExtractApiKeyFromProviderJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("apiKey", out var apiKey)
                && apiKey.ValueKind == JsonValueKind.String)
            {
                return apiKey.GetString();
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Default merge: replace only <c>apiKey</c>, preserve every other field on the
    /// existing provider object. Subclasses override this when they know about
    /// richer schemas (LLM type/model/maxTokens, broker secret/baseUrl, etc.).
    /// </summary>
    protected virtual string MergeApiKeyIntoProviderJson(string? existingJson, string providerId, string apiKey)
    {
        // Parse existing into a mutable map of name → raw JSON text.
        var existing = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(existingJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        existing[prop.Name] = prop.Value.GetRawText();
                }
            }
            catch { }
        }

        // Replace apiKey.
        existing["apiKey"] = JsonSerializer.Serialize(apiKey);

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (var kv in existing)
            {
                w.WritePropertyName(kv.Key);
                using var subDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(kv.Value) ? "null" : kv.Value);
                subDoc.RootElement.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private void WriteProvidersJson(IDictionary<string, string> providers)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (var (providerId, json) in providers.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                w.WritePropertyName(providerId);
                try
                {
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    doc.RootElement.WriteTo(w);
                }
                catch
                {
                    w.WriteStartObject();
                    w.WriteEndObject();
                }
            }
            w.WriteEndObject();
        }
        File.WriteAllBytes(ProvidersFilePath, ms.ToArray());
    }

    // ── small helpers ───────────────────────────────────────────────────────────

    private static string? ReadFileSafe(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
    }

    private static Dictionary<string, string> ParseFlatJsonSafe(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }
}
