using Microsoft.Extensions.DependencyInjection;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Azure;
using MindAttic.Vault.Desktop.Azure.Setup;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Desktop.ViewModels;

namespace MindAttic.Vault.Desktop;

/// <summary>
/// The app's composition root (Microsoft.Extensions.DependencyInjection, no Generic Host). Every view model takes its
/// services through its constructor, so tests build the same graph with fakes.
/// </summary>
public static class DesktopServices
{
    /// <summary>Registers everything the window needs.</summary>
    public static IServiceCollection AddVaultDesktop(this IServiceCollection services)
    {
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<IDialogs, WpfDialogs>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => KeyHealthStore.ForLocalRoot());
        services.AddSingleton<IAuditLog>(_ => new SqliteAuditLog());
        services.AddSingleton<DesktopContext>();
        services.AddSingleton<IManifestCatalog>(_ => new FileManifestCatalog());
        // Key tests: outbound HTTPS to the provider only.
        services.AddSingleton<IKeyTester>(_ => new ProviderKeyTester());
        services.AddSingleton<KeyOperations>();
        services.AddSingleton(GetStartedSources.Default);

        // Profiles: every IProfile registered here appears in the picker; Desktop is the default.
        services.AddSingleton<IProfile>(_ => new DesktopProfile());
        AddExtraProfiles(services);

        services.AddSingleton<MainViewModel>();
        return services;
    }

    /// <summary>
    /// More Profiles: each one is an <see cref="IProfile"/> registration (the picker lists them all), plus an optional
    /// <see cref="ProfileSetup"/> whose view model gets a setup tab while that Profile is selected. Register a new
    /// platform here.
    /// </summary>
    private static void AddExtraProfiles(IServiceCollection services)
    {
        services.AddAzureProfile();
        services.AddSingleton(sp => new ProfileSetup(AzureProfile.ProfileId, "Azure setup", sp.GetRequiredService<AzureSetupViewModel>()));
    }

    /// <summary>The app's service provider.</summary>
    public static ServiceProvider Build() =>
        new ServiceCollection().AddVaultDesktop().BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
}
