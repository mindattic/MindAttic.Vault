namespace MindAttic.Vault.Paths;

/// <summary>
/// Helpers for layering environment-variable overrides onto in-memory settings objects.
///
/// <para>Replaces a pattern hand-rolled in every MindAttic app:</para>
/// <code>
///   var v = Environment.GetEnvironmentVariable("MyKey");
///   if (!string.IsNullOrWhiteSpace(v)) target.MyKey = v;
/// </code>
///
/// <para>For cloud-native deployments prefer using <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
/// with <c>AddEnvironmentVariables()</c>; this helper is the lightweight
/// equivalent for plain console / desktop apps that don't host a configuration
/// pipeline.</para>
/// </summary>
public static class EnvironmentOverlay
{
    /// <summary>
    /// If <paramref name="envVar"/> is set to a non-empty value, invokes
    /// <paramref name="apply"/> with the value. Otherwise no-op.
    /// </summary>
    /// <param name="envVar">
    /// Environment variable name. Null/whitespace is a no-op (so callers can
    /// pass conditional/disabled variables without guarding).
    /// </param>
    /// <param name="apply">
    /// The action to invoke with the resolved value. Null is a no-op.
    /// </param>
    public static void Apply(string envVar, Action<string> apply)
    {
        if (string.IsNullOrWhiteSpace(envVar)) return;
        if (apply is null) return;

        var value = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(value))
            apply(value);
    }

    /// <summary>
    /// Bulk overlay: each pair maps an env var name to a setter on the target object.
    /// </summary>
    /// <param name="pairs">
    /// (envVar, apply) pairs. Null is a no-op. Each entry is processed
    /// independently; a missing variable does not skip later entries.
    /// </param>
    public static void ApplyAll(IEnumerable<(string EnvVar, Action<string> Apply)> pairs)
    {
        if (pairs is null) return;
        foreach (var (envVar, apply) in pairs)
            Apply(envVar, apply);
    }
}
