using System.Text.Json;
using System.Text.Json.Nodes;

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
public class CredentialStore : ICredentialStore, IRotatingKeyStore
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
    /// <remarks>Equivalent to the first entry of <see cref="GetKeys"/>.</remarks>
    public string? GetKey(string providerId) => GetKeys(providerId).FirstOrDefault()?.Key;

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="providerId"/> is null or whitespace.
    /// </exception>
    /// <remarks>Equivalent to <see cref="SetKeys"/> with a single-entry pool.</remarks>
    public void SetKey(string providerId, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("Provider ID is required.", nameof(providerId));

        // Treat null as empty — the caller may be intentionally clearing a key.
        SetKeys(providerId, new[] { new CredentialPoolEntry(apiKey ?? "") });
    }

    /// <inheritdoc />
    public IReadOnlyList<CredentialPoolEntry> GetKeys(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return Array.Empty<CredentialPoolEntry>();
        // No directory means no keys — short-circuit before any file probe.
        if (!System.IO.Directory.Exists(Directory)) return Array.Empty<CredentialPoolEntry>();

        // 1. Per-provider .key file (highest priority — manual override). Always a
        // single-key "pool" — there's no multi-key syntax for this override file.
        var keyFile = Path.Combine(Directory, providerId + KeyFileExtension);
        if (File.Exists(keyFile))
        {
            var raw = ReadFileSafe(keyFile);
            if (!string.IsNullOrWhiteSpace(raw))
                return new[] { new CredentialPoolEntry(raw.Trim()) };
        }

        // 2. providers.json (canonical rich format) — the "apiKeys" pool array when
        // present, else the single "apiKey" field wrapped as a one-element pool.
        var providers = LoadProvidersRawSafe();
        if (providers.TryGetValue(providerId, out var json))
        {
            var pool = ExtractApiKeysFromProviderJson(json);
            if (pool.Count > 0) return pool;
        }

        // 3. credentials.json (legacy flat format).
        var jsonFile = Path.Combine(Directory, CredentialsJsonFile);
        if (File.Exists(jsonFile))
        {
            var all = ParseFlatJsonSafe(jsonFile);
            if (all.TryGetValue(providerId, out var key) && !string.IsNullOrWhiteSpace(key))
                return new[] { new CredentialPoolEntry(key.Trim()) };
        }

        return Array.Empty<CredentialPoolEntry>();
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="providerId"/> is null or whitespace.
    /// </exception>
    public void SetKeys(string providerId, IReadOnlyList<CredentialPoolEntry> keys)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("Provider ID is required.", nameof(providerId));

        var pool = (keys ?? Array.Empty<CredentialPoolEntry>())
            .Where(k => !string.IsNullOrWhiteSpace(k.Key))
            .Select(k => new CredentialPoolEntry(k.Key.Trim(), string.IsNullOrWhiteSpace(k.Label) ? null : k.Label!.Trim()))
            .ToList();

        lock (writeLock)
        {
            System.IO.Directory.CreateDirectory(Directory);

            // Read-modify-write: pull the current map, splice in the new primary
            // apiKey (subclasses get to decide how the splice handles their richer
            // schema) then layer the full pool on top, then atomically replace the file.
            var providers = LoadProvidersRawSafe();
            var existingJson = providers.TryGetValue(providerId, out var existing) ? existing : null;
            var primaryKey = pool.Count > 0 ? pool[0].Key : "";
            var withPrimary = MergeApiKeyIntoProviderJson(existingJson, providerId, primaryKey);
            providers[providerId] = MergeApiKeysIntoProviderJson(withPrimary, pool);

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
    private static string? ExtractApiKeyFromProviderJson(string? json) =>
        ExtractApiKeysFromProviderJson(json).FirstOrDefault()?.Key;

    /// <summary>
    /// Pulls the key pool out of a per-provider JSON object: the <c>apiKeys</c> array
    /// when present and non-empty, else the single <c>apiKey</c> field wrapped as a
    /// one-element pool.
    /// </summary>
    private static List<CredentialPoolEntry> ExtractApiKeysFromProviderJson(string? json)
    {
        var result = new List<CredentialPoolEntry>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;

            if (doc.RootElement.TryGetProperty("apiKeys", out var apiKeys) && apiKeys.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in apiKeys.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    if (!entry.TryGetProperty("key", out var keyProp) || keyProp.ValueKind != JsonValueKind.String) continue;
                    var key = keyProp.GetString();
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    var label = entry.TryGetProperty("label", out var labelProp) && labelProp.ValueKind == JsonValueKind.String
                        ? labelProp.GetString()
                        : null;
                    result.Add(new CredentialPoolEntry(key.Trim(), label));
                }
                if (result.Count > 0) return result;
            }

            if (doc.RootElement.TryGetProperty("apiKey", out var single) && single.ValueKind == JsonValueKind.String)
            {
                var key = single.GetString();
                if (!string.IsNullOrWhiteSpace(key)) result.Add(new CredentialPoolEntry(key.Trim()));
            }
        }
        catch { /* swallow — same posture as the rest of the store. */ }
        return result;
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

        // Strip any case-variant of "apiKey" (a hand-edited file might have "ApiKey")
        // so we don't emit two competing properties after the canonical assignment below.
        foreach (var k in existing.Keys.Where(k => k.Equals("apiKey", StringComparison.OrdinalIgnoreCase) && k != "apiKey").ToList())
            existing.Remove(k);

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
    /// Splices an <c>apiKeys</c> pool array onto an already-built provider JSON object
    /// (the output of <see cref="MergeApiKeyIntoProviderJson"/>, which has already
    /// handled the primary <c>apiKey</c> mirror and any schema-specific fields).
    /// Only emits the array when there's more than one key — a provider with a
    /// single key keeps looking exactly like it did before this feature existed.
    /// </summary>
    /// <param name="providerJson">The provider's JSON object after the primary-key merge.</param>
    /// <param name="pool">The full pool being written, in priority order.</param>
    protected virtual string MergeApiKeysIntoProviderJson(string providerJson, IReadOnlyList<CredentialPoolEntry> pool)
    {
        JsonObject obj;
        try
        {
            obj = JsonNode.Parse(providerJson) as JsonObject ?? new JsonObject();
        }
        catch
        {
            obj = new JsonObject();
        }

        obj.Remove("apiKeys");
        if (pool.Count > 1)
        {
            var arr = new JsonArray();
            foreach (var entry in pool)
            {
                var entryObj = new JsonObject { ["key"] = entry.Key };
                if (!string.IsNullOrWhiteSpace(entry.Label)) entryObj["label"] = entry.Label;
                arr.Add(entryObj);
            }
            obj["apiKeys"] = arr;
        }

        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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
        // Use a unique temp name per write — a fixed "providers.json.tmp" would let
        // two concurrent writers (separate instances, or separate processes — the
        // in-process writeLock guards neither) clobber each other's temp file and
        // race the swap, defeating the very atomicity this dance exists to provide.
        var tempPath = ProvidersFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(tempPath, ms.ToArray());
            if (File.Exists(ProvidersFilePath))
                File.Replace(tempPath, ProvidersFilePath, ProvidersFilePath + ".bak");
            else
                File.Move(tempPath, ProvidersFilePath);
        }
        catch
        {
            // Never leave an orphan temp behind if the write/swap failed.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort */ }
            throw;
        }
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
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed is null) return result;
            // Re-wrap with a case-insensitive comparer so legacy credentials.json
            // lookups honour the case-insensitive provider-id contract documented
            // on ICredentialStore.GetKey (the .key and providers.json layers already
            // resolve case-insensitively; this layer must match). A plain
            // Deserialize yields an Ordinal/case-sensitive map.
            //
            // Copy entry-by-entry (last write wins) instead of using the
            // OrdinalIgnoreCase copy-constructor: a hand-edited credentials.json with
            // two case-variant keys ("openai" + "OpenAI") deserialises into an ordinal
            // map holding BOTH, and the copy-constructor would throw on the duplicate —
            // which the catch below swallows, silently dropping EVERY legacy credential.
            foreach (var kv in parsed)
                result[kv.Key] = kv.Value;
            return result;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
