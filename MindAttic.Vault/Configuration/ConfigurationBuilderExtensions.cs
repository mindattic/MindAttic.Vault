using Microsoft.Extensions.Configuration;

namespace MindAttic.Vault.Configuration;

/// <summary>
/// Hook MindAttic's existing on-disk credential files into the IConfiguration
/// pipeline so legacy keys at <c>%APPDATA%\MindAttic\&lt;bucket&gt;\providers.json</c>
/// surface alongside User Secrets, env vars, and Azure Key Vault.
/// </summary>
public static class ConfigurationBuilderExtensions
{
    /// <summary>
    /// Adds the MindAttic file-backed configuration source. Recommended order
    /// in <c>Program.cs</c> (lowest precedence first; later sources win):
    /// <code>
    /// builder.Configuration
    ///     .AddJsonFile("appsettings.json", optional: true)
    ///     .AddMindAtticVaultFiles()           // %APPDATA%\MindAttic\... legacy keyrings
    ///     .AddUserSecrets&lt;Program&gt;()       // dev secrets
    ///     .AddEnvironmentVariables()          // App Service / containers
    ///     .AddAzureKeyVault(...);             // optional, Azure-only
    /// </code>
    /// </summary>
    public static IConfigurationBuilder AddMindAtticVaultFiles(
        this IConfigurationBuilder builder,
        Action<MindAtticConfigurationSource>? configure = null)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));

        var source = new MindAtticConfigurationSource();
        configure?.Invoke(source);
        builder.Add(source);
        return builder;
    }
}
