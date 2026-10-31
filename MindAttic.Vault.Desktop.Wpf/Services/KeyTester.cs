using System.Net.Http.Headers;
using MindAttic.Vault.Credentials;

namespace MindAttic.Vault.Desktop.Services;

/// <summary>How a key test came out.</summary>
public enum KeyTestOutcome
{
    /// <summary>The provider accepted the key.</summary>
    Ok,
    /// <summary>The provider refused the key (401/402/403/429).</summary>
    KeyFailed,
    /// <summary>No cheap authenticated call is known for this provider.</summary>
    NotAvailable,
    /// <summary>Network trouble or an unexpected status: the key isn't blamed.</summary>
    Inconclusive,
}

/// <summary>A key test's result. Its message names the provider and status, never the key.</summary>
public sealed record KeyTestResult(KeyTestOutcome Outcome, string Message, KeyFailureKind? FailureKind = null, TimeSpan? RetryAfter = null);

/// <summary>One cheap authenticated call per provider, to see whether a key works.</summary>
public interface IKeyTester
{
    bool CanTest(string providerId);
    Task<KeyTestResult> TestAsync(string providerId, string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Lists the provider's models (or its key info) with the key: <c>claude</c> GET api.anthropic.com/v1/models with
/// <c>x-api-key</c> and <c>anthropic-version</c>, <c>openai</c> GET api.openai.com/v1/models with a bearer token,
/// <c>gemini</c> GET generativelanguage.googleapis.com/v1beta/models?key=, and the bearer models endpoint of the other
/// OpenAI-style providers. The result is classified with <see cref="KeySelector.Classify(System.Net.HttpStatusCode)"/>.
/// Outbound HTTPS only, through an injectable handler.
/// </summary>
public sealed class ProviderKeyTester : IKeyTester
{
    public const string AnthropicVersion = "2023-06-01";

    private sealed record Endpoint(Func<string, Uri> Url, Action<HttpRequestHeaders, string> Authorize);

    private static readonly Action<HttpRequestHeaders, string> Bearer = (h, key) => h.Authorization = new AuthenticationHeaderValue("Bearer", key);

    private static Endpoint BearerGet(string url) => new(_ => new Uri(url), Bearer);

    private static readonly Dictionary<string, Endpoint> Endpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = new(_ => new Uri("https://api.anthropic.com/v1/models"), (h, key) =>
        {
            h.Add("x-api-key", key);
            h.Add("anthropic-version", AnthropicVersion);
        }),
        ["openai"] = BearerGet("https://api.openai.com/v1/models"),
        ["gemini"] = new(key => new Uri("https://generativelanguage.googleapis.com/v1beta/models?key=" + Uri.EscapeDataString(key)), (_, _) => { }),
        ["deepseek"] = BearerGet("https://api.deepseek.com/models"),
        ["kimi"] = BearerGet("https://api.moonshot.ai/v1/models"),
        ["mistral"] = BearerGet("https://api.mistral.ai/v1/models"),
        ["grok"] = BearerGet("https://api.x.ai/v1/models"),
        ["groq"] = BearerGet("https://api.groq.com/openai/v1/models"),
        ["openrouter"] = BearerGet("https://openrouter.ai/api/v1/key"),
        ["together"] = BearerGet("https://api.together.xyz/v1/models"),
        ["fireworks"] = BearerGet("https://api.fireworks.ai/inference/v1/models"),
        ["cohere"] = BearerGet("https://api.cohere.com/v1/models"),
        ["elevenlabs"] = new(_ => new Uri("https://api.elevenlabs.io/v1/models"), (h, key) => h.Add("xi-api-key", key)),
    };

    private readonly HttpClient http;

    public ProviderKeyTester(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: handler is null) { Timeout = timeout ?? TimeSpan.FromSeconds(15) };
    }

    public bool CanTest(string providerId) => Endpoints.ContainsKey(ProviderCatalog.Normalize(providerId));

    public async Task<KeyTestResult> TestAsync(string providerId, string key, CancellationToken cancellationToken = default)
    {
        var provider = ProviderCatalog.Get(providerId);
        if (!Endpoints.TryGetValue(provider.Id, out var endpoint)) return new(KeyTestOutcome.NotAvailable, "Test not available");
        if (string.IsNullOrWhiteSpace(key)) return new(KeyTestOutcome.KeyFailed, "No key to test", KeyFailureKind.Auth);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Url(key.Trim()));
        endpoint.Authorize(request.Headers, key.Trim());
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode) return new(KeyTestOutcome.Ok, $"{provider.DisplayName} accepted the key (HTTP {status})");
            if (KeySelector.Classify(response.StatusCode) is { } kind)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta
                                 ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
                return new(KeyTestOutcome.KeyFailed, $"{provider.DisplayName} refused the key (HTTP {status}, {Describe(kind)})", kind, retryAfter);
            }
            return new(KeyTestOutcome.Inconclusive, $"{provider.DisplayName} answered HTTP {status}; the key wasn't blamed");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(KeyTestOutcome.Inconclusive, $"{provider.DisplayName} didn't answer in time; the key wasn't blamed");
        }
        catch (HttpRequestException)
        {
            // Never the exception's message: it can carry the request URL, and Gemini's carries the key.
            return new(KeyTestOutcome.Inconclusive, $"Couldn't reach {provider.DisplayName}; the key wasn't blamed");
        }
    }

    private static string Describe(KeyFailureKind kind) => kind switch
    {
        KeyFailureKind.Auth => "wrong or revoked",
        KeyFailureKind.Quota => "out of quota",
        _ => "rate limited",
    };
}
