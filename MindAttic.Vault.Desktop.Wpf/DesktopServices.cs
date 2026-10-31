using Microsoft.Extensions.DependencyInjection;
using MindAttic.Vault.Credentials;
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
        services.AddSingleton<DesktopContext>();
        services.AddSingleton<IManifestCatalog>(_ => new FileManifestCatalog());

        // Profiles: every IProfile registered here appears in the picker; Desktop is the default.
        services.AddSingleton<IProfile>(_ => new DesktopProfile());
        AddExtraProfiles(services);

        services.AddSingleton<MainViewModel>();
        return services;
    }

    /// <summary>
    /// More Profiles. Each one is an <see cref="IProfile"/> registration; the picker lists them all.
    /// </summary>
    private static void AddExtraProfiles(IServiceCollection services)
    {
        // LAMP: add services.AddAzureProfile(); here once MindAttic.Vault.Desktop.Azure builds (and reference that project).
    }

    /// <summary>The app's service provider.</summary>
    public static ServiceProvider Build() =>
        new ServiceCollection().AddVaultDesktop().BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
}
