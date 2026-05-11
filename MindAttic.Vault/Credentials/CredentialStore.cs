using System.Text.Json;

namespace MindAttic.Vault.Credentials;

/// <summary>
/// Generic 3-tier credential store. Used directly for ad-hoc keyrings (e.g. GitHub
/// tokens), or wrapped by a domain-specific store
/// (<see cref="LlmCredentialStore"/>, <see cref="BrokerCredentialStore"/>) when
/// callers need richer per-provider payloads.
///
/// <para><b>On-disk layout (rooted at <see cref="Directory"/>):</b></para>
/// <list type="bullet">
///   <item><description><c>&lt;providerId&gt;.key</c> — single-line override (highest priority).</description></item>
///   <item><description><c>providers.json</c> — canonical rich format <c>{ id: { apiKey, ... } }</c>.</description></item>
///   <item><description><c>credentials.json</c> — legacy flat format <c>{ id: "key" }</c> (lowest priority).</description></item>
/// </list>
///
/// <para><b>IO posture:</b> all file I/O is best-effort. Parse failures, missing files,
/// or transient IO errors surface as "no credential found" rather than exceptions.
/// Writes are serialised through <see cref="WriteProvidersJson"/>'s atomic
/// temp-file-then-replace dance so a reader can never observe a half-written
/// <c>providers.json</c>.</para>
///
/// <para><b>Thread safety:</b> safe for concurrent reads. Writes within a single
/// process are serialised by an internal lock. Cross-process safety relies on the
/// atomic <see cref="File.Replace(string, string, string?)"/> swap.</para>
/// </summary>
public class CredentialStore : ICredentialStore
{
    /// <summary>Filename of the canonical rich-format providers file.</summary>
    public const string ProvidersJsonFile   = "providers.json";

    /// <summary>Filename of the legacy flat-format credentials file.</summary>
    public const string CredentialsJsonFile = "credentials.json";

    /// <summary>Extension for per-provider override key files (e.g. <c>claude.key</c>).</summary>
    public const string KeyFileExtension    = ".key";

    // Single-process serialisation gate for all mutating operations. The atomic
    // temp-file swap in WriteProvidersJson handles cross-process safety.
    private readonly object writeLock = new();

    /// <summary>The bucket directory on disk. Does not have to exist yet — it's created lazily on first write.</summary>
    public string Directory { get; }

    /// <summary>Absolute path to <c>providers.json</c> inside <see cref="Directory"/>.</summary>
    public string ProvidersFilePath => Path.Combine(Directory, ProvidersJsonFile);

    /// <summary>Construct a credential store rooted at <paramref name="directory"/>.</summary>
    /// <param name="directory">
    /// Absolute or relative directory path. Required. The directory does not need
    /// to exist; it will be created on first write.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="directory"/> is null or whitespace.
    /// </exception>
    public CredentialStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Directory is required.", nameof(directory));
        Directory = directory;
    }

    /// <inheritdoc />
    public bool ProvidersFileExists() => File.Exists(ProvidersFilePath);

    /// <inheritdoc />
    public string? GetKey(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;
        // No directory means no keys — short-circuit before any file probe.
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

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="providerId"/> is null or whitespace.
    /// </exception>
    public void SetKey(string providerId, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("Provider ID is required.", nameof(providerId));

        // Treat null as empty — the caller may be intentionally clearing a key.
        var trimmed = apiKey?.Trim() ?? "";

        lock (writeLock)
        {
            System.IO.Directory.CreateDirectory(Directory);

            // Read-modify-write: pull the current map, splice in the new apiKey
            // (subclasses get to decide how the splice handles their richer schema),
            // then atomically replace the file.
            var providers = LoadProvidersRawSafe();
            providers[providerId] = MergeApiKeyIntoProviderJson(
                existingJson: providers.TryGetValue(providerId, out var existing) ? existing : null,
                providerId: providerId,
                apiKey: trimmed);

            WriteProvidersJson(providers);
        }
    }

    /// <inheritdoc />
    public Dictionary<string, string> LoadAll()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!System.IO.Directory.Exists(Directory)) return result;

        // Layer in priority order — each subsequent layer overwrites earlier ones.
        // This mirrors GetKey's precedence: .key files > providers.json > credentials.json.

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

    /// <inheritdoc />
    public List<string> ListProviders() => LoadAll().Keys.ToList();

    /// <inheritdoc />
    public Dictionary<string, string> LoadAllRaw() => LoadProvidersRawSafe();

    /// <inheritdoc />
    public void SaveAllRaw(IDictionary<string, string> providers)
    {
        // Tolerant: a null map is a no-op rather than an exception. Callers that
        // wanted to wipe the file pass an empty map, not null.
        if (providers is null) return;
        lock (writeLock)
        {
            System.IO.Directory.CreateDirectory(Directory);
            WriteProvidersJson(providers);
        }
    }

    /// <inheritdoc />
    public void SaveRaw(string providerId, string rawProviderJson)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;
        lock (writeLock)
        {
            var providers = LoadProvidersRawSafe();
            // Empty payload normalised to {} so the on-disk file is always valid JSON.
            providers[providerId] = string.IsNullOrWhiteSpace(rawProviderJson) ? "{}" : rawProviderJson;
            System.IO.Directory.CreateDirectory(Directory);
            WriteProvidersJson(providers);
        }
    }

    // ── providers.json helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Reads <c>providers.json</c> and returns each provider entry as raw JSON text.
    /// Returns an empty map (not an exception) for missing/malformed files.
    /// </summary>
    private Dictionary<string, string> LoadProvidersRawSafe()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(ProvidersFilePath)) return result;

            var raw = File.ReadAllText(ProvidersFilePath);
            if (string.IsNullOrWhiteSpace(raw)) return result;

            using var doc = JsonDocument.Parse(raw);
            // Defensive: a top-level array or scalar is malformed in this schema.
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Skip any value that isn't itself an object (the schema is
                // { providerId: { ... } }; anything else is treated as garbage).
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                result[prop.Name] = prop.Value.GetRawText();
            }
        }
        catch { /* swallow — missing/malformed file means "no credentials". */ }
        return result;
    }

    /// <summary>Reads a single provider's apiKey from <c>providers.json</c>.</summary>
    private string? TryReadProvidersJsonKey(string providerId)
    {
        var providers = LoadProvidersRawSafe();
        return providers.TryGetValue(providerId, out var json)
            ? ExtractApiKeyFromProviderJson(json)
            : null;
    }

    /// <summary>Pulls the <c>apiKey</c> string field out of a per-provider JSON object.</summary>
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
        catch { /* swallow — same posture as the rest of the store. */ }
        return null;
    }

    /// <summary>
    /// Default merge: replace only <c>apiKey</c>, preserve every other field on the
    /// existing provider object. Subclasses override this when they know about
    /// richer schemas (LLM type/model/maxTokens, broker secret/baseUrl, etc.).
    /// </summary>
    /// <param name="existingJson">
    /// The current per-provider JSON object (may be null if the provider is new).
    /// </param>
    /// <param name="providerId">
    /// The provider being upserted. Available so subclasses can infer schema
    /// defaults (e.g. <c>type</c>) from the id.
    /// </param>
    /// <param name="apiKey">The trimmed key to write into the entry.</param>
    /// <returns>
    /// A pretty-printed JSON object string suitable for writing into
    /// <c>providers.json</c>.
    /// </returns>
    protected virtual string MergeApiKeyIntoProviderJson(string? existingJson, string providerId, string apiKey)
    {
        // Parse existing into a mutable map of name → raw JSON text. We deliberately
        // round-trip through string instead of JsonNode to keep the dependency
        // surface minimal (Microsoft.Extensions.Configuration only).
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
            catch { /* malformed existing entry — start clean. */ }
        }

        // Replace apiKey. Serialize through JsonSerializer so embedded quotes/
        // backslashes are escaped correctly.
        existing["apiKey"] = JsonSerializer.Serialize(apiKey);

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (var kv in existing)
            {
                w.WritePropertyName(kv.Key);
                // Round-trip raw JSON so non-string fields (numbers, booleans,
                // nested objects) are preserved verbatim.
                using var subDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(kv.Value) ? "null" : kv.Value);
                subDoc.RootElement.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Writes the provider map to <c>providers.json</c> using a temp-file-then-rename
    /// dance so a concurrent reader can never observe a half-written file.
    /// </summary>
    private void WriteProvidersJson(IDictionary<string, string> providers)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            // Sort keys so the on-disk file is deterministic — friendlier to git diffs
            // and easier to eyeball.
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
                    // Defensive: unparseable entry becomes an empty object rather
                    // than corrupting the rest of the file.
                    w.WriteStartObject();
                    w.WriteEndObject();
                }
            }
            w.WriteEndObject();
        }

        // Atomic swap: a reader process must never see a half-written providers.json
        // (which would parse-fail and silently report all credentials as missing).
        var tempPath = ProvidersFilePath + ".tmp";
        File.WriteAllBytes(tempPath, ms.ToArray());
        if (File.Exists(ProvidersFilePath))
            File.Replace(tempPath, ProvidersFilePath, ProvidersFilePath + ".bak");
        else
            File.Move(tempPath, ProvidersFilePath);
    }

    // ── small helpers ───────────────────────────────────────────────────────────

    /// <summary>Reads a file's contents, returning <c>null</c> on any IO error.</summary>
    private static string? ReadFileSafe(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
    }

    /// <summary>
    /// Parses a flat <c>{ id: "key", ... }</c> JSON file. Returns an empty map for
    /// missing/malformed files.
    /// </summary>
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
