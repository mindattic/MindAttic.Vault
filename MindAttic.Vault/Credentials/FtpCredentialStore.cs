using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MindAttic.Vault.Paths;
using MindAttic.Vault.Settings;

namespace MindAttic.Vault.Credentials;

/// <summary>
/// FTP(S) deploy credentials at <c>%APPDATA%\MindAttic\Ftp\ftp.json</c>. Unlike
/// <see cref="LlmCredentialStore"/>/<see cref="BrokerCredentialStore"/> this is a
/// single flat record, not a per-provider keyring — MindAttic deploys everything
/// through one shared FTP target. On-disk shape:
/// <code>
/// {
///   "host": "ftp.example.com", "port": 21, "user": "user@example.com",
///   "password": "...", "secure": true, "servername": "prod.example.net"
/// }
/// </code>
///
/// <para>Field names (including the all-lowercase <c>servername</c> and the
/// leading-underscore <c>_rejectUnauthorized</c>) intentionally match
/// MindAttic.Deploy's pre-existing <c>secrets/ftp.json</c> / <c>ftp.json.template</c>
/// shape and MindAttic.Bob's <c>bob.ps1</c> reader, so an existing file can be
/// copied in verbatim and every consumer keeps working unchanged.</para>
///
/// <para>The <c>MINDATTIC_FTP_CREDENTIALS</c> env var overrides the directory
/// (mirrors <see cref="BrokerCredentialStore.DirectoryEnvVar"/> for tests).</para>
/// </summary>
public sealed class FtpCredentialStore
{
    /// <summary>Bucket folder name under <c>%APPDATA%\MindAttic\</c>.</summary>
    public const string Bucket = "Ftp";

    /// <summary>Filename of the on-disk credentials file.</summary>
    public const string FileName = "ftp.json";

    /// <summary>Environment variable that overrides the resolved bucket directory.</summary>
    public const string DirectoryEnvVar = "MINDATTIC_FTP_CREDENTIALS";

    /// <summary>
    /// Default instance pointed at <c>%APPDATA%\MindAttic\Ftp\</c>
    /// (or the value of <c>MINDATTIC_FTP_CREDENTIALS</c> if set).
    /// </summary>
    /// <remarks>
    /// Captured once at type-load time. Construct a fresh
    /// <see cref="FtpCredentialStore"/> if you need a runtime override.
    /// </remarks>
    public static FtpCredentialStore Default { get; } = new(ResolveDefaultDirectory());

    private readonly JsonSettingsStore<FtpSettingsFile> store;

    /// <summary>The bucket directory on disk. Does not have to exist yet — it's created lazily on first write.</summary>
    public string Directory { get; }

    /// <summary>Absolute path to <c>ftp.json</c> inside <see cref="Directory"/>.</summary>
    public string FilePath => Path.Combine(Directory, FileName);

    /// <summary>Construct an FTP credential store rooted at <paramref name="directory"/>.</summary>
    /// <param name="directory">
    /// Absolute or relative directory path. Required. The directory does not need
    /// to exist; it will be created on first write.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="directory"/> is null or whitespace.
    /// </exception>
    public FtpCredentialStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Directory is required.", nameof(directory));
        Directory = directory;
        store = new JsonSettingsStore<FtpSettingsFile>(directory, FileName);
    }

    private static string ResolveDefaultDirectory()
    {
        // Treat a blank override (env var set to "" / whitespace) as unset: a plain
        // ?? would pass the blank straight into the ctor's IsNullOrWhiteSpace guard,
        // throwing a TypeInitializationException the first time Default is touched.
        var overrideDir = Environment.GetEnvironmentVariable(DirectoryEnvVar);
        return string.IsNullOrWhiteSpace(overrideDir) ? VaultPaths.RoamingBucket(Bucket) : overrideDir;
    }

    /// <summary>True if <c>ftp.json</c> exists in <see cref="Directory"/>.</summary>
    public bool Exists() => store.Exists();

    /// <summary>Strongly-typed FTP(S) credentials.</summary>
    /// <param name="Host">FTP host/IP. Required (non-empty).</param>
    /// <param name="Port">FTP port. Conventionally 21.</param>
    /// <param name="User">FTP username. Required (non-empty).</param>
    /// <param name="Password">FTP password.</param>
    /// <param name="Secure">Whether to use FTPS (explicit TLS). Conventionally true.</param>
    /// <param name="ServerName">
    /// Optional TLS server name for certificate validation (needed when the FTP
    /// host's certificate doesn't match its IP/hostname, e.g. shared hosting).
    /// </param>
    /// <param name="RejectUnauthorized">
    /// Optional override to disable TLS certificate validation. <c>null</c> means
    /// "use the default" (validate). Only ever set <c>false</c> for a host with a
    /// self-signed/expired cert you explicitly trust.
    /// </param>
    public sealed record FtpCreds(
        string Host, int Port, string User, string Password, bool Secure,
        string? ServerName, bool? RejectUnauthorized);

    /// <summary>
    /// Loads the FTP credentials.
    /// </summary>
    /// <returns>
    /// The credential record when the file exists and has a non-empty
    /// <c>host</c> and <c>user</c>, or <c>null</c> when missing, malformed, or
    /// incomplete.
    /// </returns>
    public FtpCreds? Get()
    {
        if (!store.Exists()) return null;

        var file = store.Load();
        if (string.IsNullOrWhiteSpace(file.Host) || string.IsNullOrWhiteSpace(file.User))
            return null;

        return new FtpCreds(
            file.Host.Trim(), file.Port, file.User.Trim(), file.Password ?? "", file.Secure,
            string.IsNullOrWhiteSpace(file.ServerName) ? null : file.ServerName.Trim(),
            file.RejectUnauthorized);
    }

    /// <summary>Persists the FTP credentials, creating the directory if needed.</summary>
    /// <param name="creds">The credential record to write. Required.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="creds"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <c>Host</c> or <c>User</c> is null/whitespace.</exception>
    public void Set(FtpCreds creds)
    {
        if (creds is null) throw new ArgumentNullException(nameof(creds));
        if (string.IsNullOrWhiteSpace(creds.Host)) throw new ArgumentException("Host is required.", nameof(creds));
        if (string.IsNullOrWhiteSpace(creds.User)) throw new ArgumentException("User is required.", nameof(creds));

        store.Save(new FtpSettingsFile
        {
            Host               = creds.Host.Trim(),
            Port               = creds.Port,
            User               = creds.User.Trim(),
            Password           = creds.Password ?? "",
            Secure             = creds.Secure,
            ServerName         = creds.ServerName,
            RejectUnauthorized = creds.RejectUnauthorized
        });
    }

    /// <summary>
    /// Serializes the stored credentials into the exact flat JSON shape consumed by
    /// <c>MINDATTIC_FTP_JSON</c> (MindAttic.Deploy's <c>src/deploy.js</c>) — a direct
    /// drop-in for that env var, so callers never hand-build the JSON themselves.
    /// </summary>
    /// <returns>The compact JSON blob, or <c>null</c> when no usable credentials are stored.</returns>
    public string? TryGetJson()
    {
        if (!store.Exists()) return null;
        var file = store.Load();
        if (string.IsNullOrWhiteSpace(file.Host) || string.IsNullOrWhiteSpace(file.User)) return null;

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("host", file.Host.Trim());
            w.WriteNumber("port", file.Port);
            w.WriteString("user", file.User.Trim());
            w.WriteString("password", file.Password ?? "");
            w.WriteBoolean("secure", file.Secure);
            if (!string.IsNullOrWhiteSpace(file.ServerName))
                w.WriteString("servername", file.ServerName.Trim());
            if (file.RejectUnauthorized.HasValue)
                w.WriteBoolean("_rejectUnauthorized", file.RejectUnauthorized.Value);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // Backing shape for JsonSettingsStore<T> — field names pinned via
    // JsonPropertyName rather than relying on any naming policy, since
    // "servername"/"_rejectUnauthorized" don't match camelCase output and must
    // stay byte-for-byte compatible with the pre-existing ftp.json consumers.
    private sealed class FtpSettingsFile
    {
        [JsonPropertyName("host")]
        public string Host { get; set; } = "";

        [JsonPropertyName("port")]
        public int Port { get; set; } = 21;

        [JsonPropertyName("user")]
        public string User { get; set; } = "";

        [JsonPropertyName("password")]
        public string Password { get; set; } = "";

        [JsonPropertyName("secure")]
        public bool Secure { get; set; } = true;

        [JsonPropertyName("servername")]
        public string? ServerName { get; set; }

        [JsonPropertyName("_rejectUnauthorized")]
        public bool? RejectUnauthorized { get; set; }
    }
}
