namespace MindAttic.Vault.Paths;

/// <summary>
/// Central path resolver for everything MindAttic apps store on disk.
///
/// Two roots:
///   <c>%APPDATA%\MindAttic\</c>          — roaming, shared across MindAttic apps
///                                          (credentials, keyrings, GitHub tokens, etc).
///   <c>%LOCALAPPDATA%\MindAttic\&lt;app&gt;\</c> — per-machine, per-app data
///                                          (caches, evidence, run output).
///
/// On non-Windows hosts these resolve to <c>~/.config/MindAttic</c> and
/// <c>~/.local/share/MindAttic/&lt;app&gt;</c> via the standard SpecialFolder lookup.
///
/// Override roots for tests / sandboxes via env vars:
///   <c>MINDATTIC_VAULT_ROAMING_ROOT</c>  → wins for <see cref="RoamingRoot"/>
///   <c>MINDATTIC_VAULT_LOCAL_ROOT</c>    → wins for <see cref="LocalRoot"/>
/// </summary>
public static class VaultPaths
{
    public const string RoamingRootEnvVar = "MINDATTIC_VAULT_ROAMING_ROOT";
    public const string LocalRootEnvVar   = "MINDATTIC_VAULT_LOCAL_ROOT";
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

    /// <summary>Roaming bucket directory (e.g. "LLM", "Brokers", "GitHub"). Does not create it.</summary>
    public static string RoamingBucket(string bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket))
            throw new ArgumentException("Bucket name is required.", nameof(bucket));
        return Path.Combine(RoamingRoot, bucket);
    }

    /// <summary>Local data directory for a given app (e.g. "IdiotProof", "StreetSamurai"). Does not create it.</summary>
    public static string LocalApp(string app)
    {
        if (string.IsNullOrWhiteSpace(app))
            throw new ArgumentException("App name is required.", nameof(app));
        return Path.Combine(LocalRoot, app);
    }

    /// <summary>Ensures a directory exists, returning the supplied path for fluent chaining.</summary>
    public static string Ensure(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));
        Directory.CreateDirectory(path);
        return path;
    }
}
