using MindAttic.Vault.Desktop.Azure.Identity;
using MindAttic.Vault.Desktop.Azure.Tests.Fakes;
using Azure.Core;

namespace MindAttic.Vault.Desktop.Azure.Tests;

/// <summary>A GUID temp folder deleted on dispose (tests never touch the real %APPDATA%, M.VALT-LAW-6).</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VaultDesktopAzureTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>A signed-in (or not) session without a browser.</summary>
public sealed class FakeSession : IAzureSession
{
    public bool IsSignedIn { get; set; }
    public string? UserPrincipalName { get; set; }
    public string? TenantId { get; set; } = "00000000-0000-0000-0000-000000000001";
    public TokenCredential Credential { get; set; } = new FakeTokenCredential();
    public int SignIns;
    public Exception? SignInFailure { get; set; }

    public event EventHandler? Changed;

    public Task SignInAsync(CancellationToken cancellationToken = default)
    {
        SignIns++;
        if (SignInFailure is not null) throw SignInFailure;
        IsSignedIn = true;
        UserPrincipalName ??= "ryan@contoso.com";
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public void SignOut()
    {
        IsSignedIn = false;
        UserPrincipalName = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
