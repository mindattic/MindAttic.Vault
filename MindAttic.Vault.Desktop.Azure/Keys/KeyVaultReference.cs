using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Data.AppConfiguration;

namespace MindAttic.Vault.Desktop.Azure.Keys;

/// <summary>
/// An App Configuration Key Vault reference: a key-value whose content type is
/// <see cref="ContentType"/> and whose value is <c>{"uri":"https://&lt;vault&gt;/secrets/&lt;name&gt;"}</c>. The App
/// Configuration provider in a deployed app resolves it to the secret's latest version (no version in the URI).
/// </summary>
public sealed record KeyVaultReference(Uri VaultUri, string SecretName, string? Version = null)
{
    public const string ContentType = "application/vnd.microsoft.appconfig.keyvaultref+json;charset=utf-8";

    /// <summary><c>https://host/secrets/name</c> (plus <c>/version</c> when pinned).</summary>
    public Uri SecretUri => new($"{VaultUri.GetLeftPart(UriPartial.Authority)}/secrets/{SecretName}{(Version is null ? "" : "/" + Version)}");

    /// <summary>The stored value: exactly <c>{"uri":"..."}</c>.</summary>
    public string ToJson() => new JsonObject { ["uri"] = SecretUri.ToString() }.ToJsonString();

    /// <summary>The key-value that stores this reference under <paramref name="key"/>.</summary>
    public ConfigurationSetting ToSetting(string key) => new(key, ToJson()) { ContentType = ContentType };

    public static bool IsReference(ConfigurationSetting setting) =>
        setting is SecretReferenceConfigurationSetting
        || (setting.ContentType is { } ct && ct.StartsWith("application/vnd.microsoft.appconfig.keyvaultref+json", StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses a reference setting; null when it isn't one or its URI isn't a Key Vault secret URI.</summary>
    public static KeyVaultReference? TryParse(ConfigurationSetting setting)
    {
        if (!IsReference(setting)) return null;
        Uri? uri = setting is SecretReferenceConfigurationSetting secretRef ? secretRef.SecretId : null;
        if (uri is null)
        {
            try
            {
                if (JsonNode.Parse(setting.Value ?? "") is JsonObject obj && obj["uri"]?.GetValue<string>() is { } text)
                    Uri.TryCreate(text, UriKind.Absolute, out uri);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                return null;
            }
        }
        return uri is null ? null : FromSecretUri(uri);
    }

    public static KeyVaultReference? FromSecretUri(Uri uri)
    {
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !string.Equals(segments[0], "secrets", StringComparison.OrdinalIgnoreCase)) return null;
        return new KeyVaultReference(new Uri(uri.GetLeftPart(UriPartial.Authority)), segments[1], segments.Length > 2 ? segments[2] : null);
    }
}
