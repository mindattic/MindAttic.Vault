using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton<MainViewModel>();
        return services;
    }

    /// <summary>The app's service provider.</summary>
    public static ServiceProvider Build() =>
        new ServiceCollection().AddVaultDesktop().BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
}
