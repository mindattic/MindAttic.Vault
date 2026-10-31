using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.AppConfiguration;
using Azure.ResourceManager;
using Azure.Security.KeyVault.Secrets;
using MindAttic.Vault.Desktop.Azure.Identity;

namespace MindAttic.Vault.Desktop.Azure;

/// <summary>Creates the Azure SDK clients. Tests pass a fake transport and credential; the app uses the defaults.</summary>
public interface IAzureClientFactory
{
    ArmClient CreateArmClient();
    ConfigurationClient CreateConfigurationClient(Uri endpoint);
    SecretClient CreateSecretClient(Uri vaultUri);
}

/// <summary>Clients authenticated as the signed-in user (<see cref="IAzureSession.Credential"/>).</summary>
public sealed class AzureClientFactory : IAzureClientFactory
{
    private readonly Func<TokenCredential> credential;
    private readonly HttpPipelineTransport? transport;
    private readonly int? maxRetries;

    public AzureClientFactory(IAzureSession session) : this(() => session.Credential) { }

    /// <param name="credential">The credential (read per client so a new sign-in takes effect).</param>
    /// <param name="transport">A transport override (tests: <c>new HttpClientTransport(handler)</c>).</param>
    /// <param name="maxRetries">Retry override (tests: 0 so scripted failures surface immediately).</param>
    public AzureClientFactory(Func<TokenCredential> credential, HttpPipelineTransport? transport = null, int? maxRetries = null)
    {
        this.credential = credential ?? throw new ArgumentNullException(nameof(credential));
        this.transport = transport;
        this.maxRetries = maxRetries;
    }

    public ArmClient CreateArmClient() => new(credential(), defaultSubscriptionId: null, Configure(new ArmClientOptions()));

    public ConfigurationClient CreateConfigurationClient(Uri endpoint) =>
        new(endpoint, credential(), Configure(new ConfigurationClientOptions()));

    public SecretClient CreateSecretClient(Uri vaultUri) =>
        new(vaultUri, credential(), Configure(new SecretClientOptions()));

    private T Configure<T>(T options) where T : ClientOptions
    {
        if (transport is not null) options.Transport = transport;
        if (maxRetries is { } retries) options.Retry.MaxRetries = retries;
        // Request/response bodies (which can hold key values) are never logged.
        options.Diagnostics.IsLoggingContentEnabled = false;
        return options;
    }
}
