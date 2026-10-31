using MindAttic.Vault.Paths;
using MindAttic.Vault.Settings.Manifest;
using NUnit.Framework;

namespace MindAttic.Vault.Ecosystem.Tests;

/// <summary>One app as found on this machine: its folder, the root it was found under, and its manifest.</summary>
public sealed record DiscoveredApp(string Directory, string FoundScope, SettingsManifest Manifest)
{
    public string AppId => Manifest.AppId;
    public string FolderName => Path.GetFileName(Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    public string SettingsPath => Path.Combine(Directory, string.IsNullOrWhiteSpace(Manifest.SettingsFile) ? "settings.json" : Manifest.SettingsFile);
    public override string ToString() => AppId;
}

/// <summary>
/// The real roots of this machine, captured once before <see cref="EcosystemSetUp"/> points the Vault environment
/// variables at a guard folder, and the apps discovered under them. Everything here only reads.
/// </summary>
public static class Ecosystem
{
    /// <summary>Every app the LAMP migration (M.VALT-T35) moved into Vault; each must have a manifest on this machine.</summary>
    public static readonly IReadOnlyList<string> ExpectedApps = new[]
    {
        "Automata", "Capture", "Claudia", "Cursory", "Formicarium", "Global", "Ideas", "IdiotProof", "JellyFinPoster",
        "JobHunt", "MediaButler", "MindAttic.Browser", "MindAttic.Launcher", "MindAttic.Log.Reader", "OpenCredentials",
        "Orchestrator", "ProcessLogger", "Prose", "TaxRateCollector", "ThinkTank", "Tickets", "Tutor", "UnstuckTaskbar",
    };

    /// <summary>The time Vault stopped keeping <c>.bak</c> copies of key files (M.VALT-T61, 2d1605c).</summary>
    public static readonly DateTimeOffset NoKeyBackupsSince = DateTimeOffset.Parse("2026-10-31T11:07:16-05:00");

    private static readonly Lazy<IReadOnlyList<DiscoveredApp>> apps = new(DiscoverApps);

    /// <summary>The real roaming root (<c>%APPDATA%\MindAttic</c>) as resolved before any override in this run.</summary>
    public static string RealRoaming { get; } = VaultPaths.RoamingRoot;

    /// <summary>The real local root (<c>%LOCALAPPDATA%\MindAttic</c>) as resolved before any override in this run.</summary>
    public static string RealLocal { get; } = VaultPaths.LocalRoot;

    /// <summary>What <see cref="SettingsManifestFile.Discover"/> finds under the real roots.</summary>
    public static IReadOnlyList<DiscoveredApp> Apps => apps.Value;

    public static DiscoveredApp App(string appId) =>
        Apps.FirstOrDefault(a => string.Equals(a.AppId, appId, StringComparison.OrdinalIgnoreCase))
        ?? throw new AssertionException($"{appId} was not discovered.");

    /// <summary>One test case per discovered app, named after it.</summary>
    public static IEnumerable<TestCaseData> AppCases() =>
        Apps.Select(a => new TestCaseData(a.AppId).SetArgDisplayNames(a.AppId));

    /// <summary>One test case per discovered app that declares at least one credential.</summary>
    public static IEnumerable<TestCaseData> AppsWithCredentials() =>
        Apps.Where(a => a.Manifest.Credentials.Count > 0).Select(a => new TestCaseData(a.AppId).SetArgDisplayNames(a.AppId));

    private static IReadOnlyList<DiscoveredApp> DiscoverApps()
    {
        var roaming = Path.GetFullPath(RealRoaming).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return SettingsManifestFile.Discover(RealRoaming, RealLocal)
            .Select(d => new DiscoveredApp(d.Directory,
                Path.GetFullPath(d.Directory).StartsWith(roaming, StringComparison.OrdinalIgnoreCase) ? SettingsManifest.RoamingScope : SettingsManifest.LocalScope,
                d.Manifest))
            .ToList();
    }

    /// <summary>
    /// Runs a read with the Vault environment variables pointed at the real roots (for code that only resolves the
    /// default root, e.g. the installed exe's path), then restores the guard folder.
    /// </summary>
    public static T WithRealRoots<T>(Func<T> read)
    {
        var oldRoaming = Environment.GetEnvironmentVariable(VaultPaths.RoamingRootEnvVar);
        var oldLocal = Environment.GetEnvironmentVariable(VaultPaths.LocalRootEnvVar);
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, RealRoaming);
        Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, RealLocal);
        try { return read(); }
        finally
        {
            Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, oldRoaming);
            Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, oldLocal);
        }
    }

    /// <summary>Fails with every problem listed (names only, never values) when there are any.</summary>
    public static void AssertNone(IReadOnlyCollection<string> problems, string what)
    {
        if (problems.Count == 0) return;
        Assert.Fail($"{what}: {problems.Count} problem(s):{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", problems));
    }

    /// <summary>A finding that doesn't fail the test (shown in the test output).</summary>
    public static void Report(string line) => TestContext.Out.WriteLine("FINDING: " + line);
}
