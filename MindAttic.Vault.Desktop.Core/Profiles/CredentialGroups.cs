using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Paths;

namespace MindAttic.Vault.Desktop.Profiles;

/// <summary>How a credential group's file is laid out.</summary>
public enum CredentialGroupShape
{
    /// <summary><c>providers.json</c>: <c>{ entry: { field: value } }</c>, plus any top-level fields (e.g. Notifications' <c>to</c>).</summary>
    Entries,

    /// <summary><c>tokens.json</c>: a flat <c>{ name: "token" }</c> map; every value is a secret.</summary>
    Tokens,

    /// <summary>One object of fields (e.g. <c>Ftp\ftp.json</c>).</summary>
    Object,
}

/// <summary>
/// One structured credential bucket several apps share (M.VALT-T62). The bucket and file stay exactly where they are:
/// apps read them as <c>MindAttic:Vault:{Bucket}:...</c>, so they are edited in place, never renamed or moved.
/// </summary>
public sealed record CredentialGroupInfo(string Bucket, string FileName, CredentialGroupShape Shape, string DisplayName, string? Description = null)
{
    /// <summary>Stable id: <c>{Bucket}/{FileName}</c>.</summary>
    public string Id => Bucket + "/" + FileName;

    /// <summary>The configuration path apps read it under: <c>MindAttic:Vault:{Bucket}</c>.</summary>
    public string FullName => VaultConfigurationKeys.VaultSection + ":" + Bucket;

    /// <summary>The shape a known group starts from when its file doesn't exist yet (null: empty).</summary>
    public JsonObject? Template { get; init; }
}

/// <summary>A group's values as loaded, with the backend's version for concurrency.</summary>
public sealed record CredentialGroupDocument(CredentialGroupInfo Group, JsonObject Values, string Version);

/// <summary>
/// Loads and saves the structured credential groups in a Profile. Values travel as JSON documents so unknown fields
/// and JSON types survive a Save untouched.
/// </summary>
public interface ICredentialGroupBackend
{
    /// <summary>Why this Profile can't edit credential groups (shown instead of the groups), or null when it can.</summary>
    string? ReadOnlyReason { get; }

    /// <summary>The known groups plus any other bucket on disk that holds a <c>providers.json</c> or <c>tokens.json</c>.</summary>
    Task<IReadOnlyList<CredentialGroupInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The group's current values; a group with no file yet loads its template (or an empty object).</summary>
    Task<CredentialGroupDocument> LoadAsync(CredentialGroupInfo group, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="values"/> as the group's whole document, atomically. Throws
    /// <see cref="ConcurrencyConflictException"/> when the stored values changed since <paramref name="version"/>.
    /// Returns the new version.
    /// </summary>
    Task<string> SaveAsync(CredentialGroupInfo group, JsonObject values, string version, CancellationToken cancellationToken = default);
}

/// <summary>The groups every Profile knows about, and which field names hold secrets.</summary>
public static class CredentialGroupCatalog
{
    /// <summary>Buckets edited elsewhere (LLM keys on the Keys tab; Vault's own assignments) or not credentials.</summary>
    public static readonly IReadOnlySet<string> NotGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        LlmCredentialStore.Bucket, KeyAssignmentStore.Bucket, "Vault.Desktop", "Logging",
    };

    /// <summary>The structured groups apps share today, in the order the tab shows them.</summary>
    public static IReadOnlyList<CredentialGroupInfo> Known { get; } = new[]
    {
        new CredentialGroupInfo("Notifications", CredentialStore.ProvidersJsonFile, CredentialGroupShape.Entries, "Notifications",
            "Email (SMTP) and SMS (Twilio) for Psst, IdiotProof, MindAttic.Authentication, Ideas, Prose and Tutor; to / toEmail are the default recipients.")
        {
            Template = new JsonObject
            {
                ["email"] = new JsonObject { ["smtpHost"] = "", ["smtpPort"] = "", ["username"] = "", ["password"] = "", ["from"] = "" },
                ["twilio"] = new JsonObject { ["accountSid"] = "", ["authToken"] = "", ["from"] = "", ["brandId"] = "" },
                ["to"] = "",
                ["toEmail"] = "",
            },
        },
        new CredentialGroupInfo("Subtitles", CredentialStore.ProvidersJsonFile, CredentialGroupShape.Entries, "Subtitles",
            "Subtitle-site logins for MediaButler (OpenSubtitles user and password).")
        {
            Template = new JsonObject { ["OpenSubtitles"] = new JsonObject { ["user"] = "", ["password"] = "" } },
        },
        new CredentialGroupInfo("AudioStore", CredentialStore.ProvidersJsonFile, CredentialGroupShape.Entries, "Audio store",
            "Where generated audio is kept: provider, container and connection string.")
        {
            Template = new JsonObject { ["provider"] = "", ["container"] = "", ["connectionString"] = "" },
        },
        new CredentialGroupInfo(BrokerCredentialStore.Bucket, CredentialStore.ProvidersJsonFile, CredentialGroupShape.Entries, "Brokers",
            "Broker accounts (alpaca-paper, alpaca-live): type, API key, secret and base URL. A broker's named key list is on the Keys tab."),
        new CredentialGroupInfo(FtpCredentialStore.Bucket, FtpCredentialStore.FileName, CredentialGroupShape.Object, "FTP",
            "The FTP / FTPS deploy target (MindAttic.Deploy).")
        {
            Template = new JsonObject { ["host"] = "", ["port"] = 21, ["user"] = "", ["password"] = "", ["secure"] = true },
        },
        new CredentialGroupInfo("Tokens", TokenStore.TokensJsonFile, CredentialGroupShape.Tokens, "Tokens",
            "Single tokens by name (github, nuget-org, ...)."),
        new CredentialGroupInfo("Hosting", CredentialStore.ProvidersJsonFile, CredentialGroupShape.Entries, "Hosting",
            "Unity.Multiplayer mp-host cloud credentials: GHCR, AWS, Azure, Fly, GameLift, OCI, VPS and the rendezvous secret."),
    };

    private static readonly string[] SecretWords = { "password", "secret", "apikey", "authtoken", "token", "connectionstring", "key" };

    /// <summary>
    /// True for a field whose name says it holds a secret (password, secret, apiKey, authToken, token, connectionString,
    /// key; case-insensitive). A name ending in <c>Path</c> or <c>Url</c> (e.g. <c>sshKeyPath</c>) names where a secret
    /// is, not the secret, and stays plain.
    /// </summary>
    public static bool LooksSecret(string? fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName)) return false;
        var name = fieldName.Trim();
        if (name.EndsWith("Path", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Url", StringComparison.OrdinalIgnoreCase)) return false;
        return SecretWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The <c>Desktop</c> Profile's credential groups: each bucket's file under the roaming root, edited in place. A
/// <c>tokens.json</c> of plain strings is written through <see cref="TokenStore"/>, a <c>providers.json</c> whose values
/// are all entries through <see cref="CredentialStore.SaveAllRaw"/>; anything those stores would drop on rewrite (top-level
/// fields such as Notifications' <c>to</c>, AudioStore's flat fields, <c>ftp.json</c>'s unknown fields, non-string tokens)
/// is written as the whole document with the same atomic swap and no backup copy (M.VALT-T61). The version is a SHA-256
/// of the file's bytes, so an edit by an app or another editor since Load is caught on Save.
/// </summary>
public sealed class DesktopCredentialGroupBackend : ICredentialGroupBackend
{
    /// <summary>The version of a group whose file doesn't exist yet.</summary>
    public const string AbsentVersion = "absent";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Readable files: '+' in a phone number stays '+', not +.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? roamingRoot;

    /// <param name="roamingRoot">The roaming root; null resolves <see cref="VaultPaths.RoamingRoot"/> on every call.</param>
    public DesktopCredentialGroupBackend(string? roamingRoot = null) => this.roamingRoot = roamingRoot;

    public string? ReadOnlyReason => null;

    private string Root => roamingRoot ?? VaultPaths.RoamingRoot;

    /// <summary>The group's file; refuses bucket and file names that would leave the roaming root.</summary>
    public string PathFor(CredentialGroupInfo group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!IsPlainName(group.Bucket) || !IsPlainName(group.FileName))
            throw new InvalidOperationException("A credential group's bucket and file must be plain names.");
        return Path.Combine(Root, group.Bucket, group.FileName);
    }

    public Task<IReadOnlyList<CredentialGroupInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var groups = CredentialGroupCatalog.Known.ToList();
        try
        {
            if (Directory.Exists(Root))
            {
                foreach (var dir in Directory.EnumerateDirectories(Root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var bucket = Path.GetFileName(dir);
                    if (!IsPlainName(bucket) || CredentialGroupCatalog.NotGroups.Contains(bucket)) continue;
                    foreach (var (file, shape) in new[] { (CredentialStore.ProvidersJsonFile, CredentialGroupShape.Entries), (TokenStore.TokensJsonFile, CredentialGroupShape.Tokens) })
                    {
                        if (!File.Exists(Path.Combine(dir, file))) continue;
                        if (groups.Any(g => string.Equals(g.Bucket, bucket, StringComparison.OrdinalIgnoreCase)
                                            && string.Equals(g.FileName, file, StringComparison.OrdinalIgnoreCase))) continue;
                        var title = groups.Any(g => string.Equals(g.Bucket, bucket, StringComparison.OrdinalIgnoreCase)) ? $"{bucket} ({file})" : bucket;
                        groups.Add(new CredentialGroupInfo(bucket, file, shape, title));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The known groups still show; an unreadable root is the folder-access check's business.
        }
        return Task.FromResult<IReadOnlyList<CredentialGroupInfo>>(groups);
    }

    public async Task<CredentialGroupDocument> LoadAsync(CredentialGroupInfo group, CancellationToken cancellationToken = default)
    {
        var path = PathFor(group);
        if (!File.Exists(path)) return new CredentialGroupDocument(group, (JsonObject?)group.Template?.DeepClone() ?? new JsonObject(), AbsentVersion);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return new CredentialGroupDocument(group, Parse(bytes) ?? new JsonObject(), DesktopSettingsBackend.Hash(bytes));
    }

    public async Task<string> SaveAsync(CredentialGroupInfo group, JsonObject values, string version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var path = PathFor(group);
        var gate = Gates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : null;
            var currentVersion = current is null ? AbsentVersion : DesktopSettingsBackend.Hash(current);
            if (!string.Equals(version, currentVersion, StringComparison.Ordinal))
                throw new ConcurrencyConflictException($"{group.DisplayName} changed on disk since you opened it.");

            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var stored = (current is null ? null : Parse(current)) ?? new JsonObject();
            if (group.Shape == CredentialGroupShape.Tokens && IsPlainTokenMap(values) && IsPlainTokenMap(stored))
                WriteThroughTokenStore(new TokenStore(dir), stored, values);
            else if (group.Shape == CredentialGroupShape.Entries && string.Equals(group.FileName, CredentialStore.ProvidersJsonFile, StringComparison.OrdinalIgnoreCase)
                     && values.All(p => p.Value is JsonObject))
                new CredentialStore(dir).SaveAllRaw(values.ToDictionary(p => p.Key, p => p.Value!.ToJsonString(), StringComparer.OrdinalIgnoreCase));
            else
                WriteAtomically(path, Encoding.UTF8.GetBytes(values.ToJsonString(WriteOptions)));

            return DesktopSettingsBackend.Hash(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Applies the token map's differences one name at a time (TokenStore trims each value, as it always has).</summary>
    private static void WriteThroughTokenStore(TokenStore tokens, JsonObject stored, JsonObject values)
    {
        foreach (var (name, _) in stored.ToList())
            if (!values.Any(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase))) tokens.Remove(name);
        foreach (var (name, node) in values)
        {
            var value = node!.GetValue<string>();
            var old = stored.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));
            if (old.Value is JsonValue v && v.GetValue<string>() == value && old.Key == name) continue;
            tokens.Set(name, value);
        }
    }

    private static bool IsPlainTokenMap(JsonObject o) =>
        o.All(p => p.Value is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        && o.Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == o.Count;

    private static bool IsPlainName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name is not "." and not ".." && Path.GetFileName(name) == name
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static JsonObject? Parse(byte[] bytes)
    {
        try { return JsonNode.Parse(bytes) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>Temp file then swap, no backup copy (a backup would keep a replaced secret on disk).</summary>
    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }
}

/// <summary>A Profile that can't edit credential groups yet: the tab shows <see cref="ReadOnlyReason"/> instead.</summary>
public sealed class ReadOnlyCredentialGroupBackend(string reason) : ICredentialGroupBackend
{
    public string? ReadOnlyReason { get; } = reason;

    public Task<IReadOnlyList<CredentialGroupInfo>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CredentialGroupInfo>>(Array.Empty<CredentialGroupInfo>());

    public Task<CredentialGroupDocument> LoadAsync(CredentialGroupInfo group, CancellationToken cancellationToken = default) =>
        Task.FromException<CredentialGroupDocument>(new NotSupportedException(ReadOnlyReason));

    public Task<string> SaveAsync(CredentialGroupInfo group, JsonObject values, string version, CancellationToken cancellationToken = default) =>
        Task.FromException<string>(new NotSupportedException(ReadOnlyReason));
}
