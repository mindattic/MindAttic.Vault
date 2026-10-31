using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.AppConfiguration;
using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.Resources;
using MindAttic.Vault.Desktop.Profiles;

namespace MindAttic.Vault.Desktop.Azure.Resources;

public sealed record AzureSubscriptionInfo(string SubscriptionId, string DisplayName, string? TenantId)
{
    public override string ToString() => DisplayName;
}

public sealed record AzureResourceGroupInfo(string SubscriptionId, string Name, string? Location)
{
    public override string ToString() => Name;
}

/// <summary>A Key Vault or App Configuration store: its ARM id, name and data-plane endpoint.</summary>
public sealed record AzureResourceInfo(string ResourceId, string Name, string Endpoint, string? Location)
{
    public override string ToString() => Name;
}

/// <summary>Lists what the signed-in user can pick: subscriptions → resource groups → Key Vaults / App Configuration stores.</summary>
public interface IAzureResourceCatalog
{
    Task<IReadOnlyList<AzureSubscriptionInfo>> ListSubscriptionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AzureResourceGroupInfo>> ListResourceGroupsAsync(string subscriptionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AzureResourceInfo>> ListKeyVaultsAsync(string subscriptionId, string resourceGroup, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AzureResourceInfo>> ListAppConfigurationStoresAsync(string subscriptionId, string resourceGroup, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IAzureResourceCatalog"/> over Azure.ResourceManager (every page followed).</summary>
public sealed class ArmResourceCatalog : IAzureResourceCatalog
{
    private readonly IAzureClientFactory clients;

    public ArmResourceCatalog(IAzureClientFactory clients) => this.clients = clients ?? throw new ArgumentNullException(nameof(clients));

    public Task<IReadOnlyList<AzureSubscriptionInfo>> ListSubscriptionsAsync(CancellationToken cancellationToken = default) =>
        Guard("list subscriptions", async () =>
        {
            var result = new List<AzureSubscriptionInfo>();
            await foreach (var sub in clients.CreateArmClient().GetSubscriptions().GetAllAsync(cancellationToken).ConfigureAwait(false))
                result.Add(new AzureSubscriptionInfo(sub.Data.SubscriptionId, sub.Data.DisplayName ?? sub.Data.SubscriptionId, sub.Data.TenantId?.ToString()));
            return Sorted(result, s => s.DisplayName);
        });

    public Task<IReadOnlyList<AzureResourceGroupInfo>> ListResourceGroupsAsync(string subscriptionId, CancellationToken cancellationToken = default) =>
        Guard($"list resource groups in subscription {subscriptionId}", async () =>
        {
            var sub = clients.CreateArmClient().GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(Required(subscriptionId)));
            var result = new List<AzureResourceGroupInfo>();
            await foreach (var rg in sub.GetResourceGroups().GetAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                result.Add(new AzureResourceGroupInfo(subscriptionId, rg.Data.Name, rg.Data.Location.ToString()));
            return Sorted(result, r => r.Name);
        });

    public Task<IReadOnlyList<AzureResourceInfo>> ListKeyVaultsAsync(string subscriptionId, string resourceGroup, CancellationToken cancellationToken = default) =>
        Guard($"list Key Vaults in {resourceGroup}", async () =>
        {
            var result = new List<AzureResourceInfo>();
            await foreach (var vault in Group(subscriptionId, resourceGroup).GetKeyVaults().GetAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                result.Add(new AzureResourceInfo(vault.Id.ToString(), vault.Data.Name,
                    vault.Data.Properties?.VaultUri?.ToString() ?? $"https://{vault.Data.Name}.vault.azure.net/", vault.Data.Location.ToString()));
            return Sorted(result, r => r.Name);
        });

    public Task<IReadOnlyList<AzureResourceInfo>> ListAppConfigurationStoresAsync(string subscriptionId, string resourceGroup, CancellationToken cancellationToken = default) =>
        Guard($"list App Configuration stores in {resourceGroup}", async () =>
        {
            var result = new List<AzureResourceInfo>();
            await foreach (var store in Group(subscriptionId, resourceGroup).GetAppConfigurationStores().GetAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                result.Add(new AzureResourceInfo(store.Id.ToString(), store.Data.Name,
                    store.Data.Endpoint ?? $"https://{store.Data.Name}.azconfig.io", store.Data.Location.ToString()));
            return Sorted(result, r => r.Name);
        });

    private ResourceGroupResource Group(string subscriptionId, string resourceGroup) =>
        clients.CreateArmClient().GetResourceGroupResource(
            ResourceGroupResource.CreateResourceIdentifier(Required(subscriptionId), Required(resourceGroup)));

    private static string Required(string value) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.") : value.Trim();

    private static IReadOnlyList<T> Sorted<T>(List<T> items, Func<T, string> key) =>
        items.OrderBy(key, StringComparer.OrdinalIgnoreCase).ToList();

    private static async Task<IReadOnlyList<T>> Guard<T>(string what, Func<Task<IReadOnlyList<T>>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            throw new ProfileAccessDeniedException(
                $"Azure refused to {what}. You need at least the 'Reader' role there (and to be signed in as the right account).", ex);
        }
    }
}
