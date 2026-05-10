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
/// </summary>
public sealed class ConfigurationCredentialStore : ICredentialStore
{
    private readonly IConfiguration configuration;
    private readonly string bucketSection;

    /// <summary>
    /// Construct a store reading from <paramref name="configuration"/> at
    /// <paramref name="bucketSection"/> (e.g. <see cref="VaultConfigurationKeys.LlmSection"/>).
    /// </summary>
    public ConfigurationCredentialStore(IConfiguration configuration, string bucketSection)
    {
        this.configuration  = configuration ?? throw new ArgumentNullException(nameof(configuration));
        if (string.IsNullOrWhiteSpace(bucketSection))
            throw new ArgumentException("Bucket section is required.", nameof(bucketSection));
        this.bucketSection = bucketSection;
    }

    /// <summary>Convenience: a store rooted at <c>MindAttic:Vault:LLM</c>.</summary>
    public static ConfigurationCredentialStore ForLlm(IConfiguration configuration) =>
        new(configuration, VaultConfigurationKeys.LlmSection);

    /// <summary>Convenience: a store rooted at <c>MindAttic:Vault:Brokers</c>.</summary>
    public static ConfigurationCredentialStore ForBrokers(IConfiguration configuration) =>
        new(configuration, VaultConfigurationKeys.BrokersSection);

    /// <summary>The configuration section path this store is rooted at.</summary>
    public string BucketSection => bucketSection;

    public string Directory          => "(configuration)";
    public string ProvidersFilePath  => "(configuration:" + bucketSection + ")";

    public bool ProvidersFileExists() => configuration.GetSection(bucketSection).GetChildren().Any();

    public string? GetKey(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;
        var path = VaultConfigurationKeys.ProviderApiKeyPath(bucketSection, providerId);
        var raw = configuration[path];
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    public void SetKey(string providerId, string apiKey) =>
        throw new NotSupportedException(
            "ConfigurationCredentialStore is read-only. Wrap it with CompositeCredentialStore over a writable CredentialStore, or write to the underlying IConfiguration source (User Secrets / App Service Application Settings / Key Vault) directly.");

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

    public List<string> ListProviders() => LoadAll().Keys.ToList();

    public Dictionary<string, string> LoadAllRaw()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = configuration.GetSection(bucketSection);
        foreach (var providerSection in section.GetChildren())
        {
            var providerId = providerSection.Key;
            if (string.IsNullOrWhiteSpace(providerId)) continue;
            result[providerId] = SerializeSectionAsObject(providerSection);
        }
        return result;
    }

    public void SaveAllRaw(IDictionary<string, string> providers) =>
        throw new NotSupportedException("ConfigurationCredentialStore is read-only.");

    public void SaveRaw(string providerId, string rawProviderJson) =>
        throw new NotSupportedException("ConfigurationCredentialStore is read-only.");

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

        // Treat purely numeric keys (0, 1, 2, ...) as an array.
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
