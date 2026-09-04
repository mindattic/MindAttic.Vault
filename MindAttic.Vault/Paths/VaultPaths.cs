namespace MindAttic.Vault.Paths;

/// <summary>Where a resolved vault root came from. Useful when a host resolves somewhere surprising.</summary>
public enum VaultRootSource
{
    /// <summary>An explicit <c>MINDATTIC_VAULT_*_ROOT</c> environment variable.</summary>
    EnvironmentOverride,

    /// <summary><see cref="Environment.SpecialFolder"/> — the normal answer on Windows, macOS, iOS and Android.</summary>
    SpecialFolder,

    /// <summary>The platform's own convention (<c>%APPDATA%</c>, XDG, <c>~/Library/Application Support</c>).</summary>
    PlatformConvention,

    /// <summary><c>$HOME/.mindattic/…</c>, when the platform convention could not be determined.</summary>
    HomeDirectory,

    /// <summary>Beside the application binaries. The last resort, when the host exposes no user profile at all.</summary>
    ApplicationBase,
}

/// <summary>A resolved vault root and the rule that produced it.</summary>
/// <param name="Path">Absolute directory path. Never null, never blank.</param>
/// <param name="Source">Which rule in the chain won.</param>
public readonly record struct VaultRootResolution(string Path, VaultRootSource Source);

/// <summary>
/// Central path resolver for everything MindAttic apps store on disk.
///
/// <para><b>Two roots:</b></para>
/// <list type="bullet">
///   <item><description>
///     <b>Roaming</b> — shared across MindAttic apps (credentials, keyrings, tokens).
///     <c>%APPDATA%\MindAttic\</c> on Windows, <c>~/.config/MindAttic</c> on Linux,
///     <c>~/Library/Application Support/MindAttic</c> on macOS.
///   </description></item>
///   <item><description>
///     <b>Local</b> — per-machine, per-app data (caches, evidence, run output).
///     <c>%LOCALAPPDATA%\MindAttic\&lt;app&gt;\</c> on Windows,
///     <c>~/.local/share/MindAttic/&lt;app&gt;</c> on Linux.
///   </description></item>
/// </list>
///
/// <para><b>Resolution never fails.</b> <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
/// returns an empty string — it does not throw — on hosts with no user profile, which includes a
/// Linux App Service container where <c>HOME</c> is not exported to the worker process. Resolution
/// used to throw there, and because the vault is wired into the <c>IConfiguration</c> chain that
/// happens during host construction: the process aborts before a single line of app code runs, with
/// a stack trace that points at configuration rather than at the missing folder.
/// So the chain falls through instead, in this order:</para>
/// <list type="number">
///   <item><description><c>MINDATTIC_VAULT_ROAMING_ROOT</c> / <c>MINDATTIC_VAULT_LOCAL_ROOT</c>, used verbatim.</description></item>
///   <item><description>The matching <see cref="Environment.SpecialFolder"/>, when the host gives one.</description></item>
///   <item><description>The platform convention read straight from the environment.</description></item>
///   <item><description><c>$HOME/.mindattic/{config,data}</c>.</description></item>
///   <item><description><c>{AppContext.BaseDirectory}/.mindattic/{config,data}</c>.</description></item>
/// </list>
///
/// <para>Steps 3-5 only ever run where step 2 previously threw, so no host that already worked
/// resolves anywhere new. Use <see cref="ResolveRoaming"/> / <see cref="ResolveLocal"/> to see which
/// rule won.</para>
/// </summary>
public static class VaultPaths
{
    /// <summary>Environment variable that overrides <see cref="RoamingRoot"/>.</summary>
    public const string RoamingRootEnvVar = "MINDATTIC_VAULT_ROAMING_ROOT";

    /// <summary>Environment variable that overrides <see cref="LocalRoot"/>.</summary>
    public const string LocalRootEnvVar = "MINDATTIC_VAULT_LOCAL_ROOT";

    /// <summary>The folder name appended to both roaming and local app-data roots.</summary>
    public const string MindAtticFolder = "MindAttic";

    /// <summary>Directory used under <c>$HOME</c> / the app base when no convention is available.</summary>
    internal const string FallbackFolder = ".mindattic";

    /// <summary>Roaming MindAttic root. See <see cref="VaultPaths"/> for the resolution order.</summary>
    public static string RoamingRoot => ResolveRoaming().Path;

    /// <summary>Local MindAttic root. See <see cref="VaultPaths"/> for the resolution order.</summary>
    public static string LocalRoot => ResolveLocal().Path;

    /// <summary>Resolves the roaming root and reports which rule produced it.</summary>
    public static VaultRootResolution ResolveRoaming() =>
        Resolve(VaultRootKind.Roaming, Environment.GetEnvironmentVariable, Environment.GetFolderPath,
            CurrentPlatform, AppContext.BaseDirectory);

    /// <summary>Resolves the local root and reports which rule produced it.</summary>
    public static VaultRootResolution ResolveLocal() =>
        Resolve(VaultRootKind.Local, Environment.GetEnvironmentVariable, Environment.GetFolderPath,
            CurrentPlatform, AppContext.BaseDirectory);

    /// <summary>One line per root, naming the path and the rule that chose it. For startup diagnostics.</summary>
    public static string Describe()
    {
        var roaming = ResolveRoaming();
        var local = ResolveLocal();
        return $"roaming = {roaming.Path} ({roaming.Source}){Environment.NewLine}"
             + $"local   = {local.Path} ({local.Source})";
    }

    internal enum VaultRootKind { Roaming, Local }

    internal enum VaultPlatform { Windows, MacCatalyst, Unix }

    internal static VaultPlatform CurrentPlatform()
    {
        if (OperatingSystem.IsWindows()) return VaultPlatform.Windows;
        // iOS and Mac Catalyst share the Apple layout; Android reports as neither and takes the Unix
        // branch, which is correct -- its SpecialFolder lookup answers first in practice anyway.
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsIOS())
            return VaultPlatform.MacCatalyst;
        return VaultPlatform.Unix;
    }

    /// <summary>
    /// The resolution chain, with every environment dependency injected so each branch is reachable
    /// from a test without mutating process-wide state.
    /// </summary>
    internal static VaultRootResolution Resolve(
        VaultRootKind kind,
        Func<string, string?> getEnvironmentVariable,
        Func<Environment.SpecialFolder, string> getFolderPath,
        Func<VaultPlatform> getPlatform,
        string applicationBase)
    {
        var roaming = kind == VaultRootKind.Roaming;

        // 1. Explicit override, used verbatim -- no MindAttic suffix, so a caller can point at an
        //    exact directory (tests, sandboxes, a container with a mounted secrets volume).
        var overrideRoot = NonBlank(getEnvironmentVariable(roaming ? RoamingRootEnvVar : LocalRootEnvVar));
        if (overrideRoot != null)
            return new VaultRootResolution(overrideRoot, VaultRootSource.EnvironmentOverride);

        // 2. What the framework says, when the host has a user profile.
        var special = NonBlank(Safe(getFolderPath, roaming
            ? Environment.SpecialFolder.ApplicationData
            : Environment.SpecialFolder.LocalApplicationData));
        if (special != null)
            return new VaultRootResolution(Path.Combine(special, MindAtticFolder), VaultRootSource.SpecialFolder);

        // 3. The platform's own convention, read from the environment directly.
        var convention = PlatformConvention(roaming, getEnvironmentVariable, getPlatform());
        if (convention != null)
            return new VaultRootResolution(Path.Combine(convention, MindAtticFolder), VaultRootSource.PlatformConvention);

        // 4. A home directory with no recognised convention.
        var home = NonBlank(getEnvironmentVariable("HOME")) ?? NonBlank(getEnvironmentVariable("USERPROFILE"));
        if (home != null)
            return new VaultRootResolution(Path.Combine(home, FallbackFolder, Leaf(roaming)), VaultRootSource.HomeDirectory);

        // 5. No user profile at all. Beside the binaries, so the process still starts and the vault
        //    simply finds no files -- which is the correct outcome in production, where secrets
        //    arrive as environment variables or Key Vault references rather than as files on disk.
        return new VaultRootResolution(
            Path.Combine(applicationBase, FallbackFolder, Leaf(roaming)), VaultRootSource.ApplicationBase);
    }

    private static string Leaf(bool roaming) => roaming ? "config" : "data";

    private static string? PlatformConvention(
        bool roaming, Func<string, string?> getEnvironmentVariable, VaultPlatform platform)
    {
        switch (platform)
        {
            case VaultPlatform.Windows:
                return NonBlank(getEnvironmentVariable(roaming ? "APPDATA" : "LOCALAPPDATA"));

            case VaultPlatform.MacCatalyst:
            {
                var home = NonBlank(getEnvironmentVariable("HOME"));
                return home == null ? null : Path.Combine(home, "Library", "Application Support");
            }

            default:
            {
                // XDG Base Directory: config for roaming-equivalent state, data for local.
                var xdg = NonBlank(getEnvironmentVariable(roaming ? "XDG_CONFIG_HOME" : "XDG_DATA_HOME"));
                if (xdg != null) return xdg;

                var home = NonBlank(getEnvironmentVariable("HOME"));
                if (home == null) return null;

                return roaming
                    ? Path.Combine(home, ".config")
                    : Path.Combine(home, ".local", "share");
            }
        }
    }

    // An override env var explicitly set to "" or whitespace (possible on non-Windows hosts, where
    // setting an empty value doesn't unset the variable) must be treated as unset -- otherwise the
    // fallback is bypassed and Path.Combine turns the blank root into a relative path off the cwd.
    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // GetFolderPath returns "" rather than throwing on hosts with no such folder, but a sandboxed
    // platform can still throw on the underlying lookup. Either way the chain should continue.
    private static string? Safe(Func<Environment.SpecialFolder, string> getFolderPath, Environment.SpecialFolder folder)
    {
        try { return getFolderPath(folder); }
        catch { return null; }
    }

    /// <summary>
    /// Roaming bucket directory (e.g. <c>"LLM"</c>, <c>"Brokers"</c>, <c>"GitHub"</c>).
    /// Does not create the directory.
    /// </summary>
    /// <param name="bucket">Bucket folder name. Required.</param>
    /// <returns>Absolute path to the bucket directory under <see cref="RoamingRoot"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="bucket"/> is null/whitespace.</exception>
    public static string RoamingBucket(string bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket))
            throw new ArgumentException("Bucket name is required.", nameof(bucket));
        return Path.Combine(RoamingRoot, bucket);
    }

    /// <summary>
    /// Local data directory for a given app (e.g. <c>"IdiotProof"</c>, <c>"Prose"</c>).
    /// Does not create the directory.
    /// </summary>
    /// <param name="app">App folder name. Required.</param>
    /// <returns>Absolute path to the app directory under <see cref="LocalRoot"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="app"/> is null/whitespace.</exception>
    public static string LocalApp(string app)
    {
        if (string.IsNullOrWhiteSpace(app))
            throw new ArgumentException("App name is required.", nameof(app));
        return Path.Combine(LocalRoot, app);
    }

    /// <summary>Ensures a directory exists, returning the supplied path for fluent chaining.</summary>
    /// <param name="path">Directory path. Required.</param>
    /// <returns>The same <paramref name="path"/>, after the directory has been created if needed.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is null/whitespace.</exception>
    public static string Ensure(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));
        Directory.CreateDirectory(path);
        return path;
    }
}
