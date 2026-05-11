using System.Text.Json;
using MindAttic.Vault.Paths;

namespace MindAttic.Vault.Credentials;

/// <summary>
/// Broker keyring at <c>%APPDATA%\MindAttic\Brokers\</c>. Per-provider entry shape:
/// <code>
/// {
///   "alpaca-paper": { "type": "alpaca", "apiKey": "PK...", "secret": "...", "baseUrl": "https://paper-api.alpaca.markets" },
///   "alpaca-live":  { "type": "alpaca", "apiKey": "AK...", "secret": "...", "baseUrl": "https://api.alpaca.markets" }
/// }
/// </code>
///
/// <para>Drop-in replacement for IdiotProof's <c>BrokerCredentialStore</c>. The
/// <c>MINDATTIC_BROKER_CREDENTIALS</c> env var still overrides the directory.</para>
///
/// <para>For cloud-native deployments prefer <see cref="BrokerCredentialResolver"/>,
/// which reads from <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
/// first and falls back to this file store.</para>
/// </summary>
public sealed class BrokerCredentialStore : CredentialStore
{
    /// <summary>Bucket folder name under <c>%APPDATA%\MindAttic\</c>.</summary>
    public const string Bucket          = "Brokers";

    /// <summary>Environment variable that overrides the resolved bucket directory.</summary>
    public const string DirectoryEnvVar = "MINDATTIC_BROKER_CREDENTIALS";

    /// <summary>
    /// Default instance pointed at <c>%APPDATA%\MindAttic\Brokers\</c>
    /// (or the value of <c>MINDATTIC_BROKER_CREDENTIALS</c> if set).
    /// </summary>
    /// <remarks>
    /// Captured once at type-load time. Construct a fresh
    /// <see cref="BrokerCredentialStore"/> if you need a runtime override.
    /// </remarks>
    public static BrokerCredentialStore Default { get; } = new(ResolveDefaultDirectory());

    /// <summary>Construct a broker credential store rooted at <paramref name="directory"/>.</summary>
    /// <inheritdoc />
    public BrokerCredentialStore(string directory) : base(directory) { }

    private static string ResolveDefaultDirectory() =>
        Environment.GetEnvironmentVariable(DirectoryEnvVar)
        ?? VaultPaths.RoamingBucket(Bucket);

    /// <summary>Strongly-typed broker credentials.</summary>
    /// <param name="ApiKey">The broker API key. Required (non-empty).</param>
    /// <param name="Secret">The broker API secret. Required (non-empty).</param>
    /// <param name="BaseUrl">
    /// Optional broker base URL (e.g. <c>https://paper-api.alpaca.markets</c>).
    /// Null when not relevant.
    /// </param>
    public sealed record BrokerCreds(string ApiKey, string Secret, string? BaseUrl);

    /// <summary>
    /// Loads a broker provider's full credential payload (<c>apiKey</c> +
    /// <c>secret</c> + <c>baseUrl</c>).
    /// </summary>
    /// <param name="providerId">
    /// Provider id (e.g. <c>"alpaca-paper"</c>). Case-insensitive.
    /// Empty/whitespace returns <c>null</c>.
    /// </param>
    /// <returns>
    /// The credential record when found, or <c>null</c> when the provider is
    /// missing, malformed, or has an empty <c>apiKey</c> or <c>secret</c>.
    /// </returns>
    /// <remarks>
    /// Mirrors <c>IdiotProof.Engine.Settings.BrokerCredentialStore.Get()</c> so
    /// existing call sites can move to MindAttic.Vault unchanged.
    /// </remarks>
    public BrokerCreds? GetBrokerCreds(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;

        var raw = LoadAllRaw();
        if (!raw.TryGetValue(providerId, out var json)) return null;
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            // Read each field defensively — a wrong type (e.g. number where a
            // string was expected) is treated as missing rather than a crash.
            var apiKey = doc.RootElement.TryGetProperty("apiKey", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString() ?? "" : "";
            var secret = doc.RootElement.TryGetProperty("secret", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? "" : "";
            var baseUrl = doc.RootElement.TryGetProperty("baseUrl", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString() : null;

            // A broker entry without apiKey AND secret is unusable; report null
            // so callers don't construct a half-valid credentials record.
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(secret))
                return null;

            return new BrokerCreds(apiKey.Trim(), secret.Trim(), baseUrl?.Trim());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Upserts a broker provider's full credential payload, preserving the
    /// existing <c>type</c> field when one is already on disk (defaults to
    /// <paramref name="brokerType"/> when not).
    /// </summary>
    /// <param name="providerId">Provider id. Required.</param>
    /// <param name="creds">The credential record to write. Required.</param>
    /// <param name="brokerType">
    /// The broker family identifier to use when no <c>type</c> is already on
    /// disk. Defaults to <c>"alpaca"</c>.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="providerId"/> is null/whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="creds"/> is null.</exception>
    public void SetBrokerCreds(string providerId, BrokerCreds creds, string brokerType = "alpaca")
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("Provider ID is required.", nameof(providerId));
        if (creds is null) throw new ArgumentNullException(nameof(creds));

        // Read the existing entry so we can preserve a custom 'type' (e.g. a
        // broker-specific value the caller set previously).
        var raw = LoadAllRaw();
        string? existingType = brokerType;
        if (raw.TryGetValue(providerId, out var existingJson) && !string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(existingJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("type", out var t)
                    && t.ValueKind == JsonValueKind.String)
                {
                    existingType = t.GetString() ?? brokerType;
                }
            }
            catch { /* swallow — fall back to brokerType. */ }
        }

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("type", existingType);
            w.WriteString("apiKey", creds.ApiKey ?? "");
            w.WriteString("secret", creds.Secret ?? "");
            // baseUrl is optional in the broker schema — only write it when present.
            if (!string.IsNullOrWhiteSpace(creds.BaseUrl))
                w.WriteString("baseUrl", creds.BaseUrl);
            w.WriteEndObject();
        }

        SaveRaw(providerId, System.Text.Encoding.UTF8.GetString(ms.ToArray()));
    }

    /// <summary>
    /// Preserves <c>type</c>, <c>secret</c>, and <c>baseUrl</c> when only the
    /// <c>apiKey</c> is being rotated through the generic
    /// <see cref="CredentialStore.SetKey"/> path.
    /// </summary>
    /// <inheritdoc />
    protected override string MergeApiKeyIntoProviderJson(string? existingJson, string providerId, string apiKey)
    {
        string? type    = null;
        string? secret  = null;
        string? baseUrl = null;

        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(existingJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("type",    out var t) && t.ValueKind == JsonValueKind.String) type    = t.GetString();
                    if (doc.RootElement.TryGetProperty("secret",  out var s) && s.ValueKind == JsonValueKind.String) secret  = s.GetString();
                    if (doc.RootElement.TryGetProperty("baseUrl", out var b) && b.ValueKind == JsonValueKind.String) baseUrl = b.GetString();
                }
            }
            catch { /* swallow — fall back to inferred defaults. */ }
        }

        // Infer broker family from the provider id when no 'type' is already on disk.
        // 'alpaca' covers paper / live / variants; everything else is a generic bearer.
        type ??= providerId.StartsWith("alpaca", StringComparison.OrdinalIgnoreCase) ? "alpaca" : "bearer";

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("type", type);
            w.WriteString("apiKey", apiKey);
            // Only emit secret/baseUrl when they were already on the entry —
            // we don't fabricate placeholders for fields the caller never set.
            if (secret  is not null) w.WriteString("secret",  secret);
            if (baseUrl is not null) w.WriteString("baseUrl", baseUrl);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
