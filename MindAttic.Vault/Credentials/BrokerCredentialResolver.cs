using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;

namespace MindAttic.Vault.Credentials;

/// <summary>
/// Cloud-native broker credential resolver. Reads from <see cref="IConfiguration"/>
/// at <see cref="VaultConfigurationKeys.BrokersSection"/> first, then falls back
/// to the file-backed <see cref="BrokerCredentialStore"/> at
/// <c>%APPDATA%\MindAttic\Brokers\</c>.
///
/// <para>This is the type apps should inject in 0.2.0+ when they need broker
/// keys. The DI registration in <c>AddMindAtticVault(IConfiguration)</c> wires
/// it automatically.</para>
///
/// <para>For full <see cref="BrokerCredentialStore.BrokerCreds"/> payloads
/// (apiKey + secret + baseUrl), the resolver still exposes
/// <see cref="CompositeCredentialStore.GetKey"/> for the apiKey only — call
/// <see cref="BrokerCredentialStore.GetBrokerCreds"/> on the underlying file
/// store for the rich record. Production deployments using
/// <see cref="IConfiguration"/> should bind the full provider object via
/// <see cref="CompositeCredentialStore.LoadAllRaw"/> and parse it themselves.</para>
/// </summary>
public sealed class BrokerCredentialResolver : CompositeCredentialStore
{
    /// <summary>
    /// Constructs a resolver chaining <see cref="ConfigurationCredentialStore.ForBrokers"/>
    /// over <paramref name="fileStore"/>.
    /// </summary>
    /// <param name="configuration">The configuration root. Required.</param>
    /// <param name="fileStore">The file-backed fallback store. Required.</param>
    public BrokerCredentialResolver(IConfiguration configuration, BrokerCredentialStore fileStore)
        : base(ConfigurationCredentialStore.ForBrokers(configuration), fileStore) { }
}
