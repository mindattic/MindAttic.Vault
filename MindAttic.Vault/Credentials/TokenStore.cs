using System.Text.Json;
using MindAttic.Vault.Paths;

namespace MindAttic.Vault.Credentials;

/// <summary>
/// Single-secret store for tokens that do not need a full provider/key/secret
/// triplet: GitHub tokens, USPS tokens, simple bearer keys, etc.
///
/// <para>On disk this is a plain <c>tokens.json</c> at
/// <c>%APPDATA%\MindAttic\&lt;bucket&gt;\</c>:</para>
/// <code>
/// {
///   "github": "ghp_...",
///   "usps":   "USPS-..."
/// }
/// </code>
///
/// <para>For richer payloads (e.g. OAuth refresh tokens, expiry, scopes) callers
/// should drop down to <see cref="CredentialStore.SaveRaw"/> and store under
/// <c>providers.json</c> instead.</para>
///
/// <para><b>Thread safety:</b> safe for concurrent reads and writes within a
/// single process — all operations are serialised through an internal lock.
/// Cross-process safety is provided by the atomic temp-file-then-rename swap
/// in <see cref="WriteAll"/>.</para>
///
/// <para><b>Case sensitivity:</b> token names are matched case-insensitively
/// (<c>"github"</c> and <c>"GitHub"</c> resolve to the same entry).</para>
/// </summary>
public sealed class TokenStore
{
    /// <summary>Filename of the on-disk tokens file.</summary>
    public const string TokensJsonFile = "tokens.json";

    // Single-process serialisation gate. Guards both reads and writes so a Set's
    // File.Replace cannot interleave with a concurrent LoadAll on the same instance.
    private readonly object writeLock = new();

    /// <summary>The bucket directory on disk. Created on first write.</summary>
    public string Directory { get; }

    /// <summary>Absolute path to <c>tokens.json</c> inside <see cref="Directory"/>.</summary>
    public string TokensFilePath => Path.Combine(Directory, TokensJsonFile);

    /// <summary>Construct a token store rooted at <paramref name="directory"/>.</summary>
    /// <param name="directory">
    /// Absolute or relative directory path. Required. The directory does not need
    /// to exist; it will be created on first write.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="directory"/> is null or whitespace.
    /// </exception>
    public TokenStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Directory is required.", nameof(directory));
        Directory = directory;
    }

    /// <summary>Convenience factory rooted at <c>%APPDATA%\MindAttic\&lt;bucket&gt;\</c>.</summary>
    /// <param name="bucket">
    /// Bucket folder name (e.g. <c>"GitHub"</c>, <c>"USPS"</c>). Required.
    /// </param>
    /// <returns>A token store rooted at the resolved roaming bucket directory.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="bucket"/> is null/whitespace.</exception>
    public static TokenStore ForBucket(string bucket) => new(VaultPaths.RoamingBucket(bucket));

    /// <summary>Resolves a single token by name.</summary>
    /// <param name="name">
    /// Logical token name. Case-insensitive. Empty/whitespace names return <c>null</c>.
    /// </param>
    /// <returns>The trimmed token value, or <c>null</c> if missing/empty/unreadable.</returns>
    public string? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var all = LoadAll();
        return all.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    /// <summary>Loads every token in the file as a case-insensitive map.</summary>
    /// <returns>
    /// A case-insensitive map of token name → value. Returns an empty map for
    /// missing or malformed files (matching the rest of the Vault's IO posture).
    /// </returns>
    public Dictionary<string, string> LoadAll()
    {
        // Hold writeLock so an in-process Set's File.Replace cannot interleave with
        // this read (cross-process consistency comes from the atomic swap in WriteAll).
        lock (writeLock)
        {
            if (!File.Exists(TokensFilePath))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var raw = File.ReadAllText(TokensFilePath);
                if (string.IsNullOrWhiteSpace(raw))
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(raw)
                             ?? new Dictionary<string, string>();
                // Normalise to case-insensitive comparer so callers can use any casing.
                return new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                // Swallow: malformed JSON behaves the same as a missing file.
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>Upserts a single token. Whitespace is trimmed; null is treated as empty.</summary>
    /// <param name="name">Logical token name. Required.</param>
    /// <param name="token">The token value (trimmed on write).</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null/whitespace.</exception>
    public void Set(string name, string token)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Token name is required.", nameof(name));

        lock (writeLock)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var all = LoadAll();
            all[name] = token?.Trim() ?? "";
            WriteAll(all);
        }
    }

    /// <summary>Removes a token by name.</summary>
    /// <param name="name">Logical token name. Empty/whitespace names are no-ops.</param>
    /// <returns>
    /// <c>true</c> if a token was removed; <c>false</c> if no entry existed
    /// (or the name was empty).
    /// </returns>
    public bool Remove(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (writeLock)
        {
            var all = LoadAll();
            if (!all.Remove(name)) return false;
            // Re-create the directory in case it was deleted between LoadAll and now.
            System.IO.Directory.CreateDirectory(Directory);
            WriteAll(all);
            return true;
        }
    }

    /// <summary>
    /// Writes the entire token map to <c>tokens.json</c> using a temp-file-then-rename
    /// dance so a concurrent reader can never observe a half-written file.
    /// </summary>
    private void WriteAll(IDictionary<string, string> tokens)
    {
        // Sort by key for a deterministic, diff-friendly on-disk file. The inner
        // dictionary uses Ordinal because keys at this point are already canonical
        // (the case-insensitive comparer would treat "GitHub" and "github" as
        // duplicates, which we don't want here — LoadAll already normalised).
        var ordered = tokens
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var json = JsonSerializer.Serialize(ordered, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        // Atomic swap: a reader process must never see a half-written tokens.json
        // (which would parse-fail and silently report all tokens as missing).
        var tempPath = TokensFilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        if (File.Exists(TokensFilePath))
            File.Replace(tempPath, TokensFilePath, TokensFilePath + ".bak");
        else
            File.Move(tempPath, TokensFilePath);
    }
}
