using System.Text.Json.Nodes;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Vault.Desktop.Profiles;

/// <summary>Whether a Profile can Load/Save right now, and if not, what the user must do.</summary>
public sealed record ProfileReadiness(bool IsReady, string? Message = null)
{
    public static ProfileReadiness Ready { get; } = new(true);
    public static ProfileReadiness NeedsSetup(string message) => new(false, message);
}

/// <summary>
/// A named backend for every tab: <c>Desktop</c> (local AppData files) or <c>Azure</c> (App Configuration
/// + Key Vault). Tabs and fields are identical in every Profile; only where values load from and save to
/// changes. A future platform is one more implementation.
/// </summary>
public interface IProfile
{
    /// <summary>Stable id, e.g. <c>Desktop</c>, <c>Azure</c>. Remembered in Desktop's state.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Checked before Load/Save; Azure answers NeedsSetup until signed in and mapped.</summary>
    Task<ProfileReadiness> GetReadinessAsync(CancellationToken cancellationToken = default);

    /// <summary>Settings values per app.</summary>
    ISettingsBackend Settings { get; }

    /// <summary>The shared named keys and per-app assignments.</summary>
    IKeyBackend Keys { get; }

    /// <summary>
    /// The structured credential groups apps share (Notifications, Subtitles, AudioStore, Brokers, Ftp, Tokens,
    /// Hosting). A Profile that doesn't implement them shows the Credentials tab read-only with this note.
    /// </summary>
    ICredentialGroupBackend CredentialGroups => new ReadOnlyCredentialGroupBackend(
        $"Credential groups can't be edited in {DisplayName} yet; switch to the Desktop Profile to edit this PC's. (Azure support: M.VALT-T63.)");

    /// <summary>Who is making changes, for the audit log (Windows user, or the Azure UPN).</summary>
    string Actor { get; }
}

/// <summary>An app's settings as an editable JSON document, with the backend's version for concurrency.</summary>
/// <param name="Values">The settings.json-shaped object (property names as in the manifest).</param>
/// <param name="Version">Opaque version (file timestamp, ETags); passed back on Save to detect concurrent edits.</param>
public sealed record SettingsDocument(JsonObject Values, string? Version);

/// <summary>Thrown by Save when someone else changed the values since they were loaded.</summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string message) : base(message) { }
}

/// <summary>Thrown when the backend refuses for lack of permission; the message says which role on which resource.</summary>
public sealed class ProfileAccessDeniedException : Exception
{
    public ProfileAccessDeniedException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Loads and saves one app's settings document in a Profile.</summary>
public interface ISettingsBackend
{
    /// <summary>
    /// The app's current values. A backend with nothing saved yet returns the manifest defaults
    /// (each field's <c>default</c>) so a first-run tab is never blank.
    /// </summary>
    Task<SettingsDocument> LoadAsync(SettingsManifest manifest, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves <paramref name="document"/>. <paramref name="changedFields"/> are the full names that were edited;
    /// a backend may write only those. Throws <see cref="ConcurrencyConflictException"/> when
    /// <see cref="SettingsDocument.Version"/> is stale. Returns the new version.
    /// </summary>
    Task<string?> SaveAsync(SettingsManifest manifest, SettingsDocument document, IReadOnlyCollection<string> changedFields,
        CancellationToken cancellationToken = default);

    /// <summary>Plain words for when the app will see a saved value (e.g. "on its next start").</summary>
    string WhenAppsSeeChanges { get; }
}

/// <summary>The shared named key lists and app assignments in a Profile. Mirrors <see cref="NamedKeyStore"/>.</summary>
public interface IKeyBackend
{
    Task<IReadOnlyList<string>> ProvidersWithKeysAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NamedKey>> GetKeysAsync(string providerId, CancellationToken cancellationToken = default);
    Task AddAsync(string providerId, string name, string value, CancellationToken cancellationToken = default);
    Task SetValueAsync(string providerId, string name, string value, CancellationToken cancellationToken = default);
    Task RenameAsync(string providerId, string oldName, string newName, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="KeyInUseException"/> while any app has the key assigned.</summary>
    Task RemoveAsync(string providerId, string name, CancellationToken cancellationToken = default);

    Task SetDisabledAsync(string providerId, string name, bool disabled, CancellationToken cancellationToken = default);
    Task ReorderAsync(string providerId, IReadOnlyList<string> names, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetAssignmentAsync(string appId, string providerId, CancellationToken cancellationToken = default);
    Task SetAssignmentAsync(string appId, string providerId, IReadOnlyList<string> names, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> AppsUsingAsync(string providerId, string name, CancellationToken cancellationToken = default);
}
