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
/// Drop-in replacement for IdiotProof's <c>BrokerCredentialStore</c>. The
/// <c>MINDATTIC_BROKER_CREDENTIALS</c> env var still overrides the directory.
/// </summary>
public sealed class BrokerCredentialStore : CredentialStore
{
    public const string Bucket          = "Brokers";
    public const string DirectoryEnvVar = "MINDATTIC_BROKER_CREDENTIALS";

    /// <summary>
    /// Default instance pointed at <c>%APPDATA%\MindAttic\Brokers\</c>
    /// (or the value of <c>MINDATTIC_BROKER_CREDENTIALS</c> if set).
    /// </summary>
    public static BrokerCredentialStore Default { get; } = new(ResolveDefaultDirectory());

    public BrokerCredentialStore(string directory) : base(directory) { }

    private static string ResolveDefaultDirectory() =>
        Environment.GetEnvironmentVariable(DirectoryEnvVar)
        ?? VaultPaths.RoamingBucket(Bucket);

    /// <summary>Strongly-typed broker credentials.</summary>
    public sealed record BrokerCreds(string ApiKey, string Secret, string? BaseUrl);

    /// <summary>
    /// Loads a broker provider's full credential payload (apiKey + secret + baseUrl).
    /// Returns <c>null</c> if the provider is missing, malformed, or has an empty
    /// apiKey or secret. Mirrors IdiotProof.Engine.Settings.BrokerCredentialStore.Get().
    /// </summary>
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

            var apiKey = doc.RootElement.TryGetProperty("apiKey", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString() ?? "" : "";
            var secret = doc.RootElement.TryGetProperty("secret", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? "" : "";
            var baseUrl = doc.RootElement.TryGetProperty("baseUrl", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString() : null;

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
    /// <c>type</c> field (defaults to <c>"alpaca"</c> when not already set).
    /// </summary>
    public void SetBrokerCreds(string providerId, BrokerCreds creds, string brokerType = "alpaca")
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("Provider ID is required.", nameof(providerId));
        if (creds is null) throw new ArgumentNullException(nameof(creds));

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
            catch { }
        }

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("type", existingType);
            w.WriteString("apiKey", creds.ApiKey ?? "");
            w.WriteString("secret", creds.Secret ?? "");
            if (!string.IsNullOrWhiteSpace(creds.BaseUrl))
                w.WriteString("baseUrl", creds.BaseUrl);
            w.WriteEndObject();
        }

        SaveRaw(providerId, System.Text.Encoding.UTF8.GetString(ms.ToArray()));
    }

    /// <summary>
    /// Preserves <c>type</c>, <c>secret</c>, and <c>baseUrl</c> when only the apiKey
    /// is being rotated through the generic <see cref="CredentialStore.SetKey"/> path.
    /// </summary>
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
            catch { }
        }

        type ??= providerId.StartsWith("alpaca", StringComparison.OrdinalIgnoreCase) ? "alpaca" : "bearer";

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("type", type);
            w.WriteString("apiKey", apiKey);
            if (secret  is not null) w.WriteString("secret",  secret);
            if (baseUrl is not null) w.WriteString("baseUrl", baseUrl);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
