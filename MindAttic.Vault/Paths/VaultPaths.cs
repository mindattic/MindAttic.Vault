namespace MindAttic.Vault.Paths;

/// <summary>
/// Central path resolver for everything MindAttic apps store on disk.
///
/// <para><b>Two roots:</b></para>
/// <list type="bullet">
///   <item><description>
///     <c>%APPDATA%\MindAttic\</c> — roaming, shared across MindAttic apps
///     (credentials, keyrings, GitHub tokens, etc).
///   </description></item>
///   <item><description>
///     <c>%LOCALAPPDATA%\MindAttic\&lt;app&gt;\</c> — per-machine, per-app data
///     (caches, evidence, run output).
///   </description></item>
/// </list>
///
/// <para>On non-Windows hosts these resolve to <c>~/.config/MindAttic</c> and
/// <c>~/.local/share/MindAttic/&lt;app&gt;</c> via the standard
/// <see cref="Environment.SpecialFolder"/> lookup.</para>
///
/// <para><b>Override roots</b> for tests / sandboxes via env vars:</para>
/// <list type="bullet">
///   <item><description><c>MINDATTIC_VAULT_ROAMING_ROOT</c> wins for <see cref="RoamingRoot"/>.</description></item>
///   <item><description><c>MINDATTIC_VAULT_LOCAL_ROOT</c> wins for <see cref="LocalRoot"/>.</description></item>
/// </list>
/// </summary>
public static class VaultPaths
{
    /// <summary>Environment variable that overrides <see cref="RoamingRoot"/>.</summary>
    public const string RoamingRootEnvVar = "MINDATTIC_VAULT_ROAMING_ROOT";

    /// <summary>Environment variable that overrides <see cref="LocalRoot"/>.</summary>
    public const string LocalRootEnvVar   = "MINDATTIC_VAULT_LOCAL_ROOT";

    /// <summary>The folder name appended to both roaming and local app-data roots.</summary>
    public const string MindAtticFolder   = "MindAttic";

    /// <summary>Roaming MindAttic root (defaults to <c>%APPDATA%\MindAttic</c>).</summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no <see cref="RoamingRootEnvVar"/> override is set and the OS
    /// returns an empty path for <see cref="Environment.SpecialFolder.ApplicationData"/>
    /// (e.g. some restricted Linux container contexts). Set the env var to recover.
    /// </exception>
    public static string RoamingRoot =>
        NonBlankEnv(RoamingRootEnvVar)
        ?? Path.Combine(ResolveSpecialFolder(Environment.SpecialFolder.ApplicationData, RoamingRootEnvVar), MindAtticFolder);

    /// <summary>Local MindAttic root (defaults to <c>%LOCALAPPDATA%\MindAttic</c>).</summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no <see cref="LocalRootEnvVar"/> override is set and the OS
    /// returns an empty path for <see cref="Environment.SpecialFolder.LocalApplicationData"/>.
    /// Set the env var to recover.
    /// </exception>
    public static string LocalRoot =>
        NonBlankEnv(LocalRootEnvVar)
        ?? Path.Combine(ResolveSpecialFolder(Environment.SpecialFolder.LocalApplicationData, LocalRootEnvVar), MindAtticFolder);

    // An override env var explicitly set to "" or whitespace (possible on non-Windows
    // hosts, where setting an empty value doesn't unset the variable) must be treated
    // as unset — otherwise the ?? fallback is bypassed and Path.Combine would turn the
    // blank root into a relative/bogus path instead of the intended app-data location.
    private static string? NonBlankEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    // Environment.GetFolderPath returns "" rather than throwing when the OS has
    // no concept of the requested folder (some restricted Linux/container hosts).
    // Surface that as a clear, actionable exception instead of silently combining
    // into a bogus root like "MindAttic" relative to the cwd.
    private static string ResolveSpecialFolder(Environment.SpecialFolder folder, string overrideEnvVar)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(path))
            throw new InvalidOperationException(
                $"Environment.GetFolderPath({folder}) returned an empty string on this host. " +
                $"Set the {overrideEnvVar} environment variable to an explicit directory to override.");
        return path;
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
    /// Local data directory for a given app (e.g. <c>"IdiotProof"</c>,
    /// <c>"Prose"</c>). Does not create the directory.
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
