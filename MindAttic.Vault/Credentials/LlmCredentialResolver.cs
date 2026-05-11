using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;

namespace MindAttic.Vault.Credentials;

/// <summary>
/// Cloud-native LLM credential resolver. Reads from <see cref="IConfiguration"/>
/// at <see cref="VaultConfigurationKeys.LlmSection"/> first (User Secrets,
/// App Service Application Settings, Azure Key Vault — whichever the host has
/// registered), then falls back to the file-backed <see cref="LlmCredentialStore"/>
/// at <c>%APPDATA%\MindAttic\LLM\</c>.
///
/// <para>This is the type apps should inject in 0.2.0+ (replaces direct
/// dependency on <see cref="LlmCredentialStore"/>). The DI registration in
/// <c>AddMindAtticVault(IConfiguration)</c> wires it automatically.</para>
///
/// <para>Writes (e.g. from a settings UI) skip the read-only configuration
/// store and land in the file store — see <see cref="CompositeCredentialStore"/>
/// for the full chaining contract.</para>
/// </summary>
public sealed class LlmCredentialResolver : CompositeCredentialStore
{
    /// <summary>
    /// Constructs a resolver chaining <see cref="ConfigurationCredentialStore.ForLlm"/>
    /// over <paramref name="fileStore"/>.
    /// </summary>
    /// <param name="configuration">The configuration root. Required.</param>
    /// <param name="fileStore">The file-backed fallback store. Required.</param>
    public LlmCredentialResolver(IConfiguration configuration, LlmCredentialStore fileStore)
        : base(ConfigurationCredentialStore.ForLlm(configuration), fileStore) { }
}
