using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Credentials;

namespace MindAttic.Vault.DependencyInjection;

/// <summary>
/// DI helpers for wiring MindAttic.Vault into any host (Blazor, Console, Worker).
///
/// <para><b>Recommended cloud-native usage in <c>Program.cs</c>:</b></para>
/// <code>
/// builder.Configuration
///     .AddJsonFile("appsettings.json", optional: true)
///     .AddMindAtticVaultFiles()           // %APPDATA%\MindAttic\... legacy keyrings (dev only)
///     .AddUserSecrets&lt;Program&gt;()       // dev secrets (use shared id "mindattic-vault-shared")
///     .AddEnvironmentVariables();         // App Service Application Settings + KV references
///
/// builder.Services.AddMindAtticVault(builder.Configuration);
/// </code>
///
/// <para>Stores resolve to a <see cref="CompositeCredentialStore"/> with the
/// configuration-backed read view in front of the writable file-backed store.
/// Reads see User Secrets / App Service / Key Vault; writes (e.g. from a
/// settings UI) land in the file store. Apps in production should not write
/// secrets at runtime; the writable fallback is for dev.</para>
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers stores backed only by <c>%APPDATA%\MindAttic\...</c> files.
    /// Suitable for console / desktop scenarios where the host has no
    /// <see cref="IConfiguration"/>.
    /// </summary>
    /// <param name="services">The service collection. Required.</param>
    /// <returns>The same <paramref name="services"/> for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is null.</exception>
    public static IServiceCollection AddMindAtticVault(this IServiceCollection services)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        // Default singletons share the bucket-default instance pointer (which
        // honours the env var override the legacy code already relies on).
        services.AddSingleton(LlmCredentialStore.Default);
        services.AddSingleton(BrokerCredentialStore.Default);
        // ICredentialStore default = LLM (most common ask). Apps that want the
        // broker store inject BrokerCredentialStore explicitly.
        services.AddSingleton<ICredentialStore>(sp => sp.GetRequiredService<LlmCredentialStore>());

        return services;
    }

    /// <summary>
    /// Cloud-native wiring. Registers Composite(Configuration → File) for both
    /// LLM and Broker buckets so the same code resolves credentials from User
    /// Secrets, App Service Application Settings, Azure Key Vault, or the
    /// legacy <c>%APPDATA%\MindAttic</c> file — whichever is present.
    /// </summary>
    /// <param name="services">The service collection. Required.</param>
    /// <param name="configuration">The configuration root. Required.</param>
    /// <returns>The same <paramref name="services"/> for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="configuration"/> is null.
    /// </exception>
    public static IServiceCollection AddMindAtticVault(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));

        var llmFile    = LlmCredentialStore.Default;
        var brokerFile = BrokerCredentialStore.Default;

        var llmResolver    = new LlmCredentialResolver(configuration, llmFile);
        var brokerResolver = new BrokerCredentialResolver(configuration, brokerFile);

        // Concrete file stores — exposed so legacy code that injects them keeps working.
        services.AddSingleton(llmFile);
        services.AddSingleton(brokerFile);

        // Cloud-native resolvers — what new code should depend on.
        services.AddSingleton(llmResolver);
        services.AddSingleton(brokerResolver);

        // Default ICredentialStore resolves to the LLM resolver (most common ask).
        services.AddSingleton<ICredentialStore>(_ => llmResolver);

        return services;
    }

    /// <summary>
    /// Registers a per-app <see cref="Settings.JsonSettingsStore{T}"/> rooted
    /// at <c>%APPDATA%\MindAttic\&lt;app&gt;\settings.json</c> (settings stay
    /// roaming per the cloud-native design — only secrets move into
    /// <see cref="IConfiguration"/>).
    /// </summary>
    /// <typeparam name="T">The settings POCO type. Must have a public parameterless constructor.</typeparam>
    /// <param name="services">The service collection. Required.</param>
    /// <param name="app">App folder name. Required.</param>
    /// <param name="fileName">
    /// Optional file name override. Defaults to
    /// <see cref="Settings.JsonSettingsStore{T}.DefaultFileName"/>.
    /// </param>
    /// <returns>The same <paramref name="services"/> for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="app"/> is null/whitespace.</exception>
    public static IServiceCollection AddVaultAppSettings<T>(
        this IServiceCollection services,
        string app,
        string fileName = Settings.JsonSettingsStore<T>.DefaultFileName)
        where T : class, new()
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (string.IsNullOrWhiteSpace(app))
            throw new ArgumentException("App name is required.", nameof(app));

        services.AddSingleton(_ => new Settings.JsonSettingsStore<T>(
            MindAttic.Vault.Paths.VaultPaths.RoamingBucket(app), fileName));
        return services;
    }
}
