using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Credentials;

namespace MindAttic.Vault.Resolution;

/// <summary>
/// Chained credential resolver. Each delegate is tried in order; the first
/// non-empty value wins. Mirrors the explicit precedence chain documented in
/// IdiotProof:
///
/// <code>
///   explicit DI &gt; env var &gt; vault providers.json &gt; database / fallback
/// </code>
///
/// <para>Build one with <see cref="From"/> + <see cref="Then"/>, or use the
/// convenience helpers (<see cref="Explicit"/>, <see cref="Env"/>,
/// <see cref="EnvByConvention"/>, <see cref="FromStore"/>,
/// <see cref="FromConfiguration"/>) to wire common steps.</para>
///
/// <para>A throwing step is treated as "no value" so a misbehaving step can't
/// kill the chain. All values are trimmed before being returned.</para>
/// </summary>
public sealed class KeyResolver
{
    private readonly List<Func<string, string?>> resolvers = new();

    /// <summary>Start a new chain with an initial resolver step.</summary>
    /// <param name="resolver">The first step. Required.</param>
    /// <returns>A new <see cref="KeyResolver"/> with the step appended.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="resolver"/> is null.</exception>
    public static KeyResolver From(Func<string, string?> resolver) => new KeyResolver().Then(resolver);

    /// <summary>Append a step to the chain.</summary>
    /// <param name="resolver">The step to append. Required.</param>
    /// <returns>This instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="resolver"/> is null.</exception>
    public KeyResolver Then(Func<string, string?> resolver)
    {
        if (resolver is null) throw new ArgumentNullException(nameof(resolver));
        resolvers.Add(resolver);
        return this;
    }

    /// <summary>Resolve a provider id by walking the chain.</summary>
    /// <param name="providerId">
    /// Provider id (e.g. <c>"claude"</c>). Empty/whitespace returns <c>null</c>.
    /// </param>
    /// <returns>
    /// The first non-empty value from any step (trimmed), or <c>null</c> if
    /// nothing matched.
    /// </returns>
    public string? Resolve(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;
        foreach (var step in resolvers)
        {
            string? value;
            // A throwing step is treated as "no value" so a misbehaving step
            // (e.g. a network-backed lookup that timed out) doesn't kill the chain.
            try { value = step(providerId); }
            catch { value = null; }
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    // ── convenience step builders ───────────────────────────────────────────────

    /// <summary>
    /// Step that returns a single hard-coded value when the resolver's
    /// provider id matches <paramref name="forProviderId"/>. Useful for tests
    /// or for promoting a value injected via DI ahead of every other source.
    /// </summary>
    /// <param name="forProviderId">The provider id this step matches. Case-insensitive.</param>
    /// <param name="value">The value to return on match. Empty/whitespace yields no match.</param>
    public static Func<string, string?> Explicit(string forProviderId, string? value) =>
        id => string.Equals(id, forProviderId, StringComparison.OrdinalIgnoreCase)
              && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    /// <summary>
    /// Step that reads a fixed environment variable for a fixed provider id.
    /// </summary>
    /// <param name="forProviderId">The provider id this step matches. Case-insensitive.</param>
    /// <param name="envVar">The environment variable name to read. Empty/whitespace yields no match.</param>
    public static Func<string, string?> Env(string forProviderId, string envVar) =>
        id =>
        {
            if (!string.Equals(id, forProviderId, StringComparison.OrdinalIgnoreCase)) return null;
            if (string.IsNullOrWhiteSpace(envVar)) return null;
            var v = Environment.GetEnvironmentVariable(envVar);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        };

    /// <summary>
    /// Step that reads <c>{providerId}_API_KEY</c> from the environment,
    /// normalised to uppercase with non-alphanumeric chars replaced by
    /// underscores. Lets every provider get a deterministic env-var name
    /// without per-provider plumbing.
    /// </summary>
    /// <param name="suffix">
    /// Suffix appended to the normalised provider id. Defaults to
    /// <c>"_API_KEY"</c>.
    /// </param>
    /// <example>
    /// <c>"alpaca-paper"</c> reads <c>ALPACA_PAPER_API_KEY</c>.
    /// </example>
    public static Func<string, string?> EnvByConvention(string suffix = "_API_KEY") =>
        id =>
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            var envVar = NormaliseToEnvName(id) + suffix;
            var v = Environment.GetEnvironmentVariable(envVar);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        };

    /// <summary>
    /// Step that delegates to a <see cref="ICredentialStore"/> (e.g. the LLM or
    /// broker keyring). A null store yields no match.
    /// </summary>
    /// <param name="store">The credential store to query.</param>
    public static Func<string, string?> FromStore(ICredentialStore store) =>
        id => store?.GetKey(id);

    /// <summary>
    /// Step that reads <c>{bucketSection}:{providerId}:apiKey</c> from
    /// <paramref name="configuration"/>. Use
    /// <see cref="VaultConfigurationKeys.LlmSection"/> or
    /// <see cref="VaultConfigurationKeys.BrokersSection"/> for the standard
    /// MindAttic schema, or any custom path you have set up.
    /// </summary>
    /// <param name="configuration">The configuration root. Required.</param>
    /// <param name="bucketSection">Colon-delimited section path. Required.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="bucketSection"/> is null/whitespace.</exception>
    public static Func<string, string?> FromConfiguration(IConfiguration configuration, string bucketSection)
    {
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        if (string.IsNullOrWhiteSpace(bucketSection))
            throw new ArgumentException("Bucket section is required.", nameof(bucketSection));

        return id =>
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            var path = VaultConfigurationKeys.ProviderApiKeyPath(bucketSection, id);
            return configuration[path];
        };
    }

    /// <summary>
    /// Uppercases <paramref name="id"/> and replaces every non-alphanumeric
    /// character with <c>_</c>. <c>"alpaca-paper"</c> → <c>"ALPACA_PAPER"</c>.
    /// </summary>
    private static string NormaliseToEnvName(string id)
    {
        Span<char> buf = stackalloc char[id.Length];
        for (int i = 0; i < id.Length; i++)
        {
            var c = id[i];
            buf[i] = (char.IsLetterOrDigit(c)) ? char.ToUpperInvariant(c) : '_';
        }
        return new string(buf);
    }
}
