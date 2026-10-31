namespace MindAttic.Vault.Credentials;

/// <summary>What kind of service a provider is; Vault.Desktop's Keys tab groups by it.</summary>
public enum ProviderCategory { Llm, Broker, Media, Tts, Other }

/// <summary>One provider every MindAttic app can share keys for.</summary>
/// <param name="Id">Canonical lowercase id (e.g. <c>claude</c>).</param>
/// <param name="DisplayName">Name shown in the UI.</param>
/// <param name="Category">Grouping on the Keys tab.</param>
/// <param name="Bucket">Credential bucket folder under <c>%APPDATA%\MindAttic\</c> holding its keys.</param>
/// <param name="KeyPageUrl">Where to get a key, or null.</param>
/// <param name="Aliases">Other ids that mean the same provider (merged on read).</param>
public sealed record ProviderInfo(
    string Id,
    string DisplayName,
    ProviderCategory Category,
    string Bucket,
    string? KeyPageUrl,
    IReadOnlyList<string> Aliases);

/// <summary>
/// The one list of providers for the whole workspace. Aliases (<c>anthropic</c>, <c>google</c>,
/// <c>xai</c>) resolve to their canonical id so their keys merge into one named list. Unknown ids
/// pass through unchanged as their own provider in the LLM bucket.
/// </summary>
public static class ProviderCatalog
{
    /// <summary>Bucket for non-LLM, non-broker service keys (TTS, media servers).</summary>
    public const string ServicesBucket = "Services";

    /// <summary>Every known provider.</summary>
    public static IReadOnlyList<ProviderInfo> All { get; } = new ProviderInfo[]
    {
        new("claude",     "Claude",     ProviderCategory.Llm,    LlmCredentialStore.Bucket, "https://console.anthropic.com/settings/keys", new[] { "anthropic" }),
        new("openai",     "OpenAI",     ProviderCategory.Llm,    LlmCredentialStore.Bucket, "https://platform.openai.com/api-keys", Array.Empty<string>()),
        new("gemini",     "Gemini",     ProviderCategory.Llm,    LlmCredentialStore.Bucket, "https://aistudio.google.com/app/apikey", new[] { "google" }),
        new("deepseek",   "DeepSeek",   ProviderCategory.Llm,    LlmCredentialStore.Bucket, "https://platform.deepseek.com/api_keys", Array.Empty<string>()),
        new("kimi",       "Kimi",       ProviderCategory.Llm,    LlmCredentialStore.Bucket, "https://platform.moonshot.ai/console/api-keys", new[] { "moonshot" }),
        new("mistral",    "Mistral",    ProviderCategory.Llm,    LlmCredentialStore.Bucket, "https://console.mistral.ai/api-keys", Array.Empty<string>()),
        new("grok",       "Grok",       ProviderCategory.Llm,    LlmCredentialStore.Bucket, "https://console.x.ai", new[] { "xai" }),
        new("groq",       "Groq",       ProviderCategory.Llm,    LlmCredentialStore.Bucket, "https://console.groq.com/keys", Array.Empty<string>()),
        new("perplexity", "Perplexity", ProviderCategory.Llm,    LlmCredentialStore.Bucket, "https://www.perplexity.ai/settings/api", Array.Empty<string>()),
        new("cohere",     "Cohere",     ProviderCategory.Llm,    LlmCredentialStore.Bucket, "https://dashboard.cohere.com/api-keys", Array.Empty<string>()),
        new("openrouter", "OpenRouter", ProviderCategory.Llm,    LlmCredentialStore.Bucket, "https://openrouter.ai/keys", Array.Empty<string>()),
        new("ollama",     "Ollama",     ProviderCategory.Llm,    LlmCredentialStore.Bucket, null, Array.Empty<string>()),
        new("alpaca",     "Alpaca",     ProviderCategory.Broker, BrokerCredentialStore.Bucket, "https://app.alpaca.markets/paper/dashboard/overview", Array.Empty<string>()),
        new("elevenlabs", "ElevenLabs", ProviderCategory.Tts,    ServicesBucket, "https://elevenlabs.io/app/settings/api-keys", Array.Empty<string>()),
        new("jellyfin",   "Jellyfin",   ProviderCategory.Media,  ServicesBucket, null, Array.Empty<string>()),
    };

    private static readonly Dictionary<string, ProviderInfo> ById = BuildIndex();

    private static Dictionary<string, ProviderInfo> BuildIndex()
    {
        var map = new Dictionary<string, ProviderInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in All)
        {
            map[p.Id] = p;
            foreach (var a in p.Aliases) map[a] = p;
        }
        return map;
    }

    /// <summary>The canonical id for <paramref name="id"/> (aliases resolved, trimmed, lowercased).</summary>
    public static string Normalize(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Provider id is required.", nameof(id));
        var trimmed = id.Trim();
        return ById.TryGetValue(trimmed, out var p) ? p.Id : trimmed.ToLowerInvariant();
    }

    /// <summary>The catalog entry, or a synthesized LLM-bucket entry for an unknown id.</summary>
    public static ProviderInfo Get(string id)
    {
        var canonical = Normalize(id);
        return ById.TryGetValue(canonical, out var p)
            ? p
            : new ProviderInfo(canonical, canonical, ProviderCategory.Other, LlmCredentialStore.Bucket, null, Array.Empty<string>());
    }

    /// <summary>True when <paramref name="id"/> is a known id or alias.</summary>
    public static bool IsKnown(string id) => !string.IsNullOrWhiteSpace(id) && ById.ContainsKey(id.Trim());

    /// <summary>The canonical id followed by every alias, the ids whose stored keys belong to this provider.</summary>
    public static IReadOnlyList<string> IdsFor(string id)
    {
        var p = Get(id);
        return new[] { p.Id }.Concat(p.Aliases).ToList();
    }
}
