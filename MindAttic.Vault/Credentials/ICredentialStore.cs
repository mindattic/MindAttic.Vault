namespace MindAttic.Vault.Credentials;

/// <summary>
/// A keyring rooted under <c>%APPDATA%\MindAttic\&lt;bucket&gt;\</c>. Resolves
/// per-provider credentials in this order (highest priority first):
///
///   1. <c>&lt;providerId&gt;.key</c>   — single-line override file (raw key, trimmed).
///   2. <c>providers.json</c>     — canonical rich format:
///                                   <c>{ providerId: { type, apiKey, ... }, ... }</c>
///   3. <c>credentials.json</c>   — legacy flat format:
///                                   <c>{ providerId: "key", ... }</c>
/// </summary>
public interface ICredentialStore
{
    /// <summary>The keyring directory on disk (e.g. <c>%APPDATA%\MindAttic\LLM</c>).</summary>
    string Directory { get; }

    /// <summary>Path to the canonical rich-format providers.json file.</summary>
    string ProvidersFilePath { get; }

    /// <summary>True if providers.json exists.</summary>
    bool ProvidersFileExists();

    /// <summary>Resolved API key for <paramref name="providerId"/>, or null if absent.</summary>
    string? GetKey(string providerId);

    /// <summary>Upserts a provider's apiKey, preserving sibling fields (type/model/secret/baseUrl/...).</summary>
    void SetKey(string providerId, string apiKey);

    /// <summary>All credentials as a flat map (providerId → apiKey), merged from every layer.</summary>
    Dictionary<string, string> LoadAll();

    /// <summary>Provider IDs that currently have a non-empty credential on disk.</summary>
    List<string> ListProviders();

    /// <summary>
    /// providers.json as a map of providerId → raw per-provider JSON object string.
    /// Use this when you need access to fields beyond <c>apiKey</c> (e.g. model, secret, baseUrl).
    /// </summary>
    Dictionary<string, string> LoadAllRaw();

    /// <summary>Atomic replace of providers.json with the supplied map.</summary>
    void SaveAllRaw(IDictionary<string, string> providers);

    /// <summary>Upsert a single provider's raw JSON object, preserving every other entry.</summary>
    void SaveRaw(string providerId, string rawProviderJson);
}
