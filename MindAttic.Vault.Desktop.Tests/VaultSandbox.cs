using MindAttic.Vault.Paths;

namespace MindAttic.Vault.Desktop.Tests;

/// <summary>
/// Temp roaming and local roots for one test, set through <see cref="VaultPaths.RoamingRootEnvVar"/> and
/// <see cref="VaultPaths.LocalRootEnvVar"/> so nothing touches the real %APPDATA% (M.VALT-LAW-6). Restores the
/// previous values and deletes the folders on dispose.
/// </summary>
public sealed class VaultSandbox : IDisposable
{
    private readonly string? oldRoaming = Environment.GetEnvironmentVariable(VaultPaths.RoamingRootEnvVar);
    private readonly string? oldLocal = Environment.GetEnvironmentVariable(VaultPaths.LocalRootEnvVar);

    public VaultSandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "vault-desktop-test-" + Guid.NewGuid().ToString("N"));
        Roaming = Directory.CreateDirectory(Path.Combine(Root, "roaming")).FullName;
        Local = Directory.CreateDirectory(Path.Combine(Root, "local")).FullName;
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, Roaming);
        Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, Local);
    }

    public string Root { get; }
    public string Roaming { get; }
    public string Local { get; }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, oldRoaming);
        Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, oldLocal);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}
