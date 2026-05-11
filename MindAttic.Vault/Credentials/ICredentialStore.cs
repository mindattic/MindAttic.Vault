namespace MindAttic.Vault.Credentials;

/// <summary>
/// A keyring rooted under <c>%APPDATA%\MindAttic\&lt;bucket&gt;\</c>. Resolves
/// per-provider credentials in this order (highest priority first):
///
/// <list type="number">
///   <item><description>
///     <c>&lt;providerId&gt;.key</c> — single-line override file containing the raw key
///     (whitespace is trimmed). Useful for ad-hoc local overrides without editing JSON.
///   </description></item>
///   <item><description>
///     <c>providers.json</c> — canonical rich format:
///     <c>{ providerId: { type, apiKey, ... }, ... }</c>. This is what
///     <see cref="SetKey"/> writes to.
///   </description></item>
///   <item><description>
///     <c>credentials.json</c> — legacy flat format:
///     <c>{ providerId: "key", ... }</c>. Read-only fallback for migrations
///     from older MindAttic apps.
///   </description></item>
/// </list>
///
/// <para>All read operations are best-effort: missing files, malformed JSON, and
/// transient IO errors surface as "no credential" rather than exceptions, mirroring
/// the swallow-and-skip behavior the rest of the MindAttic family already relies on.</para>
///
/// <para>Implementations include the file-backed <see cref="CredentialStore"/>,
/// the read-only <see cref="ConfigurationCredentialStore"/> (User Secrets / App
/// Service / Key Vault), and the chained <see cref="CompositeCredentialStore"/>.</para>
/// </summary>
public interface ICredentialStore
{
    /// <summary>The keyring directory on disk (e.g. <c>%APPDATA%\MindAttic\LLM</c>).</summary>
    /// <remarks>
    /// For <see cref="ConfigurationCredentialStore"/> this is a synthetic
    /// <c>"(configuration)"</c> sentinel, not a real path.
    /// </remarks>
    string Directory { get; }

    /// <summary>Path to the canonical rich-format <c>providers.json</c> file.</summary>
    string ProvidersFilePath { get; }

    /// <summary>True if this store reports a backing <c>providers.json</c> (or its equivalent).</summary>
    /// <returns>
    /// <c>true</c> when at least one credential is currently surfaced by this store,
    /// <c>false</c> otherwise.
    /// </returns>
    bool ProvidersFileExists();

    /// <summary>Resolves a single API key by provider id.</summary>
    /// <param name="providerId">
    /// Logical provider name (e.g. <c>"claude"</c>, <c>"alpaca-paper"</c>). Case-insensitive.
    /// Empty / whitespace ids return <c>null</c>.
    /// </param>
    /// <returns>
    /// The trimmed API key when found, or <c>null</c> when the provider is missing,
    /// has an empty key, or the underlying store can't be read.
    /// </returns>
    string? GetKey(string providerId);

    /// <summary>
    /// Upserts a provider's <c>apiKey</c>, preserving every sibling field already on
    /// the entry (<c>type</c>, <c>model</c>, <c>secret</c>, <c>baseUrl</c>, custom fields, etc.).
    /// </summary>
    /// <param name="providerId">Logical provider name. Required.</param>
    /// <param name="apiKey">The new key. Whitespace is trimmed; null is treated as empty.</param>
    /// <exception cref="System.ArgumentException">
    /// Thrown when <paramref name="providerId"/> is null or whitespace.
    /// </exception>
    /// <exception cref="System.NotSupportedException">
    /// Thrown by read-only implementations such as <see cref="ConfigurationCredentialStore"/>.
    /// </exception>
    void SetKey(string providerId, string apiKey);

    /// <summary>All credentials as a flat map (<c>providerId</c> → <c>apiKey</c>).</summary>
    /// <returns>
    /// A case-insensitive map merged from every layer (key files, providers.json,
    /// credentials.json), with the highest-priority value winning per provider.
    /// </returns>
    Dictionary<string, string> LoadAll();

    /// <summary>Provider IDs that currently have a non-empty credential on disk.</summary>
    /// <returns>An unordered list of provider ids.</returns>
    List<string> ListProviders();

    /// <summary>
    /// <c>providers.json</c> as a map of <c>providerId</c> → raw per-provider JSON object string.
    /// </summary>
    /// <remarks>
    /// Use this when you need access to fields beyond <c>apiKey</c> (e.g. <c>model</c>,
    /// <c>secret</c>, <c>baseUrl</c>). The values are the raw JSON text of each provider
    /// object — parse them yourself or hand them to a typed deserializer.
    /// </remarks>
    /// <returns>A case-insensitive map; empty if the store has no rich-format data.</returns>
    Dictionary<string, string> LoadAllRaw();

    /// <summary>Atomic replace of <c>providers.json</c> with the supplied map.</summary>
    /// <param name="providers">Map of provider id → raw provider JSON object.</param>
    /// <exception cref="System.NotSupportedException">
    /// Thrown by read-only implementations.
    /// </exception>
    void SaveAllRaw(IDictionary<string, string> providers);

    /// <summary>Upsert a single provider's raw JSON object, preserving every other entry.</summary>
    /// <param name="providerId">Logical provider name. Required.</param>
    /// <param name="rawProviderJson">
    /// The raw JSON object string for the provider. <c>null</c> or whitespace
    /// is normalised to an empty object (<c>{}</c>).
    /// </param>
    /// <exception cref="System.NotSupportedException">
    /// Thrown by read-only implementations.
    /// </exception>
    void SaveRaw(string providerId, string rawProviderJson);
}
