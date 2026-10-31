using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Azure.Environments;
using MindAttic.Vault.Desktop.Azure.Identity;
using MindAttic.Vault.Desktop.Azure.Keys;
using MindAttic.Vault.Desktop.Azure.Settings;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Vault.Desktop.Azure;

/// <summary>
/// The <c>Azure</c> Profile: the same tabs as Desktop, loaded from and saved to the selected environment's App
/// Configuration store (settings, key names, order, assignments) and Key Vault (key values), as the signed-in user.
/// <see cref="Settings"/> and <see cref="Keys"/> follow the currently selected environment.
/// </summary>
public sealed class AzureProfile : IProfile
{
    public const string ProfileId = "Azure";
    public const string SetupMessage = "Sign in and pick a Key Vault and App Configuration store";

    private readonly IAzureSession session;
    private readonly AzureEnvironmentStore environments;
    private readonly IAzureClientFactory clients;
    private readonly object gate = new();
    private (string Key, AppConfigurationSettingsBackend Settings, KeyVaultKeyBackend Keys)? cached;

    public AzureProfile(IAzureSession session, AzureEnvironmentStore environments, IAzureClientFactory clients)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.environments = environments ?? throw new ArgumentNullException(nameof(environments));
        this.clients = clients ?? throw new ArgumentNullException(nameof(clients));
        Settings = new SettingsFacade(this);
        Keys = new KeysFacade(this);
        session.Changed += (_, _) => Invalidate();
        environments.Changed += (_, _) => Invalidate();
    }

    public string Id => ProfileId;

    public string DisplayName => environments.Current() is { } env ? $"Azure ({env.Name})" : "Azure";

    public string Actor => session.UserPrincipalName ?? Environment.UserName;

    public ISettingsBackend Settings { get; }

    public IKeyBackend Keys { get; }

    /// <summary>The environment Load/Save use, or null.</summary>
    public AzureEnvironment? CurrentEnvironment => environments.Current();

    public Task<ProfileReadiness> GetReadinessAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(session.IsSignedIn && environments.Current() is { IsComplete: true }
            ? ProfileReadiness.Ready
            : ProfileReadiness.NeedsSetup(SetupMessage));

    private (AppConfigurationSettingsBackend Settings, KeyVaultKeyBackend Keys) Backends()
    {
        var env = environments.Current();
        if (!session.IsSignedIn || env is not { IsComplete: true }) throw new InvalidOperationException(SetupMessage + ".");
        var key = $"{env.Name}|{env.AppConfigurationEndpoint}|{env.KeyVaultUri}";
        lock (gate)
        {
            if (cached is { } c && c.Key == key) return (c.Settings, c.Keys);
            var config = clients.CreateConfigurationClient(new Uri(env.AppConfigurationEndpoint));
            var secrets = clients.CreateSecretClient(new Uri(env.KeyVaultUri));
            var storeName = string.IsNullOrWhiteSpace(env.AppConfigurationName) ? new Uri(env.AppConfigurationEndpoint).Host : env.AppConfigurationName;
            var vaultName = string.IsNullOrWhiteSpace(env.KeyVaultName) ? new Uri(env.KeyVaultUri).Host : env.KeyVaultName;
            var settings = new AppConfigurationSettingsBackend(config, storeName);
            var keys = new KeyVaultKeyBackend(config, secrets, storeName, vaultName, clients.CreateSecretClient);
            cached = (key, settings, keys);
            return (settings, keys);
        }
    }

    private void Invalidate()
    {
        lock (gate) cached = null;
    }

    private sealed class SettingsFacade(AzureProfile profile) : ISettingsBackend
    {
        public string WhenAppsSeeChanges => "after the app's App Configuration refresh interval, or its next restart";

        public Task<SettingsDocument> LoadAsync(SettingsManifest manifest, CancellationToken cancellationToken = default) =>
            profile.Backends().Settings.LoadAsync(manifest, cancellationToken);

        public Task<string?> SaveAsync(SettingsManifest manifest, SettingsDocument document, IReadOnlyCollection<string> changedFields,
            CancellationToken cancellationToken = default) =>
            profile.Backends().Settings.SaveAsync(manifest, document, changedFields, cancellationToken);
    }

    private sealed class KeysFacade(AzureProfile profile) : IKeyBackend
    {
        private IKeyBackend Inner => profile.Backends().Keys;

        public Task<IReadOnlyList<string>> ProvidersWithKeysAsync(CancellationToken cancellationToken = default) => Inner.ProvidersWithKeysAsync(cancellationToken);
        public Task<IReadOnlyList<NamedKey>> GetKeysAsync(string providerId, CancellationToken cancellationToken = default) => Inner.GetKeysAsync(providerId, cancellationToken);
        public Task AddAsync(string providerId, string name, string value, CancellationToken cancellationToken = default) => Inner.AddAsync(providerId, name, value, cancellationToken);
        public Task SetValueAsync(string providerId, string name, string value, CancellationToken cancellationToken = default) => Inner.SetValueAsync(providerId, name, value, cancellationToken);
        public Task RenameAsync(string providerId, string oldName, string newName, CancellationToken cancellationToken = default) => Inner.RenameAsync(providerId, oldName, newName, cancellationToken);
        public Task RemoveAsync(string providerId, string name, CancellationToken cancellationToken = default) => Inner.RemoveAsync(providerId, name, cancellationToken);
        public Task SetDisabledAsync(string providerId, string name, bool disabled, CancellationToken cancellationToken = default) => Inner.SetDisabledAsync(providerId, name, disabled, cancellationToken);
        public Task ReorderAsync(string providerId, IReadOnlyList<string> names, CancellationToken cancellationToken = default) => Inner.ReorderAsync(providerId, names, cancellationToken);
        public Task<IReadOnlyList<string>> GetAssignmentAsync(string appId, string providerId, CancellationToken cancellationToken = default) => Inner.GetAssignmentAsync(appId, providerId, cancellationToken);
        public Task SetAssignmentAsync(string appId, string providerId, IReadOnlyList<string> names, CancellationToken cancellationToken = default) => Inner.SetAssignmentAsync(appId, providerId, names, cancellationToken);
        public Task<IReadOnlyList<string>> AppsUsingAsync(string providerId, string name, CancellationToken cancellationToken = default) => Inner.AppsUsingAsync(providerId, name, cancellationToken);
    }
}
