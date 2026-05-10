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
/// Drop-in replacement for <c>MindAttic.Legion.MindAtticCredentialStore</c>.
/// Override the directory for tests with the <c>MINDATTIC_LLM_CREDENTIALS</c> env var
/// (kept for backward-compat with Legion's existing test harness).
/// </summary>
public sealed class LlmCredentialStore : CredentialStore
{
    public const string Bucket          = "LLM";
    public const string DirectoryEnvVar = "MINDATTIC_LLM_CREDENTIALS";

    /// <summary>
    /// Default instance pointed at <c>%APPDATA%\MindAttic\LLM\</c>
    /// (or the value of <c>MINDATTIC_LLM_CREDENTIALS</c> if set).
    /// </summary>
    public static LlmCredentialStore Default { get; } = new(ResolveDefaultDirectory());

    public LlmCredentialStore(string directory) : base(directory) { }

    private static string ResolveDefaultDirectory() =>
        Environment.GetEnvironmentVariable(DirectoryEnvVar)
        ?? VaultPaths.RoamingBucket(Bucket);

    /// <summary>
    /// Preserves <c>type</c>, <c>model</c>, and <c>maxTokens</c> when present.
    /// When <c>type</c> is missing, infers from provider id (anthropic/google/bearer)
    /// to match Legion's existing behaviour.
    /// </summary>
    protected override string MergeApiKeyIntoProviderJson(string? existingJson, string providerId, string apiKey)
    {
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
            catch { }
        }

        type ??= providerId.Equals("claude", StringComparison.OrdinalIgnoreCase) ? "anthropic"
              :  providerId.Equals("gemini", StringComparison.OrdinalIgnoreCase) ? "google"
              :  "bearer";

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("type", type);
            w.WriteString("apiKey", apiKey);
            if (!string.IsNullOrWhiteSpace(model)) w.WriteString("model", model);
            if (maxTokens.HasValue)                w.WriteNumber("maxTokens", maxTokens.Value);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
