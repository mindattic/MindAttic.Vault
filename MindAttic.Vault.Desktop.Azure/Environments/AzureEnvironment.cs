using MindAttic.Vault.Settings;

namespace MindAttic.Vault.Desktop.Azure.Environments;

/// <summary>
/// One Azure deployment target: a name plus the Key Vault that holds secret values and the App Configuration
/// store that holds every name (settings, key references, order, assignments). Non-secret: resource ids and URIs only.
/// </summary>
public sealed class AzureEnvironment
{
    /// <summary>What the user calls it, e.g. <c>Production</c>. Unique (case-insensitive).</summary>
    public string Name { get; set; } = "";

    public string SubscriptionId { get; set; } = "";
    public string? TenantId { get; set; }

    /// <summary>ARM id of the Key Vault (<c>/subscriptions/.../providers/Microsoft.KeyVault/vaults/{name}</c>).</summary>
    public string KeyVaultResourceId { get; set; } = "";
    public string KeyVaultName { get; set; } = "";

    /// <summary>The vault's data-plane URI, e.g. <c>https://myvault.vault.azure.net/</c>.</summary>
    public string KeyVaultUri { get; set; } = "";

    /// <summary>ARM id of the App Configuration store.</summary>
    public string AppConfigurationResourceId { get; set; } = "";
    public string AppConfigurationName { get; set; } = "";

    /// <summary>The store's data-plane endpoint, e.g. <c>https://mystore.azconfig.io</c>.</summary>
    public string AppConfigurationEndpoint { get; set; } = "";

    /// <summary>True when both resources are set and their URIs are absolute HTTPS.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Name)
        && IsHttps(KeyVaultUri) && IsHttps(AppConfigurationEndpoint);

    private static bool IsHttps(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    public AzureEnvironment Clone() => (AzureEnvironment)MemberwiseClone();

    public override string ToString() => Name;
}

/// <summary>The remembered environments (<c>%APPDATA%\MindAttic\Vault.Desktop\azure-environments.json</c>).</summary>
public sealed class AzureEnvironmentsFile
{
    public List<AzureEnvironment> Environments { get; set; } = new();

    /// <summary>The environment the Azure Profile uses, by name.</summary>
    public string? Selected { get; set; }
}

/// <summary>Remembers environments as JSON through <see cref="JsonSettingsStore{T}"/> (atomic writes, corrupt → empty).</summary>
public sealed class AzureEnvironmentStore
{
    public const string Bucket = "Vault.Desktop";
    public const string FileName = "azure-environments.json";

    private readonly JsonSettingsStore<AzureEnvironmentsFile> store;
    private readonly object gate = new();

    public AzureEnvironmentStore(string directory) => store = new JsonSettingsStore<AzureEnvironmentsFile>(directory, FileName);

    /// <summary>The store in the roaming <c>Vault.Desktop</c> bucket.</summary>
    public static AzureEnvironmentStore ForRoamingRoot() => new(Paths.VaultPaths.RoamingBucket(Bucket));

    public string FilePath => store.FilePath;

    /// <summary>Raised after any change.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<AzureEnvironment> All()
    {
        lock (gate) return store.Load().Environments.Where(e => !string.IsNullOrWhiteSpace(e.Name)).ToList();
    }

    /// <summary>The selected environment, or the only one when exactly one exists, or null.</summary>
    public AzureEnvironment? Current()
    {
        lock (gate)
        {
            var file = store.Load();
            var envs = file.Environments.Where(e => !string.IsNullOrWhiteSpace(e.Name)).ToList();
            return envs.FirstOrDefault(e => string.Equals(e.Name, file.Selected, StringComparison.OrdinalIgnoreCase))
                   ?? (envs.Count == 1 ? envs[0] : null);
        }
    }

    /// <summary>Adds or replaces (by name) an environment and selects it.</summary>
    public void Save(AzureEnvironment environment, bool select = true)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var name = environment.Name?.Trim() ?? "";
        if (name.Length == 0) throw new ArgumentException("Environment name is required.", nameof(environment));
        if (!environment.IsComplete) throw new ArgumentException("Pick both a Key Vault and an App Configuration store.", nameof(environment));
        var copy = environment.Clone();
        copy.Name = name;
        lock (gate)
        {
            var file = store.Load();
            file.Environments.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            file.Environments.Add(copy);
            if (select) file.Selected = name;
            store.Save(file);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Select(string name)
    {
        lock (gate)
        {
            var file = store.Load();
            var match = file.Environments.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new KeyNotFoundException($"No Azure environment named '{name}'.");
            file.Selected = match.Name;
            store.Save(file);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(string name)
    {
        bool removed;
        lock (gate)
        {
            var file = store.Load();
            removed = file.Environments.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (string.Equals(file.Selected, name, StringComparison.OrdinalIgnoreCase)) file.Selected = file.Environments.FirstOrDefault()?.Name;
            if (removed) store.Save(file);
        }
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }
}
