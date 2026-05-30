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

    private static string ResolveDefaultDirectory()
    {
        // Treat a blank override (env var set to "" / whitespace) as unset: a plain
        // ?? would pass the blank straight into the base ctor's IsNullOrWhiteSpace
        // guard, throwing a TypeInitializationException the first time Default is touched.
        var overrideDir = Environment.GetEnvironmentVariable(DirectoryEnvVar);
        return string.IsNullOrWhiteSpace(overrideDir) ? VaultPaths.RoamingBucket(Bucket) : overrideDir;
    }

    /// <summary>
    /// Preserves <c>type</c>, <c>model</c>, and <c>maxTokens</c> when present.
    /// When <c>type</c> is missing, infers from provider id
    /// (<c>claude</c> → anthropic, <c>gemini</c> → google, otherwise <c>bearer</c>)
    /// to match Legion's existing behaviour. User-added fields outside the
    /// canonical {type, apiKey, model, maxTokens} set are preserved verbatim,
    /// matching the base <see cref="CredentialStore"/> contract.
    /// </summary>
    /// <inheritdoc />
    protected override string MergeApiKeyIntoProviderJson(string? existingJson, string providerId, string apiKey)
    {
        string? type      = null;
        string? model     = null;
        int?    maxTokens = null;
        // Holds any property that isn't part of the canonical LLM shape so users
        // can add arbitrary fields (organization, endpoint, etc.) without losing
        // them on the next rotation.
        var extras = new List<KeyValuePair<string, string>>();

        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(existingJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        // The old apiKey (any casing) is always replaced by the new
                        // value below — drop it. Every OTHER sibling must survive the
                        // rotation; anything we don't canonicalize is preserved verbatim
                        // as an extra (see the else arm).
                        if (prop.Name.Equals("apiKey", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (prop.NameEquals("type") && prop.Value.ValueKind == JsonValueKind.String)
                            type = prop.Value.GetString();
                        else if (prop.NameEquals("model") && prop.Value.ValueKind == JsonValueKind.String)
                            model = prop.Value.GetString();
                        else if (prop.NameEquals("maxTokens") && prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var mtVal))
                            maxTokens = mtVal;
                        else
                            // Preserve verbatim rather than dropping: user-added fields,
                            // a case-variant canonical spelling ("Model"), a non-string
                            // type/model, or a maxTokens that isn't an Int32 number
                            // (string hand-edit or > int.MaxValue). Losing any sibling on
                            // rotation violates the store contract; the prior maxTokens
                            // fix established this, and it must apply to every field.
                            extras.Add(new KeyValuePair<string, string>(prop.Name, prop.Value.GetRawText()));
                    }
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
            // Optional canonical fields are only emitted when actually present,
            // keeping the on-disk file lean for new providers.
            if (!string.IsNullOrWhiteSpace(model)) w.WriteString("model", model);
            if (maxTokens.HasValue)                w.WriteNumber("maxTokens", maxTokens.Value);
            // User-added fields trail the canonical block so a hand-edited file
            // stays readable (canonical keys clustered up top, extras after).
            foreach (var extra in extras)
            {
                w.WritePropertyName(extra.Key);
                using var subDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(extra.Value) ? "null" : extra.Value);
                subDoc.RootElement.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
