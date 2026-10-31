using Azure;
using MindAttic.Vault.Desktop.Profiles;

namespace MindAttic.Vault.Desktop.Azure;

/// <summary>
/// Turns Azure failures into the Profile contract's plain-words exceptions. Messages name roles, resources and
/// setting/key names only; never a value. The SDK's own exception rides along as the inner exception (its
/// message holds the service's error text and request URI, not request bodies).
/// </summary>
internal static class AzureErrors
{
    public const string AppConfigurationRole = "App Configuration Data Owner";
    public const string KeyVaultRole = "Key Vault Secrets Officer";

    public static ProfileAccessDeniedException AppConfigurationDenied(string store, RequestFailedException? inner = null) =>
        inner?.Status == 401
            ? new ProfileAccessDeniedException($"Azure didn't accept your sign-in for App Configuration store '{store}'. Sign in again.", inner)
            : new ProfileAccessDeniedException($"You need the '{AppConfigurationRole}' role on App Configuration store '{store}'.", inner);

    public static ProfileAccessDeniedException KeyVaultDenied(string vault, RequestFailedException? inner = null) =>
        inner?.Status == 401
            ? new ProfileAccessDeniedException($"Azure didn't accept your sign-in for Key Vault '{vault}'. Sign in again.", inner)
            : new ProfileAccessDeniedException($"You need the '{KeyVaultRole}' role on Key Vault '{vault}'.", inner);

    public static ConcurrencyConflictException Conflict(string store, string key) =>
        new($"'{key}' was changed in App Configuration store '{store}' since it was loaded. Reload to see the latest values, then save again.");

    /// <summary>Runs an App Configuration call, mapping 401/403 and 412.</summary>
    public static async Task<T> AppConfigurationAsync<T>(string store, string key, Func<Task<T>> call)
    {
        try { return await call().ConfigureAwait(false); }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403) { throw AppConfigurationDenied(store, ex); }
        catch (RequestFailedException ex) when (ex.Status == 412) { throw Conflict(store, key); }
    }

    public static async Task AppConfigurationAsync(string store, string key, Func<Task> call) =>
        await AppConfigurationAsync<bool>(store, key, async () => { await call().ConfigureAwait(false); return true; }).ConfigureAwait(false);

    /// <summary>Runs a Key Vault call, mapping 401/403.</summary>
    public static async Task<T> KeyVaultAsync<T>(string vault, Func<Task<T>> call)
    {
        try { return await call().ConfigureAwait(false); }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403) { throw KeyVaultDenied(vault, ex); }
    }

    /// <summary>An unlabeled key-value carrying <paramref name="etag"/>, for an If-Match write.</summary>
    public static global::Azure.Data.AppConfiguration.ConfigurationSetting Guarded(string key, string? value, ETag etag, string? contentType = null) =>
        global::Azure.Data.AppConfiguration.ConfigurationModelFactory.ConfigurationSetting(key, value, null, contentType, etag, null, null);

    /// <summary>
    /// Escapes an App Configuration key filter literal (<c>*</c>, <c>,</c> and <c>\</c> are special) so a prefix
    /// filter matches exactly that prefix.
    /// </summary>
    public static string EscapeFilter(string literal) =>
        literal.Replace("\\", "\\\\").Replace("*", "\\*").Replace(",", "\\,");
}
