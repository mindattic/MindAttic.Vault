using MindAttic.Vault.Paths;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

/// <summary>
/// The cross-platform root resolution chain (VLT-A: Linux/macOS/iOS/Android/Windows).
/// <para>
/// Every environment dependency is injected, so each branch is reachable on any build agent — the
/// Linux-container case that used to abort the process is exercised here from a Windows dev box.
/// </para>
/// </summary>
[TestFixture]
public class VaultPathsResolutionTests
{
    private const string Roaming = VaultPaths.RoamingRootEnvVar;
    private const string Local = VaultPaths.LocalRootEnvVar;

    /// <summary>An environment with nothing in it — the shape that used to throw.</summary>
    private static Func<string, string?> Env(params (string Key, string? Value)[] entries)
    {
        var map = entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        return key => map.TryGetValue(key, out var v) ? v : null;
    }

    private static Func<Environment.SpecialFolder, string> NoSpecialFolder() => _ => "";

    private static Func<Environment.SpecialFolder, string> SpecialFolder(string path) => _ => path;

    private static VaultRootResolution Resolve(
        VaultPaths.VaultRootKind kind,
        Func<string, string?> env,
        Func<Environment.SpecialFolder, string> folders,
        VaultPaths.VaultPlatform platform,
        string appBase = "/app") =>
        VaultPaths.Resolve(kind, env, folders, () => platform, appBase);

    // --- 1. Explicit override -------------------------------------------------------------------

    // A public test signature cannot name an internal type, so the kind crosses as a bool.
    [TestCase(true, Roaming)]
    [TestCase(false, Local)]
    public void Override_WinsAndIsUsedVerbatim(bool roaming, string variable)
    {
        var kind = roaming ? VaultPaths.VaultRootKind.Roaming : VaultPaths.VaultRootKind.Local;
        var result = Resolve(kind, Env((variable, "/mnt/secrets")), SpecialFolder("/home/u/.config"),
            VaultPaths.VaultPlatform.Unix);

        Assert.Multiple(() =>
        {
            Assert.That(result.Source, Is.EqualTo(VaultRootSource.EnvironmentOverride));
            Assert.That(result.Path, Is.EqualTo("/mnt/secrets"), "no MindAttic suffix on an explicit root");
        });
    }

    [Test]
    public void BlankOverride_IsTreatedAsUnset()
    {
        // Exporting an empty value on Unix does not unset the variable; honouring it would combine
        // into a relative path off the working directory.
        var result = Resolve(VaultPaths.VaultRootKind.Roaming,
            Env((Roaming, "   "), ("HOME", "/home/u")), NoSpecialFolder(), VaultPaths.VaultPlatform.Unix);

        Assert.That(result.Source, Is.Not.EqualTo(VaultRootSource.EnvironmentOverride));
        Assert.That(Path.IsPathRooted(result.Path), Is.True);
    }

    // --- 2. SpecialFolder -----------------------------------------------------------------------

    [Test]
    public void SpecialFolder_IsPreferredWhenTheHostProvidesOne()
    {
        var result = Resolve(VaultPaths.VaultRootKind.Roaming, Env(("HOME", "/home/u")),
            SpecialFolder("/home/u/.config"), VaultPaths.VaultPlatform.Unix);

        Assert.Multiple(() =>
        {
            Assert.That(result.Source, Is.EqualTo(VaultRootSource.SpecialFolder));
            Assert.That(result.Path, Is.EqualTo(Path.Combine("/home/u/.config", "MindAttic")));
        });
    }

    [Test]
    public void SpecialFolder_ThatThrows_FallsThroughInsteadOfPropagating()
    {
        Func<Environment.SpecialFolder, string> hostile = _ => throw new PlatformNotSupportedException();

        var result = Resolve(VaultPaths.VaultRootKind.Roaming, Env(("HOME", "/home/u")), hostile,
            VaultPaths.VaultPlatform.Unix);

        Assert.That(result.Source, Is.EqualTo(VaultRootSource.PlatformConvention));
    }

    // --- 3. Platform conventions ----------------------------------------------------------------

    [Test]
    public void Windows_FallsBackToAppDataVariables()
    {
        var env = Env(("APPDATA", @"C:\Users\u\AppData\Roaming"), ("LOCALAPPDATA", @"C:\Users\u\AppData\Local"));

        var roaming = Resolve(VaultPaths.VaultRootKind.Roaming, env, NoSpecialFolder(), VaultPaths.VaultPlatform.Windows);
        var local = Resolve(VaultPaths.VaultRootKind.Local, env, NoSpecialFolder(), VaultPaths.VaultPlatform.Windows);

        Assert.Multiple(() =>
        {
            Assert.That(roaming.Source, Is.EqualTo(VaultRootSource.PlatformConvention));
            Assert.That(roaming.Path, Is.EqualTo(Path.Combine(@"C:\Users\u\AppData\Roaming", "MindAttic")));
            Assert.That(local.Path, Is.EqualTo(Path.Combine(@"C:\Users\u\AppData\Local", "MindAttic")));
        });
    }

    [Test]
    public void Linux_UsesXdgWhenSet()
    {
        var env = Env(("HOME", "/home/u"), ("XDG_CONFIG_HOME", "/cfg"), ("XDG_DATA_HOME", "/data"));

        var roaming = Resolve(VaultPaths.VaultRootKind.Roaming, env, NoSpecialFolder(), VaultPaths.VaultPlatform.Unix);
        var local = Resolve(VaultPaths.VaultRootKind.Local, env, NoSpecialFolder(), VaultPaths.VaultPlatform.Unix);

        Assert.Multiple(() =>
        {
            Assert.That(roaming.Path, Is.EqualTo(Path.Combine("/cfg", "MindAttic")));
            Assert.That(local.Path, Is.EqualTo(Path.Combine("/data", "MindAttic")));
        });
    }

    [Test]
    public void Linux_FallsBackToTheXdgDefaultsUnderHome()
    {
        var env = Env(("HOME", "/home/u"));

        var roaming = Resolve(VaultPaths.VaultRootKind.Roaming, env, NoSpecialFolder(), VaultPaths.VaultPlatform.Unix);
        var local = Resolve(VaultPaths.VaultRootKind.Local, env, NoSpecialFolder(), VaultPaths.VaultPlatform.Unix);

        Assert.Multiple(() =>
        {
            Assert.That(roaming.Path, Is.EqualTo(Path.Combine("/home/u", ".config", "MindAttic")));
            Assert.That(local.Path, Is.EqualTo(Path.Combine("/home/u", ".local", "share", "MindAttic")));
        });
    }

    [Test]
    public void Apple_UsesLibraryApplicationSupport()
    {
        var result = Resolve(VaultPaths.VaultRootKind.Roaming, Env(("HOME", "/Users/u")), NoSpecialFolder(),
            VaultPaths.VaultPlatform.MacCatalyst);

        Assert.That(result.Path,
            Is.EqualTo(Path.Combine("/Users/u", "Library", "Application Support", "MindAttic")));
    }

    // --- 4/5. Last resorts ----------------------------------------------------------------------

    [Test]
    public void WindowsWithoutAppData_StillFindsTheUserProfile()
    {
        var result = Resolve(VaultPaths.VaultRootKind.Roaming, Env(("USERPROFILE", @"C:\Users\u")),
            NoSpecialFolder(), VaultPaths.VaultPlatform.Windows);

        Assert.Multiple(() =>
        {
            Assert.That(result.Source, Is.EqualTo(VaultRootSource.HomeDirectory));
            Assert.That(result.Path, Is.EqualTo(Path.Combine(@"C:\Users\u", ".mindattic", "config")));
        });
    }

    /// <summary>
    /// The regression this whole chain exists for: a Linux App Service worker with no user profile
    /// and no HOME. This used to throw from inside ConfigurationBuilder, aborting the process during
    /// host construction — SIGABRT before any application code ran.
    /// </summary>
    [Test]
    public void NoUserProfileAtAll_ResolvesBesideTheBinariesInsteadOfThrowing()
    {
        VaultRootResolution roaming = default;
        VaultRootResolution local = default;

        Assert.DoesNotThrow(() =>
        {
            roaming = Resolve(VaultPaths.VaultRootKind.Roaming, Env(), NoSpecialFolder(),
                VaultPaths.VaultPlatform.Unix, "/home/site/wwwroot");
            local = Resolve(VaultPaths.VaultRootKind.Local, Env(), NoSpecialFolder(),
                VaultPaths.VaultPlatform.Unix, "/home/site/wwwroot");
        });

        Assert.Multiple(() =>
        {
            Assert.That(roaming.Source, Is.EqualTo(VaultRootSource.ApplicationBase));
            Assert.That(roaming.Path, Is.EqualTo(Path.Combine("/home/site/wwwroot", ".mindattic", "config")));
            Assert.That(local.Path, Is.EqualTo(Path.Combine("/home/site/wwwroot", ".mindattic", "data")));
        });
    }

    [Test]
    public void EveryBranchReturnsARootedNonBlankPath()
    {
        var environments = new (string Name, Func<string, string?> Env, Func<Environment.SpecialFolder, string> Folders)[]
        {
            ("override",      Env((Roaming, "/mnt/x"), (Local, "/mnt/y")), NoSpecialFolder()),
            ("special",       Env(),                                       SpecialFolder("/home/u/.config")),
            ("xdg",           Env(("XDG_CONFIG_HOME", "/cfg"), ("XDG_DATA_HOME", "/d"), ("HOME", "/home/u")), NoSpecialFolder()),
            ("home",          Env(("HOME", "/home/u")),                    NoSpecialFolder()),
            ("nothing",       Env(),                                       NoSpecialFolder()),
        };

        foreach (var (name, env, folders) in environments)
        {
            foreach (var kind in new[] { VaultPaths.VaultRootKind.Roaming, VaultPaths.VaultRootKind.Local })
            {
                foreach (var platform in Enum.GetValues<VaultPaths.VaultPlatform>())
                {
                    var result = Resolve(kind, env, folders, platform, "/app");
                    Assert.That(result.Path, Is.Not.Null.And.Not.Empty, $"{name}/{kind}/{platform}");
                    Assert.That(Path.IsPathRooted(result.Path), Is.True, $"{name}/{kind}/{platform} -> {result.Path}");
                }
            }
        }
    }

    // --- The public surface still behaves on this host -------------------------------------------

    [Test]
    public void PublicRootsResolveOnThisHostAndAreReportable()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VaultPaths.RoamingRoot, Is.Not.Empty);
            Assert.That(VaultPaths.LocalRoot, Is.Not.Empty);
            Assert.That(VaultPaths.Describe(), Does.Contain("roaming =").And.Contain("local   ="));
        });
    }
}
