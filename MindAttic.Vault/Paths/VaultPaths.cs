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
    public static string RoamingRoot =>
        Environment.GetEnvironmentVariable(RoamingRootEnvVar)
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            MindAtticFolder);

    /// <summary>Local MindAttic root (defaults to <c>%LOCALAPPDATA%\MindAttic</c>).</summary>
    public static string LocalRoot =>
        Environment.GetEnvironmentVariable(LocalRootEnvVar)
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            MindAtticFolder);

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
    /// <c>"StreetSamurai"</c>). Does not create the directory.
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
