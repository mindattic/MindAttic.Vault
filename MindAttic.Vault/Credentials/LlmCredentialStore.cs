using System.Text.Json;
using MindAttic.Vault.Paths;

namespace MindAttic.Vault.Credentials;

/// <summary>
/// LLM keyring at <c>%APPDATA%\MindAttic\LLM\</c>. Per-provider entry shape:
/// <code>
/// {
///   "claude":  { "type": "anthropic", "apiKey": "sk-ant-...", "model": "claude-sonnet-4-6", "maxTokens": 8192 },
///   "gemini":  { "type": "google",    "apiKey": "AIza..." },
///   "grok":    { "type": "bearer",    "apiKey": "xai-..." }
/// }
/// </code>
///
/// <para>Drop-in replacement for the legacy <c>MindAttic.Legion.MindAtticCredentialStore</c>.
/// Override the directory for tests with the <c>MINDATTIC_LLM_CREDENTIALS</c>
/// env var (kept for backward-compat with Legion's existing test harness).</para>
///
/// <para>For cloud-native deployments, prefer <see cref="LlmCredentialResolver"/>,
/// which reads from <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
/// first and falls back to this file store.</para>
/// </summary>
public sealed class LlmCredentialStore : CredentialStore
{
    /// <summary>Bucket folder name under <c>%APPDATA%\MindAttic\</c>.</summary>
    public const string Bucket          = "LLM";

    /// <summary>Environment variable that overrides the resolved bucket directory.</summary>
    public const string DirectoryEnvVar = "MINDATTIC_LLM_CREDENTIALS";

    /// <summary>
    /// Default instance pointed at <c>%APPDATA%\MindAttic\LLM\</c>
    /// (or the value of <c>MINDATTIC_LLM_CREDENTIALS</c> if set).
    /// </summary>
    /// <remarks>
    /// This property is captured once at type-load time. Setting the env var after
    /// the type has been touched will not change <see cref="Default"/> — construct
    /// a fresh <see cref="LlmCredentialStore"/> if you need a runtime override.
    /// </remarks>
    public static LlmCredentialStore Default { get; } = new(ResolveDefaultDirectory());

    /// <summary>Construct an LLM credential store rooted at <paramref name="directory"/>.</summary>
    /// <inheritdoc />
    public LlmCredentialStore(string directory) : base(directory) { }

    private static string ResolveDefaultDirectory() =>
        Environment.GetEnvironmentVariable(DirectoryEnvVar)
        ?? VaultPaths.RoamingBucket(Bucket);

    /// <summary>
    /// Preserves <c>type</c>, <c>model</c>, and <c>maxTokens</c> when present.
    /// When <c>type</c> is missing, infers from provider id
    /// (<c>claude</c> → anthropic, <c>gemini</c> → google, otherwise <c>bearer</c>)
    /// to match Legion's existing behaviour.
    /// </summary>
    /// <inheritdoc />
    protected override string MergeApiKeyIntoProviderJson(string? existingJson, string providerId, string apiKey)
    {
        // Pull every preservable field out of the existing JSON. We only know
        // about three fields explicitly; arbitrary user-added fields are dropped
        // here (the base CredentialStore preserves them, but for LLM entries the
        // canonical shape is {type, apiKey, model?, maxTokens?} — we deliberately
        // canonicalize on every write to keep the on-disk file tidy).
        string? type      = null;
        string? model     = null;
        int?    maxTokens = null;

        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(existingJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("type",      out var t)  && t.ValueKind  == JsonValueKind.String) type      = t.GetString();
                    if (doc.RootElement.TryGetProperty("model",     out var m)  && m.ValueKind  == JsonValueKind.String) model     = m.GetString();
                    if (doc.RootElement.TryGetProperty("maxTokens", out var mt) && mt.ValueKind == JsonValueKind.Number) maxTokens = mt.GetInt32();
                }
            }
            catch { /* malformed entry — fall back to inferred defaults below. */ }
        }

        // Infer the type when not already set. The mapping mirrors Legion 0.x.
        type ??= providerId.Equals("claude", StringComparison.OrdinalIgnoreCase) ? "anthropic"
              :  providerId.Equals("gemini", StringComparison.OrdinalIgnoreCase) ? "google"
              :  "bearer";

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("type", type);
            w.WriteString("apiKey", apiKey);
            // Optional fields are only emitted when actually present, keeping the
            // on-disk file lean for new providers.
            if (!string.IsNullOrWhiteSpace(model)) w.WriteString("model", model);
            if (maxTokens.HasValue)                w.WriteNumber("maxTokens", maxTokens.Value);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
