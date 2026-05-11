using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;

namespace MindAttic.Vault.Credentials;

/// <summary>
/// <see cref="ICredentialStore"/> backed by <see cref="IConfiguration"/> at a
/// specific bucket section (e.g. <c>MindAttic:Vault:LLM</c>). This is the
/// cloud-native primary path: the same code reads from User Secrets in dev,
/// App Service Application Settings (incl. Azure Key Vault references) in
/// production, or Azure Key Vault directly when the host has registered
/// <c>AddAzureKeyVault(...)</c> upstream.
///
/// <para>Read-only by design. Writes (e.g. rotating a key from a settings UI)
/// must go to a writable backing store such as <see cref="CredentialStore"/>.
/// Use <see cref="CompositeCredentialStore"/> to chain a writable file store
/// behind this read-only configuration view.</para>
///
/// <para><b>Schema mapping:</b> a provider entry at
/// <c>{bucketSection}:{providerId}</c> with children
/// (<c>apiKey</c>, <c>type</c>, <c>model</c>, ...) is reconstructed into a
/// JSON object by <see cref="LoadAllRaw"/>. Numeric-keyed children are
/// rendered as a JSON array, matching the standard
/// <see cref="IConfiguration"/> array convention.</para>
/// </summary>
public sealed class ConfigurationCredentialStore : ICredentialStore
{
    private readonly IConfiguration configuration;
    private readonly string bucketSection;

    /// <summary>
    /// Construct a store reading from <paramref name="configuration"/> at
    /// <paramref name="bucketSection"/>
    /// (e.g. <see cref="VaultConfigurationKeys.LlmSection"/>).
    /// </summary>
    /// <param name="configuration">The configuration root. Required.</param>
    /// <param name="bucketSection">
    /// Colon-delimited section path (e.g. <c>"MindAttic:Vault:LLM"</c>). Required.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="bucketSection"/> is null/whitespace.</exception>
    public ConfigurationCredentialStore(IConfiguration configuration, string bucketSection)
    {
        this.configuration  = configuration ?? throw new ArgumentNullException(nameof(configuration));
        if (string.IsNullOrWhiteSpace(bucketSection))
            throw new ArgumentException("Bucket section is required.", nameof(bucketSection));
        this.bucketSection = bucketSection;
    }

    /// <summary>Convenience: a store rooted at <c>MindAttic:Vault:LLM</c>.</summary>
    /// <param name="configuration">The configuration root. Required.</param>
    public static ConfigurationCredentialStore ForLlm(IConfiguration configuration) =>
        new(configuration, VaultConfigurationKeys.LlmSection);

    /// <summary>Convenience: a store rooted at <c>MindAttic:Vault:Brokers</c>.</summary>
    /// <param name="configuration">The configuration root. Required.</param>
    public static ConfigurationCredentialStore ForBrokers(IConfiguration configuration) =>
        new(configuration, VaultConfigurationKeys.BrokersSection);

    /// <summary>The configuration section path this store is rooted at.</summary>
    public string BucketSection => bucketSection;

    /// <summary>Synthetic sentinel — there is no on-disk directory for a configuration-backed store.</summary>
    public string Directory          => "(configuration)";

    /// <summary>Synthetic sentinel including the bucket section, useful in diagnostics/logs.</summary>
    public string ProvidersFilePath  => "(configuration:" + bucketSection + ")";

    /// <inheritdoc />
    /// <remarks>
    /// Returns true when the bucket section has at least one child (i.e. at least
    /// one provider is configured), regardless of whether each provider has an
    /// <c>apiKey</c>.
    /// </remarks>
    public bool ProvidersFileExists() => configuration.GetSection(bucketSection).GetChildren().Any();

    /// <inheritdoc />
    public string? GetKey(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;
        var path = VaultConfigurationKeys.ProviderApiKeyPath(bucketSection, providerId);
        var raw = configuration[path];
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    /// <summary>Always throws — this store is read-only.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public void SetKey(string providerId, string apiKey) =>
        throw new NotSupportedException(
            "ConfigurationCredentialStore is read-only. Wrap it with CompositeCredentialStore over a writable CredentialStore, or write to the underlying IConfiguration source (User Secrets / App Service Application Settings / Key Vault) directly.");

    /// <inheritdoc />
    public Dictionary<string, string> LoadAll()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = configuration.GetSection(bucketSection);
        foreach (var providerSection in section.GetChildren())
        {
            var providerId = providerSection.Key;
            if (string.IsNullOrWhiteSpace(providerId)) continue;
            var apiKey = providerSection[VaultConfigurationKeys.ApiKeyProperty];
            if (!string.IsNullOrWhiteSpace(apiKey))
                result[providerId] = apiKey.Trim();
        }
        return result;
    }

    /// <inheritdoc />
    public List<string> ListProviders() => LoadAll().Keys.ToList();

    /// <inheritdoc />
    public Dictionary<string, string> LoadAllRaw()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = configuration.GetSection(bucketSection);
        foreach (var providerSection in section.GetChildren())
        {
            var providerId = providerSection.Key;
            if (string.IsNullOrWhiteSpace(providerId)) continue;
            // Reconstruct the provider's entire subtree as a JSON object so callers
            // can deserialize the rich payload (type/model/secret/etc.) directly.
            result[providerId] = SerializeSectionAsObject(providerSection);
        }
        return result;
    }

    /// <summary>Always throws — this store is read-only.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public void SaveAllRaw(IDictionary<string, string> providers) =>
        throw new NotSupportedException("ConfigurationCredentialStore is read-only.");

    /// <summary>Always throws — this store is read-only.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public void SaveRaw(string providerId, string rawProviderJson) =>
        throw new NotSupportedException("ConfigurationCredentialStore is read-only.");

    /// <summary>
    /// Serialises an <see cref="IConfigurationSection"/> subtree as a JSON object
    /// string, picking sensible scalar types where possible.
    /// </summary>
    private static string SerializeSectionAsObject(IConfigurationSection section)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            WriteSectionAsJson(writer, section);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteSectionAsJson(Utf8JsonWriter writer, IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();

        // A section with no children is a leaf — write the value (or null).
        if (children.Count == 0)
        {
            WriteScalar(writer, section.Value);
            return;
        }

        // Treat purely numeric keys (0, 1, 2, ...) as an array — this is the
        // standard IConfiguration array convention used by env vars and JSON
        // sources alike.
        var allNumeric = children.All(c => int.TryParse(c.Key, out _));
        if (allNumeric)
        {
            writer.WriteStartArray();
            foreach (var c in children.OrderBy(c => int.Parse(c.Key)))
                WriteSectionAsJson(writer, c);
            writer.WriteEndArray();
            return;
        }

        writer.WriteStartObject();
        foreach (var child in children)
        {
            writer.WritePropertyName(child.Key);
            WriteSectionAsJson(writer, child);
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a configuration leaf value with type inference: bool → boolean,
    /// integer string → number, numeric string → number, otherwise string.
    /// Null values are written as JSON null.
    /// </summary>
    private static void WriteScalar(Utf8JsonWriter writer, string? value)
    {
        if (value is null)                                          { writer.WriteNullValue(); return; }
        if (bool.TryParse(value, out var b))                        { writer.WriteBooleanValue(b); return; }
        if (long.TryParse(value, out var l))                        { writer.WriteNumberValue(l); return; }
        if (double.TryParse(value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var d))                                         { writer.WriteNumberValue(d); return; }
        writer.WriteStringValue(value);
    }
}
