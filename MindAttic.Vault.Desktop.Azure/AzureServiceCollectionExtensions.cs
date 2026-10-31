using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MindAttic.Vault.Desktop.Azure.Environments;
using MindAttic.Vault.Desktop.Azure.Identity;
using MindAttic.Vault.Desktop.Azure.Resources;
using MindAttic.Vault.Desktop.Azure.Setup;
using MindAttic.Vault.Desktop.Profiles;

namespace MindAttic.Vault.Desktop.Azure;

/// <summary>Registers the Azure Profile with Vault.Desktop's container.</summary>
public static class AzureServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="AzureProfile"/> (also as an <see cref="IProfile"/>), the sign-in session, the environment store,
    /// the resource catalog and <see cref="AzureSetupViewModel"/> (shown by <see cref="AzureSetupView"/>).
    /// Each piece is added only if not already registered, so tests and hosts can substitute their own first.
    /// </summary>
    public static IServiceCollection AddAzureProfile(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAzureSession>(_ => new InteractiveAzureSession());
        services.TryAddSingleton(_ => AzureEnvironmentStore.ForRoamingRoot());
        services.TryAddSingleton<IAzureClientFactory>(sp => new AzureClientFactory(sp.GetRequiredService<IAzureSession>()));
        services.TryAddSingleton<IAzureResourceCatalog, ArmResourceCatalog>();
        services.TryAddSingleton<AzureProfile>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProfile, AzureProfile>(sp => sp.GetRequiredService<AzureProfile>()));
        services.TryAddSingleton<AzureSetupViewModel>();
        return services;
    }
}
