using System.IO;
using Azure.Core;
using Azure.Identity;
using MindAttic.Vault.Paths;

namespace MindAttic.Vault.Desktop.Azure.Identity;

/// <summary>The signed-in Azure identity Desktop acts as. Never a stored secret: the user's own sign-in.</summary>
public interface IAzureSession
{
    /// <summary>True once the user has signed in on this machine (the account record is remembered).</summary>
    bool IsSignedIn { get; }

    /// <summary>The signed-in user's UPN (e.g. <c>ryan@contoso.com</c>), or null.</summary>
    string? UserPrincipalName { get; }

    /// <summary>The home tenant of the signed-in account, or null.</summary>
    string? TenantId { get; }

    /// <summary>The credential every Azure client uses. Silent while the cached token is good.</summary>
    TokenCredential Credential { get; }

    /// <summary>Opens the browser to sign in (once; the token cache is persisted, OS-encrypted).</summary>
    Task SignInAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets the remembered account (the next use signs in again).</summary>
    void SignOut();

    event EventHandler? Changed;
}

/// <summary>
/// <see cref="InteractiveBrowserCredential"/> with a persisted MSAL token cache
/// (<see cref="TokenCachePersistenceOptions"/>: DPAPI-encrypted on Windows) and a remembered
/// <see cref="AuthenticationRecord"/> (account name, tenant and ids; no token or secret) in
/// <c>%LOCALAPPDATA%\MindAttic\Vault.Desktop\azure-account.json</c>, so the user signs in once per machine.
/// </summary>
public sealed class InteractiveAzureSession : IAzureSession
{
    public const string AccountFileName = "azure-account.json";
    public const string TokenCacheName = "MindAttic.Vault.Desktop";
    public const string ManagementScope = "https://management.azure.com/.default";

    private readonly string accountFile;
    private readonly object gate = new();
    private AuthenticationRecord? record;
    private InteractiveBrowserCredential credential;

    public InteractiveAzureSession(string? accountDirectory = null)
    {
        accountFile = Path.Combine(accountDirectory ?? VaultPaths.LocalApp("Vault.Desktop"), AccountFileName);
        record = LoadRecord(accountFile);
        credential = Create(record);
    }

    public bool IsSignedIn { get { lock (gate) return record is not null; } }
    public string? UserPrincipalName { get { lock (gate) return record?.Username; } }
    public string? TenantId { get { lock (gate) return record?.TenantId; } }
    public TokenCredential Credential { get { lock (gate) return credential; } }

    public event EventHandler? Changed;

    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        var fresh = Create(null);
        var newRecord = await fresh.AuthenticateAsync(new TokenRequestContext(new[] { ManagementScope }), cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(accountFile)!);
        var temp = accountFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(temp))
                await newRecord.SerializeAsync(stream, cancellationToken).ConfigureAwait(false);
            File.Move(temp, accountFile, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
        lock (gate)
        {
            record = newRecord;
            credential = Create(newRecord);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SignOut()
    {
        lock (gate)
        {
            record = null;
            credential = Create(null);
        }
        try { if (File.Exists(accountFile)) File.Delete(accountFile); } catch { /* best effort */ }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static InteractiveBrowserCredential Create(AuthenticationRecord? account)
    {
        var options = new InteractiveBrowserCredentialOptions
        {
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = TokenCacheName },
            AuthenticationRecord = account,
            TenantId = account?.TenantId,
        };
        // Resources in another tenant the user can reach (guest access) still work.
        options.AdditionallyAllowedTenants.Add("*");
        return new InteractiveBrowserCredential(options);
    }

    private static AuthenticationRecord? LoadRecord(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return AuthenticationRecord.Deserialize(stream);
        }
        catch
        {
            // A corrupt record just means signing in again.
            return null;
        }
    }
}
