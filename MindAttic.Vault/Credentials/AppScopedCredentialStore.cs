namespace MindAttic.Vault.Credentials;

/// <summary>
/// Namespaces every provider id under <c>"{appId}-"</c> before delegating to an inner
/// store, so many MindAttic apps can share one physical keyring (e.g. the LLM
/// <c>providers.json</c> at <c>%APPDATA%\MindAttic\LLM\</c>) while each app's own key for
/// a given provider (<c>"claude"</c>) lives at its own entry (<c>"automata-claude"</c>,
/// <c>"tutor-claude"</c>, ...) that can never collide with another app's entry, or with
/// the unprefixed shared id every app falls back to.
///
/// <para>Compose with the shared store via <see cref="CompositeCredentialStore"/> so a
/// read tries this app's own key first, then the cross-app default:</para>
/// <code>
/// var ownThenShared = new CompositeCredentialStore(
///     new AppScopedCredentialStore("automata", LlmCredentialStore.Default),
///     LlmCredentialStore.Default);
///
/// ownThenShared.GetKey("claude");           // reads "automata-claude", else "claude"
/// ownThenShared.SetKey("claude", "sk-...."); // writes "automata-claude" only
/// </code>
///
/// <para><see cref="LoadAll"/>, <see cref="ListProviders"/>, and <see cref="LoadAllRaw"/>
/// only surface entries under this instance's own prefix, with the prefix stripped back
/// off — this is "this app's private view" of the shared keyring, not the whole file.
/// <see cref="SaveAllRaw"/> is a read-modify-write that replaces only this app's own
/// prefixed entries, leaving every other app's (and the shared, unprefixed) entries in
/// the same physical file untouched.</para>
///
/// <para>Assumes no MindAttic app id is itself a prefix of another (e.g. <c>"automata"</c>
/// and <c>"automata-beta"</c> would collide) — the same assumption every hand-written
/// <c>&lt;App&gt;ProviderId.For</c> helper this type replaces already made.</para>
/// </summary>
public sealed class AppScopedCredentialStore : ICredentialStore
{
    private readonly string prefix;
    private readonly ICredentialStore inner;

    /// <summary>Wraps <paramref name="inner"/>, scoping every provider id to <paramref name="appId"/>.</summary>
    /// <param name="appId">Short app identifier (e.g. <c>"automata"</c>). Required.</param>
    /// <param name="inner">The store to delegate to (e.g. <see cref="LlmCredentialStore.Default"/>). Required.</param>
    public AppScopedCredentialStore(string appId, ICredentialStore inner)
    {
        if (string.IsNullOrWhiteSpace(appId))
            throw new ArgumentException("appId is required.", nameof(appId));
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        prefix = appId.Trim() + "-";
    }

    /// <inheritdoc />
    public string Directory => inner.Directory;

    /// <inheritdoc />
    public string ProvidersFilePath => inner.ProvidersFilePath;

    /// <inheritdoc />
    public bool ProvidersFileExists() => inner.ProvidersFileExists();

    /// <inheritdoc />
    public string? GetKey(string providerId) =>
        string.IsNullOrWhiteSpace(providerId) ? null : inner.GetKey(prefix + providerId);

    /// <inheritdoc />
    public void SetKey(string providerId, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("providerId is required.", nameof(providerId));
        inner.SetKey(prefix + providerId, apiKey);
    }

    /// <inheritdoc />
    public Dictionary<string, string> LoadAll() => Unscope(inner.LoadAll());

    /// <inheritdoc />
    public List<string> ListProviders() => LoadAll().Keys.ToList();

    /// <inheritdoc />
    public Dictionary<string, string> LoadAllRaw() => Unscope(inner.LoadAllRaw());

    /// <inheritdoc />
    public void SaveAllRaw(IDictionary<string, string> providers)
    {
        var merged = inner.LoadAllRaw();
        foreach (var key in merged.Keys.Where(IsOwnKey).ToList())
            merged.Remove(key);
        foreach (var kv in providers)
            merged[prefix + kv.Key] = kv.Value;
        inner.SaveAllRaw(merged);
    }

    /// <inheritdoc />
    public void SaveRaw(string providerId, string rawProviderJson)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("providerId is required.", nameof(providerId));
        inner.SaveRaw(prefix + providerId, rawProviderJson);
    }

    private bool IsOwnKey(string providerId) =>
        providerId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private Dictionary<string, string> Unscope(Dictionary<string, string> scoped)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in scoped)
            if (IsOwnKey(kv.Key))
                result[kv.Key[prefix.Length..]] = kv.Value;
        return result;
    }
}
