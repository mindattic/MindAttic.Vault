using MindAttic.Vault.Paths;
using MindAttic.Vault.Settings.Manifest;
using NUnit.Framework;

namespace MindAttic.Vault.Ecosystem.Tests;

/// <summary>
/// Points the Vault at a guard folder for the whole run (M.VALT-LAW-6): the real roots are captured first in
/// <see cref="Ecosystem"/> and passed explicitly to every read, so a write that falls back to the default root lands
/// in temp, never in the real AppData.
/// </summary>
[SetUpFixture]
public sealed class EcosystemSetUp
{
    private EcosystemSandbox? guard;

    [OneTimeSetUp]
    public void PointVaultAtGuardFolder()
    {
        // Capture the real roots before overriding them.
        _ = Ecosystem.RealRoaming;
        _ = Ecosystem.RealLocal;
        _ = Ecosystem.Apps;
        guard = new EcosystemSandbox(copyApps: false);
    }

    [OneTimeTearDown]
    public void Restore() => guard?.Dispose();
}

/// <summary>
/// Temp roaming and local roots holding a copy of every discovered app's <c>settings.manifest.json</c> and settings
/// file (never key files), with the Vault environment variables pointed at them until disposed.
/// </summary>
public sealed class EcosystemSandbox : IDisposable
{
    private readonly string? oldRoaming = Environment.GetEnvironmentVariable(VaultPaths.RoamingRootEnvVar);
    private readonly string? oldLocal = Environment.GetEnvironmentVariable(VaultPaths.LocalRootEnvVar);

    public EcosystemSandbox(bool copyApps = true, IEnumerable<DiscoveredApp>? only = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "vault-ecosystem-test-" + Guid.NewGuid().ToString("N"));
        Roaming = Directory.CreateDirectory(Path.Combine(Root, "roaming")).FullName;
        Local = Directory.CreateDirectory(Path.Combine(Root, "local")).FullName;
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, Roaming);
        Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, Local);
        if (copyApps)
            foreach (var app in only ?? Ecosystem.Apps) Copy(app);
    }

    public string Root { get; }
    public string Roaming { get; }
    public string Local { get; }

    /// <summary>The copy of <paramref name="app"/>'s folder.</summary>
    public string DirectoryOf(DiscoveredApp app) =>
        Path.Combine(app.FoundScope == SettingsManifest.LocalScope ? Local : Roaming, app.FolderName);

    public string SettingsPathOf(DiscoveredApp app) => Path.Combine(DirectoryOf(app), Path.GetFileName(app.SettingsPath));

    private void Copy(DiscoveredApp app)
    {
        var target = Directory.CreateDirectory(DirectoryOf(app)).FullName;
        File.Copy(Path.Combine(app.Directory, SettingsManifest.FileName), Path.Combine(target, SettingsManifest.FileName));
        if (File.Exists(app.SettingsPath)) File.Copy(app.SettingsPath, SettingsPathOf(app));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, oldRoaming);
        Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, oldLocal);
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
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
        TestContext.Out.WriteLine($"Could not delete the temp folder {Root}.");
    }
}
