using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MindAttic.Vault.Credentials;

namespace MindAttic.Vault.Desktop.Azure.Keys;

/// <summary>
/// The one function that names a named key's Key Vault secret. Key Vault secret names allow only ASCII letters,
/// digits and dashes (1–127 characters), so a readable key name (<c>2026-10-31 IdiotProof</c>, any unicode, up to
/// 100 characters) can't be used directly. The generated name is <c>llm-{provider}-{slug}-{6 hex}</c>:
/// <list type="bullet">
/// <item><c>{provider}</c> and <c>{slug}</c> are the canonical provider id and the key name folded to lowercase
/// ASCII (accents dropped, every other run of characters becomes one dash), each length-capped; an empty slug is <c>key</c>.</item>
/// <item><c>{6 hex}</c> is the start of SHA-256 over the provider, the NFC-normalized lowercase name (key names are unique
/// case-insensitively) and the attempt number, so the name is stable for a key and two names that slug the same differ.</item>
/// <item><paramref name="attempt"/> &gt; 0 picks another name when the first is already taken (e.g. by a renamed or
/// soft-deleted key's secret).</item>
/// </list>
/// The name is generated once, when the key is added; renaming the key keeps its secret (only the App Configuration
/// key and the secret's <c>name</c> tag change), so nothing ever recomputes it from the current name.
/// </summary>
public static class SecretNames
{
    public const string Prefix = "llm";
    public const int MaxLength = 127;
    public const int MaxProviderSlug = 24;
    public const int MaxNameSlug = 60;
    public const int HashLength = 6;

    public static string Generate(string providerId, string keyName, int attempt = 0)
    {
        if (string.IsNullOrWhiteSpace(keyName)) throw new ArgumentException("Key name is required.", nameof(keyName));
        if (attempt < 0) throw new ArgumentOutOfRangeException(nameof(attempt));
        var provider = ProviderCatalog.Normalize(providerId);
        var name = keyName.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();

        var providerSlug = Slug(provider, MaxProviderSlug, "provider");
        var nameSlug = Slug(name, MaxNameSlug, "key");
        var hashInput = $"{provider}\n{name}\n{attempt.ToString(CultureInfo.InvariantCulture)}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)))[..HashLength];

        var result = $"{Prefix}-{providerSlug}-{nameSlug}-{hash}";
        return result.Length <= MaxLength ? result : result[..MaxLength];
    }

    /// <summary>True when <paramref name="name"/> is a valid Key Vault secret name.</summary>
    public static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxLength && name.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-');

    /// <summary>Lowercase ASCII letters/digits with single dashes, trimmed of dashes, at most <paramref name="max"/> long.</summary>
    internal static string Slug(string text, int max, string fallback)
    {
        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        var dash = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var lower = char.ToLowerInvariant(c);
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(lower);
                dash = false;
            }
            else if (!dash && sb.Length > 0)
            {
                sb.Append('-');
                dash = true;
            }
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > max) slug = slug[..max].TrimEnd('-');
        return slug.Length == 0 ? fallback : slug;
    }
}
