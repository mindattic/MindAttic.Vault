namespace MindAttic.Vault.Credentials;

/// <summary>
/// Chain of credential stores. Reads walk every store in order; the first
/// non-empty value wins. Writes go to the first <em>writable</em> store
/// (i.e. the first store whose <see cref="ICredentialStore.SetKey"/> doesn't
/// throw <see cref="NotSupportedException"/>).
///
/// <para>The canonical wiring is "config first, file fallback":</para>
/// <code>
/// new CompositeCredentialStore(
///     ConfigurationCredentialStore.ForLlm(builder.Configuration),
///     LlmCredentialStore.Default);
/// </code>
///
/// <para>Reads return User Secrets / App Service / Key Vault values when present;
/// fall back to <c>%APPDATA%\MindAttic\LLM\providers.json</c> when not. Writes
/// (e.g. from a settings UI) skip the read-only configuration store and land
/// in the file store, which is right: production should never write secrets
/// from app code anyway.</para>
/// </summary>
public class CompositeCredentialStore : ICredentialStore
{
    private readonly IReadOnlyList<ICredentialStore> stores;

    public CompositeCredentialStore(params ICredentialStore[] stores)
        : this((IEnumerable<ICredentialStore>)stores) { }

    public CompositeCredentialStore(IEnumerable<ICredentialStore> stores)
    {
        if (stores is null) throw new ArgumentNullException(nameof(stores));
        this.stores = stores.Where(s => s is not null).ToList();
        if (this.stores.Count == 0)
            throw new ArgumentException("At least one inner credential store is required.", nameof(stores));
    }

    /// <summary>The first inner store whose <see cref="ICredentialStore.SetKey"/> does not throw.</summary>
    public ICredentialStore WritableStore => stores.FirstOrDefault(IsWritable) ?? stores[0];

    /// <summary>The directory of the writable store (where mutations land).</summary>
    public string Directory => WritableStore.Directory;

    /// <summary>The providers.json path of the writable store.</summary>
    public string ProvidersFilePath => WritableStore.ProvidersFilePath;

    /// <summary>True if any inner store reports a backing providers.json on disk.</summary>
    public bool ProvidersFileExists() => stores.Any(s => SafeProvidersExists(s));

    public string? GetKey(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;
        foreach (var store in stores)
        {
            string? value;
            try { value = store.GetKey(providerId); }
            catch { value = null; }
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    public void SetKey(string providerId, string apiKey) =>
        WritableStore.SetKey(providerId, apiKey);

    public Dictionary<string, string> LoadAll()
    {
        // Walk in reverse so earlier (higher-priority) stores overwrite later ones,
        // mirroring the read precedence used by GetKey.
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = stores.Count - 1; i >= 0; i--)
        {
            Dictionary<string, string>? layer;
            try { layer = stores[i].LoadAll(); }
            catch { layer = null; }
            if (layer is null) continue;
            foreach (var kv in layer)
                if (!string.IsNullOrWhiteSpace(kv.Value))
                    result[kv.Key] = kv.Value;
        }
        return result;
    }

    public List<string> ListProviders() => LoadAll().Keys.ToList();

    public Dictionary<string, string> LoadAllRaw()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = stores.Count - 1; i >= 0; i--)
        {
            Dictionary<string, string>? layer;
            try { layer = stores[i].LoadAllRaw(); }
            catch { layer = null; }
            if (layer is null) continue;
            foreach (var kv in layer)
                if (!string.IsNullOrWhiteSpace(kv.Value))
                    result[kv.Key] = kv.Value;
        }
        return result;
    }

    public void SaveAllRaw(IDictionary<string, string> providers) =>
        WritableStore.SaveAllRaw(providers);

    public void SaveRaw(string providerId, string rawProviderJson) =>
        WritableStore.SaveRaw(providerId, rawProviderJson);

    private static bool IsWritable(ICredentialStore store) =>
        store is not ConfigurationCredentialStore;

    private static bool SafeProvidersExists(ICredentialStore store)
    {
        try { return store.ProvidersFileExists(); }
        catch { return false; }
    }
}
