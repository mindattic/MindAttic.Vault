using System.Xml.Linq;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

/// <summary>
/// Guards the Dashboard's dependency on the Vault library: it must build against the source in
/// this repo (a ProjectReference), never a pinned MindAttic.Vault package that drifts behind it.
/// </summary>
[TestFixture]
public class DashboardProjectReferenceTests
{
    private static XDocument LoadDashboardProject()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MindAttic.Vault.slnx")))
            dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "repo root (MindAttic.Vault.slnx) not found above the test directory");

        var path = Path.Combine(dir!.FullName, "MindAttic.Vault.Dashboard", "MindAttic.Vault.Dashboard.csproj");
        Assert.That(File.Exists(path), Is.True, $"Dashboard project not found at {path}");
        return XDocument.Load(path);
    }

    [Test]
    public void Dashboard_References_Vault_By_Project_Not_Package()
    {
        var project = LoadDashboardProject();

        var vaultPackages = project.Descendants("PackageReference")
            .Where(e => (string?)e.Attribute("Include") == "MindAttic.Vault")
            .ToList();
        var vaultProjects = project.Descendants("ProjectReference")
            .Where(e => ((string?)e.Attribute("Include") ?? "").Replace('\\', '/')
                .EndsWith("MindAttic.Vault/MindAttic.Vault.csproj", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.That(vaultPackages, Is.Empty, "Dashboard must not pin a MindAttic.Vault package version");
        Assert.That(vaultProjects, Has.Count.EqualTo(1), "Dashboard must reference ../MindAttic.Vault/MindAttic.Vault.csproj");
    }
}
