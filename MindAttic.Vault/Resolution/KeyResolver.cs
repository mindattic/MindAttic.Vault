using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Credentials;

namespace MindAttic.Vault.Resolution;

/// <summary>
/// Chained credential resolver. Each delegate is tried in order; the first non-empty
/// value wins. Mirrors the explicit precedence chain documented in IdiotProof:
///
/// <code>
///   explicit DI > env var > vault providers.json > database / fallback
/// </code>
///
/// Build one with <see cref="From"/> + <see cref="Then"/>, or use the convenience
/// helpers (<see cref="Explicit"/>, <see cref="Env"/>, <see cref="FromStore"/>) to
/// wire common steps.
/// </summary>
public sealed class KeyResolver
{
    private readonly List<Func<string, string?>> resolvers = new();

    /// <summary>Start a new chain with an initial resolver step.</summary>
    public static KeyResolver From(Func<string, string?> resolver) => new KeyResolver().Then(resolver);

    /// <summary>Append a step to the chain.</summary>
    public KeyResolver Then(Func<string, string?> resolver)
    {
        if (resolver is null) throw new ArgumentNullException(nameof(resolver));
        resolvers.Add(resolver);
        return this;
    }

    /// <summary>Resolve a provider id by walking the chain. Returns null if nothing matched.</summary>
    public string? Resolve(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;
        foreach (var step in resolvers)
        {
            string? value;
            try { value = step(providerId); }
            catch { value = null; }
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    // ── convenience step builders ───────────────────────────────────────────────

    /// <summary>
    /// Step that returns a single hard-coded value when <paramref name="providerId"/>
    /// matches <paramref name="forProviderId"/>. Useful for tests or for promoting
    /// a value injected via DI ahead of every other source.
    /// </summary>
    public static Func<string, string?> Explicit(string forProviderId, string? value) =>
        id => string.Equals(id, forProviderId, StringComparison.OrdinalIgnoreCase)
              && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    /// <summary>
    /// Step that reads a fixed environment variable for a fixed provider id.
    /// </summary>
    public static Func<string, string?> Env(string forProviderId, string envVar) =>
        id =>
        {
            if (!string.Equals(id, forProviderId, StringComparison.OrdinalIgnoreCase)) return null;
            if (string.IsNullOrWhiteSpace(envVar)) return null;
            var v = Environment.GetEnvironmentVariable(envVar);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        };

    /// <summary>
    /// Step that reads <c>{providerId}_API_KEY</c> from the environment, normalised to
    /// uppercase with non-alphanumeric chars replaced by underscores. Lets every
    /// provider get a deterministic env-var name without per-provider plumbing.
    /// </summary>
    public static Func<string, string?> EnvByConvention(string suffix = "_API_KEY") =>
        id =>
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            var envVar = NormaliseToEnvName(id) + suffix;
            var v = Environment.GetEnvironmentVariable(envVar);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        };

    /// <summary>
    /// Step that delegates to a <see cref="ICredentialStore"/> (e.g. the LLM or broker keyring).
    /// </summary>
    public static Func<string, string?> FromStore(ICredentialStore store) =>
        id => store?.GetKey(id);

    /// <summary>
    /// Step that reads <c>{bucketSection}:{providerId}:apiKey</c> from
    /// <paramref name="configuration"/>. Use <see cref="VaultConfigurationKeys.LlmSection"/>
    /// or <see cref="VaultConfigurationKeys.BrokersSection"/> for the standard
    /// MindAttic schema, or any custom path you have set up.
    /// </summary>
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
