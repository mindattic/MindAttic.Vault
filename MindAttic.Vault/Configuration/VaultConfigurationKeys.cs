namespace MindAttic.Vault.Configuration;

/// <summary>
/// Single source of truth for the section names every cloud-native source
/// (appsettings.json, User Secrets, App Service Application Settings,
/// Azure Key Vault) must use to surface MindAttic credentials.
///
/// <para>Schema:</para>
/// <code>
/// MindAttic:
///   Vault:
///     LLM:
///       claude:  { type, apiKey, model, maxTokens }
///       gemini:  { type, apiKey }
///     Brokers:
///       alpaca-paper: { type, apiKey, secret, baseUrl }
///       alpaca-live:  { type, apiKey, secret, baseUrl }
///     Tokens:
///       github: "ghp_..."
/// </code>
///
/// <para>Examples by source:</para>
/// <list type="bullet">
///   <item><description>appsettings.json — nested objects under "MindAttic":{ "Vault":{ ... } }.</description></item>
///   <item><description>User Secrets — <c>dotnet user-secrets set "MindAttic:Vault:LLM:claude:apiKey" "sk-ant-..."</c>.</description></item>
///   <item><description>Env vars (incl. App Service) — <c>MindAttic__Vault__LLM__claude__apiKey=sk-ant-...</c>.</description></item>
///   <item><description>Azure Key Vault — secret named <c>MindAttic--Vault--LLM--claude--apiKey</c> (default <c>--</c> → <c>:</c> translation).</description></item>
/// </list>
/// </summary>
public static class VaultConfigurationKeys
{
    /// <summary>Top-level section: <c>MindAttic</c>.</summary>
    public const string RootSection = "MindAttic";

    /// <summary>Vault section path: <c>MindAttic:Vault</c>.</summary>
    public const string VaultSection = RootSection + ":" + "Vault";

    /// <summary>LLM credential bucket: <c>MindAttic:Vault:LLM</c>.</summary>
    public const string LlmSection = VaultSection + ":" + "LLM";

    /// <summary>Broker credential bucket: <c>MindAttic:Vault:Brokers</c>.</summary>
    public const string BrokersSection = VaultSection + ":" + "Brokers";

    /// <summary>Single-token bucket: <c>MindAttic:Vault:Tokens</c>.</summary>
    public const string TokensSection = VaultSection + ":" + "Tokens";

    /// <summary>The shared User Secrets ID for every MindAttic app that wants family-wide dev secrets.</summary>
    public const string SharedUserSecretsId = "mindattic-vault-shared";

    /// <summary>The standard property name for an API key inside a per-provider object.</summary>
    public const string ApiKeyProperty = "apiKey";

    /// <summary>Returns the section path for a specific provider id (e.g. <c>MindAttic:Vault:LLM:claude</c>).</summary>
    public static string ProviderSection(string bucketSection, string providerId)
    {
        if (string.IsNullOrWhiteSpace(bucketSection))
            throw new ArgumentException("Bucket section is required.", nameof(bucketSection));
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("Provider id is required.", nameof(providerId));
        return $"{bucketSection}:{providerId}";
    }

    /// <summary>Returns the apiKey path inside a provider section.</summary>
    public static string ProviderApiKeyPath(string bucketSection, string providerId) =>
        $"{ProviderSection(bucketSection, providerId)}:{ApiKeyProperty}";
}
