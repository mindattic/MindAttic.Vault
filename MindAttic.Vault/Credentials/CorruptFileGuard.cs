namespace MindAttic.Vault.Credentials;

/// <summary>
/// Keeps a copy of a credential file that no longer parses before a write replaces it. The stores read
/// a malformed file as empty (never crash the host), so the next write would otherwise overwrite
/// whatever the user still has in it with only the one new entry.
/// </summary>
internal static class CorruptFileGuard
{
    /// <summary>
    /// When <paramref name="path"/> exists, is non-blank and <paramref name="parses"/> rejects it, copies it
    /// to <c>&lt;path&gt;.corrupt-&lt;UTC timestamp&gt;</c> and returns the copy's path; otherwise null.
    /// A file that can't be read is left to the caller's own read/replace (which fails loudly).
    /// A failed copy throws, so the write that would have overwritten the file doesn't happen.
    /// </summary>
    public static string? PreserveIfUnparseable(string path, Func<string, bool> parses)
    {
        string raw;
        try
        {
            if (!File.Exists(path)) return null;
            raw = File.ReadAllText(path);
        }
        catch { return null; }

        if (string.IsNullOrWhiteSpace(raw) || parses(raw)) return null;

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var dest = path + ".corrupt-" + stamp;
        if (File.Exists(dest)) dest += "-" + Guid.NewGuid().ToString("N")[..8];
        File.Copy(path, dest, overwrite: false);
        return dest;
    }
}
