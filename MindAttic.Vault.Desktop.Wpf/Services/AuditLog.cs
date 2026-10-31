using System.Text.Json;
using Microsoft.Data.Sqlite;
using MindAttic.Vault.Paths;

namespace MindAttic.Vault.Desktop.Services;

/// <summary>
/// One audited change. <see cref="Names"/> are field full names or key names; a secret appears only as the word
/// "changed" in <see cref="Detail"/>, never as a value or a hash.
/// </summary>
public sealed record AuditEntry(
    DateTimeOffset TimeUtc,
    string Actor,
    string Profile,
    string Target,
    string Action,
    IReadOnlyList<string> Names,
    string? Detail = null);

/// <summary>What the audit log calls each change.</summary>
public static class AuditActions
{
    public const string SettingsSave = "settings.save";
    public const string KeyAdd = "key.add";
    public const string KeyValue = "key.value";
    public const string KeyRename = "key.rename";
    public const string KeyDisable = "key.disable";
    public const string KeyEnable = "key.enable";
    public const string KeyRemove = "key.remove";
    public const string KeyReorder = "key.reorder";
    public const string KeyImport = "key.import";
    public const string KeysAssign = "keys.assign";
    public const string AclFix = "acl.fix";
}

/// <summary>Who changed what, when. Separate from the diagnostic log; writes never throw.</summary>
public interface IAuditLog
{
    void Record(AuditEntry entry);

    /// <summary>Every entry, oldest first.</summary>
    IReadOnlyList<AuditEntry> ReadAll();
}

/// <summary>
/// Append-only SQLite at <c>%LOCALAPPDATA%\MindAttic\Vault\audit.db</c>: triggers abort every UPDATE and DELETE, so a
/// row once written stays as written. A failed write is logged (type only) and swallowed, never thrown into a Save.
/// </summary>
public sealed class SqliteAuditLog : IAuditLog
{
    public const string FileName = "audit.db";

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS audit (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            time_utc  TEXT NOT NULL,
            actor     TEXT NOT NULL,
            profile   TEXT NOT NULL,
            target    TEXT NOT NULL,
            action    TEXT NOT NULL,
            names     TEXT NOT NULL,
            detail    TEXT NULL
        );
        CREATE TRIGGER IF NOT EXISTS audit_no_update BEFORE UPDATE ON audit
        BEGIN SELECT RAISE(ABORT, 'audit is append-only'); END;
        CREATE TRIGGER IF NOT EXISTS audit_no_delete BEFORE DELETE ON audit
        BEGIN SELECT RAISE(ABORT, 'audit is append-only'); END;
        """;

    private readonly Func<string> path;
    private readonly object gate = new();

    /// <param name="path">The database file; null uses <see cref="DefaultPath"/> (resolved on each write).</param>
    public SqliteAuditLog(string? path = null) => this.path = path is null ? () => DefaultPath : () => path;

    /// <summary><c>%LOCALAPPDATA%\MindAttic\Vault\audit.db</c> (or under the local-root override).</summary>
    public static string DefaultPath => Path.Combine(VaultPaths.LocalApp("Vault"), FileName);

    public string FilePath => path();

    public void Record(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            lock (gate)
            {
                using var connection = Open();
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO audit (time_utc, actor, profile, target, action, names, detail)
                    VALUES ($time, $actor, $profile, $target, $action, $names, $detail)
                    """;
                insert.Parameters.AddWithValue("$time", entry.TimeUtc.UtcDateTime.ToString("O"));
                insert.Parameters.AddWithValue("$actor", entry.Actor);
                insert.Parameters.AddWithValue("$profile", entry.Profile);
                insert.Parameters.AddWithValue("$target", entry.Target);
                insert.Parameters.AddWithValue("$action", entry.Action);
                insert.Parameters.AddWithValue("$names", JsonSerializer.Serialize(entry.Names));
                insert.Parameters.AddWithValue("$detail", (object?)entry.Detail ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DesktopLog.For("Audit").Warning("An audit entry for {Action} on {Target} could not be written: {Type}", entry.Action, entry.Target,
                ex.GetType().Name);
        }
    }

    public IReadOnlyList<AuditEntry> ReadAll()
    {
        var entries = new List<AuditEntry>();
        if (!File.Exists(FilePath)) return entries;
        lock (gate)
        {
            using var connection = Open();
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT time_utc, actor, profile, target, action, names, detail FROM audit ORDER BY id";
            using var reader = select.ExecuteReader();
            while (reader.Read())
                entries.Add(new AuditEntry(
                    DateTimeOffset.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture),
                    reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? Array.Empty<string>(),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return entries;
    }

    /// <summary>An open connection with the schema and triggers in place. Pooling is off so the file is never held open.</summary>
    internal SqliteConnection Open()
    {
        var file = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        connection.Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = Schema;
        schema.ExecuteNonQuery();
        return connection;
    }
}
