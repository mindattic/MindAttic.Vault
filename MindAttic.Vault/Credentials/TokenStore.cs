using System.Text.Json;
using MindAttic.Vault.Paths;

namespace MindAttic.Vault.Credentials;

/// <summary>
/// Single-secret store for tokens that do not need a full provider/key/secret triplet:
/// GitHub tokens, USPS tokens, simple bearer keys, etc.
///
/// On disk this is a plain <c>tokens.json</c> at <c>%APPDATA%\MindAttic\&lt;bucket&gt;\</c>:
/// <code>
/// {
///   "github": "ghp_...",
///   "usps":   "USPS-..."
/// }
/// </code>
///
/// For richer payloads (e.g. OAuth refresh tokens, expiry, scopes) callers should drop
/// down to <see cref="CredentialStore.SaveRaw"/> and store under <c>providers.json</c>
/// instead.
/// </summary>
public sealed class TokenStore
{
    public const string TokensJsonFile = "tokens.json";

    private readonly object writeLock = new();
    public string Directory { get; }
    public string TokensFilePath => Path.Combine(Directory, TokensJsonFile);

    public TokenStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Directory is required.", nameof(directory));
        Directory = directory;
    }

    /// <summary>Convenience factory rooted at <c>%APPDATA%\MindAttic\&lt;bucket&gt;\</c>.</summary>
    public static TokenStore ForBucket(string bucket) => new(VaultPaths.RoamingBucket(bucket));

    public string? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var all = LoadAll();
        return all.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    public Dictionary<string, string> LoadAll()
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
            return new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

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

    public bool Remove(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (writeLock)
        {
            var all = LoadAll();
            if (!all.Remove(name)) return false;
            System.IO.Directory.CreateDirectory(Directory);
            WriteAll(all);
            return true;
        }
    }

    private void WriteAll(IDictionary<string, string> tokens)
    {
        var ordered = tokens
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var json = JsonSerializer.Serialize(ordered, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(TokensFilePath, json);
    }
}
