namespace MindAttic.Vault.Configuration;

/// <summary>
/// Single source of truth for the section names every cloud-native source
/// (appsettings.json, User Secrets, App Service Application Settings,
/// Azure Key Vault) must use to surface MindAttic credentials.
///
/// <para><b>Schema:</b></para>
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
///       github:   "ghp_..."
///       nuget-org:"oy2..."
///     Subtitles:
///       OpenSubtitles: { user, password }
///     Notifications:
///       twilio: { accountSid, authToken, from }
///       email:  { smtpHost, smtpPort, username, password, from }
///       to:     "+1..."
///     AudioStore: { provider, container, connectionString }
/// </code>
///
/// <para><b>Canonical on-disk home (single local source of truth):</b> each section's
/// final segment is also its folder under <c>%APPDATA%\MindAttic\</c> — e.g.
/// <c>MindAttic:Vault:LLM</c> ↔ <c>%APPDATA%\MindAttic\LLM\providers.json</c>. Surfaced
/// through <see cref="MindAtticConfigurationSource"/>.</para>
///
/// <para><b>Examples by source:</b></para>
/// <list type="bullet">
///   <item><description>appsettings.json — nested objects under <c>"MindAttic":{ "Vault":{ ... } }</c>.</description></item>
///   <item><description>Local dev — the APPDATA bucket file above (e.g. <c>LLM\providers.json</c>).</description></item>
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

    /// <summary>Subtitle-provider credential bucket: <c>MindAttic:Vault:Subtitles</c>.</summary>
    public const string SubtitlesSection = VaultSection + ":" + "Subtitles";

    /// <summary>Notification (SMS/email) credential bucket: <c>MindAttic:Vault:Notifications</c>.</summary>
    public const string NotificationsSection = VaultSection + ":" + "Notifications";

    /// <summary>Audio blob-store credential bucket: <c>MindAttic:Vault:AudioStore</c>.</summary>
    public const string AudioStoreSection = VaultSection + ":" + "AudioStore";

    /// <summary>The standard property name for an API key inside a per-provider object.</summary>
    public const string ApiKeyProperty = "apiKey";

    /// <summary>Returns the section path for a specific provider id (e.g. <c>MindAttic:Vault:LLM:claude</c>).</summary>
    /// <param name="bucketSection">
    /// Bucket section path (e.g. <see cref="LlmSection"/>). Required.
    /// </param>
    /// <param name="providerId">Provider id. Required.</param>
    /// <returns>The colon-delimited path to the provider's section.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when either argument is null or whitespace.
    /// </exception>
    public static string ProviderSection(string bucketSection, string providerId)
    {
        if (string.IsNullOrWhiteSpace(bucketSection))
            throw new ArgumentException("Bucket section is required.", nameof(bucketSection));
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("Provider id is required.", nameof(providerId));
        return $"{bucketSection}:{providerId}";
    }

    /// <summary>Returns the apiKey path inside a provider section.</summary>
    /// <param name="bucketSection">Bucket section path (e.g. <see cref="LlmSection"/>). Required.</param>
    /// <param name="providerId">Provider id. Required.</param>
    /// <returns>
    /// The colon-delimited path to the provider's <c>apiKey</c> leaf
    /// (e.g. <c>MindAttic:Vault:LLM:claude:apiKey</c>).
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when either argument is null or whitespace.
    /// </exception>
    public static string ProviderApiKeyPath(string bucketSection, string providerId) =>
        $"{ProviderSection(bucketSection, providerId)}:{ApiKeyProperty}";
}
