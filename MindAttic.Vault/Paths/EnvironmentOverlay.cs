namespace MindAttic.Vault.Paths;

/// <summary>
/// Helpers for layering environment-variable overrides onto in-memory settings objects.
///
/// Replaces a pattern hand-rolled in every MindAttic app:
/// <code>
///   var v = Environment.GetEnvironmentVariable("MyKey");
///   if (!string.IsNullOrWhiteSpace(v)) target.MyKey = v;
/// </code>
/// </summary>
public static class EnvironmentOverlay
{
    /// <summary>
    /// If <paramref name="envVar"/> is set to a non-empty value, invokes
    /// <paramref name="apply"/> with the value. Otherwise no-op.
    /// </summary>
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
    public static void ApplyAll(IEnumerable<(string EnvVar, Action<string> Apply)> pairs)
    {
        if (pairs is null) return;
        foreach (var (envVar, apply) in pairs)
            Apply(envVar, apply);
    }
}
