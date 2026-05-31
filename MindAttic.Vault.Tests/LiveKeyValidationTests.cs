using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Credentials;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

/// <summary>
/// Live validation that the credentials <b>Vault itself resolves</b> — through
/// the exact production chain (<see cref="ConfigurationBuilderExtensions.AddMindAtticVaultFiles"/>
/// + environment variables + Azure Key Vault when the host wires it) — actually
/// authenticate against each trusted provider's real endpoint.
///
/// <para>This is the credential authority's own answer to "are the keys good?".
/// It deliberately does NOT depend on MindAttic.Legion (which depends on Vault):
/// it resolves the key via Vault's <see cref="CompositeCredentialStore"/> and
/// hits each provider's cheap <c>GET /models</c>-style endpoint — <b>zero token
/// spend</b> — so a 401/403 is unambiguously a key problem, not a request-shape
/// or billing one.</para>
///
/// <para>The <c>LiveKeysTrusted</c>-tagged gate is wired into the Vault
/// <c>pre-commit</c> hook (<c>.githooks/pre-commit</c>): no point committing when
/// a trusted-panel key is dead. Kept <c>[Explicit]</c> so normal <c>dotnet test</c>
/// stays offline/deterministic.</para>
/// <code>
///   dotnet test --filter "Category=LiveKeysTrusted"   # the pre-commit gate
/// </code>
/// </summary>
[TestFixture]
[Category("LiveKeys")]
[Explicit("Hits real provider APIs with the live Vault-resolved keys — depends on network. Run on demand / in the pre-commit gate.")]
public class LiveKeyValidationTests
{
    /// <summary>The trusted voting panel — the only providers the gate blocks on.</summary>
    private static readonly string[] TrustedFour = { "claude", "openai", "gemini", "deepseek" };

    [Test]
    [Category("LiveKeysTrusted")]
    public async Task TrustedPanel_EveryKeyAuthenticatesLive()
    {
        var store = BuildProductionStore();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var failures = new List<string>();
        foreach (var id in TrustedFour)
        {
            var key = store.GetKey(id);
            if (string.IsNullOrWhiteSpace(key))
            {
                failures.Add($"{id}: NO key resolved from the Vault chain (providers.json / env / Key Vault).");
                continue;
            }

            try
            {
                using var req = BuildAuthProbe(id, key);
                using var res = await http.SendAsync(req);
                if (res.IsSuccessStatusCode)
                {
                    TestContext.WriteLine($"{id}: OK (HTTP {(int)res.StatusCode})");
                    continue;
                }

                var why = (int)res.StatusCode switch
                {
                    401 => "AuthInvalid — key revoked / expired; rotate it",
                    403 => "AuthForbidden — key disabled or lacks access; check the account",
                    429 => "RateLimited / quota — check billing",
                    _   => "unexpected non-success status",
                };
                failures.Add($"{id}: HTTP {(int)res.StatusCode} — {why}.");
            }
            catch (Exception ex)
            {
                failures.Add($"{id}: live probe threw — {ex.Message}");
            }
        }

        Assert.That(failures, Is.Empty,
            "trusted-panel keys that FAILED live validation — fix/rotate before committing:\n  - "
            + string.Join("\n  - ", failures));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The same credential chain production uses: the on-disk canonical files
    /// (surfaced via <c>AddMindAtticVaultFiles</c>) layered with environment
    /// variables — and Azure Key Vault when the host has registered it upstream —
    /// read first, with the writable file store behind as fallback.
    /// </summary>
    private static ICredentialStore BuildProductionStore()
    {
        var cfg = new ConfigurationBuilder()
            .AddMindAtticVaultFiles()
            .AddEnvironmentVariables()
            .Build();

        return new CompositeCredentialStore(
            ConfigurationCredentialStore.ForLlm(cfg),
            LlmCredentialStore.Default);
    }

    /// <summary>
    /// A cheap, token-free auth probe per provider — <c>GET /models</c> with the
    /// provider's auth header. A valid key returns 2xx; a dead key returns 401/403.
    /// </summary>
    private static HttpRequestMessage BuildAuthProbe(string providerId, string key) => providerId switch
    {
        "claude" => Configure(new(HttpMethod.Get, "https://api.anthropic.com/v1/models"), r =>
        {
            r.Headers.Add("x-api-key", key);
            r.Headers.Add("anthropic-version", "2023-06-01");
        }),
        "openai" => Configure(new(HttpMethod.Get, "https://api.openai.com/v1/models"), r =>
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key)),
        "gemini" => Configure(new(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models"), r =>
            r.Headers.Add("x-goog-api-key", key)),
        "deepseek" => Configure(new(HttpMethod.Get, "https://api.deepseek.com/models"), r =>
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key)),
        _ => throw new ArgumentException($"No auth probe defined for provider '{providerId}'.", nameof(providerId)),
    };

    private static HttpRequestMessage Configure(HttpRequestMessage req, Action<HttpRequestMessage> configure)
    {
        configure(req);
        return req;
    }
}
