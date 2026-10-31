using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows.Input;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Vault.Desktop.ViewModels;

/// <summary>
/// One app's tab, generated from its settings manifest: the form edits <see cref="Document"/> in place and reports each
/// changed field's full name (<see cref="MarkChanged"/>). Save validates, then writes only the changed fields through the
/// Profile; Revert reloads. The "Keys used" section assigns the provider keys the app declared.
/// </summary>
public sealed class AppSettingsViewModel : TabViewModel
{
    private readonly DesktopContext context;
    private readonly HashSet<string> changed = new(StringComparer.OrdinalIgnoreCase);
    private IProfile? profile;
    private SettingsManifest manifest;
    private JsonObject manifestJson;
    private JsonObject? document;
    private string? version;

    public AppSettingsViewModel(SettingsManifest manifest, DesktopContext context)
    {
        this.context = context;
        this.manifest = manifest;
        manifestJson = ToJson(manifest);
        SaveCommand = new AsyncRelayCommand(() => SaveAsync(), () => IsDirty && Document is not null);
        RevertCommand = new AsyncRelayCommand(RevertAsync, () => IsDirty);
        SyncCredentials();
    }

    /// <summary>Raised after <see cref="Document"/> is replaced (load, revert, profile switch): the view re-renders the form.</summary>
    public event EventHandler? DocumentReloaded;

    /// <summary>Raised when a deep link asks for one setting: the view scrolls the form to it.</summary>
    public event EventHandler<string>? ScrollRequested;

    /// <summary>Raised after a successful Save with the changed field names (never values).</summary>
    public event Action<AppSettingsViewModel, IReadOnlyList<string>>? Saved;

    /// <summary>Raised after a key assignment is written: the app, provider and names.</summary>
    public event Action<AppSettingsViewModel, string, IReadOnlyList<string>>? KeysAssigned;

    public const string IdPrefix = "app:";

    public static string TabIdFor(string appId) => IdPrefix + appId;

    public override string Id => TabIdFor(AppId);
    public string AppId => manifest.AppId;
    public override string Title => string.IsNullOrWhiteSpace(manifest.DisplayName) ? manifest.AppId : manifest.DisplayName;
    public string? Description => manifest.Description;
    public override string? Trailing => IsDirty ? "unsaved" : null;

    public SettingsManifest Manifest => manifest;

    /// <summary>The manifest as the JSON <c>MaSettingsForm.Load</c> takes.</summary>
    public JsonObject ManifestJson => manifestJson;

    /// <summary>The values being edited, or null when the Profile can't load them.</summary>
    public JsonObject? Document
    {
        get => document;
        private set
        {
            if (Set(ref document, value)) Raise(nameof(HasDocument));
        }
    }

    public bool HasDocument => Document is not null;

    /// <summary>The loaded version (passed back on Save to detect concurrent edits).</summary>
    public string? Version => version;

    public IReadOnlyCollection<string> ChangedFields => changed;
    public bool IsDirty => changed.Count > 0;

    public ObservableCollection<CredentialAssignmentViewModel> Credentials { get; } = new();
    public bool HasCredentials => Credentials.Count > 0;

    public ICommand SaveCommand { get; }
    public ICommand RevertCommand { get; }

    /// <summary>
    /// Validates the form and returns the full names of invalid fields (set by the view to <c>MaSettingsForm.Validate</c>).
    /// Null while no view shows this tab.
    /// </summary>
    public Func<IReadOnlyList<string>>? Validator { get; set; }

    /// <summary>A setting a deep link asked for before the view existed; the view scrolls to it on attach.</summary>
    public string? PendingScroll { get; private set; }

    /// <summary>The form reports that the user changed <paramref name="fieldName"/>.</summary>
    public void MarkChanged(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName) || Document is null) return;
        if (!changed.Add(fieldName)) return;
        RaiseDirty();
    }

    public override async Task LoadAsync(IProfile target, ProfileReadiness readiness)
    {
        profile = target;
        if (!readiness.IsReady)
        {
            changed.Clear();
            Document = null;
            version = null;
            Notice = readiness.Message ?? $"{target.DisplayName} isn't ready.";
            foreach (var c in Credentials) c.Options.Clear();
            RaiseDirty();
            DocumentReloaded?.Invoke(this, EventArgs.Empty);
            return;
        }
        await LoadDocumentAsync().ConfigureAwait(true);
        await ReloadKeysAsync().ConfigureAwait(true);
    }

    /// <summary>Re-reads the "Keys used" rows (after a key was added, renamed, removed or tested).</summary>
    public async Task ReloadKeysAsync()
    {
        if (profile is null) return;
        foreach (var c in Credentials)
        {
            try { await c.LoadAsync(profile.Keys).ConfigureAwait(true); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DesktopLog.For("Tabs").Warning("Loading {Provider} keys for {App} failed: {Type}", c.ProviderId, AppId, ex.GetType().Name);
            }
        }
    }

    /// <summary>Validates, then saves only the changed fields. False when nothing was saved.</summary>
    public async Task<bool> SaveAsync()
    {
        if (profile is null || Document is null || changed.Count == 0) return false;
        var errors = Validator?.Invoke() ?? Array.Empty<string>();
        if (errors.Count > 0)
        {
            StatusText = errors.Count == 1 ? "Fix the highlighted field before saving." : $"Fix the {errors.Count} highlighted fields before saving.";
            return false;
        }

        var names = changed.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            version = await profile.Settings.SaveAsync(manifest, new SettingsDocument(Document, version), names).ConfigureAwait(true);
        }
        catch (ConcurrencyConflictException ex)
        {
            if (context.Dialogs.Confirm($"{ex.Message}\n\nReload {Title}'s settings and discard your unsaved changes?", "Changed elsewhere"))
            {
                await LoadDocumentAsync().ConfigureAwait(true);
                StatusText = "Reloaded the latest values; your edits were discarded.";
            }
            else StatusText = "Not saved: the settings changed elsewhere. Revert to load them.";
            return false;
        }
        catch (ProfileAccessDeniedException ex)
        {
            context.Dialogs.Error(ex.Message, "Access denied");
            StatusText = "Not saved: access denied.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DesktopLog.For("Tabs").Warning("Saving {App} failed: {Type}", AppId, ex.GetType().Name);
            context.Dialogs.Error($"Could not save {Title}'s settings: {ex.Message}", "Save failed");
            StatusText = "Not saved.";
            return false;
        }

        changed.Clear();
        RaiseDirty();
        StatusText = $"Saved {names.Count} change{(names.Count == 1 ? "" : "s")}. Apps see it {profile.Settings.WhenAppsSeeChanges}.";
        DesktopLog.For("Tabs").Information("Saved {App} in {Profile}: {Fields}", AppId, profile.Id, names);
        Saved?.Invoke(this, names);
        return true;
    }

    /// <summary>Drops every edit and reloads the values from the Profile.</summary>
    public async Task RevertAsync()
    {
        if (profile is null) return;
        await LoadDocumentAsync().ConfigureAwait(true);
        StatusText = "Reverted.";
    }

    /// <summary>Takes a newer manifest for the app (its install added a setting). Reloads unless there are edits. False when unchanged.</summary>
    public async Task<bool> UpdateManifestAsync(SettingsManifest newer)
    {
        var json = ToJson(newer);
        if (JsonNode.DeepEquals(json, manifestJson)) return false;
        manifest = newer;
        manifestJson = json;
        Raise(nameof(Manifest));
        Raise(nameof(Title));
        Raise(nameof(Description));
        SyncCredentials();
        if (profile is not null && !IsDirty) await LoadAsync(profile, ProfileReadiness.Ready).ConfigureAwait(true);
        return true;
    }

    /// <summary>Asks the view to scroll to <paramref name="setting"/> (a full name). False when the app has no such field.</summary>
    public bool RequestScroll(string setting)
    {
        if (string.IsNullOrWhiteSpace(setting)) return false;
        var prefix = string.IsNullOrEmpty(manifest.ConfigurationPrefix) ? SettingsManifest.PrefixFor(AppId) : manifest.ConfigurationPrefix;
        if (manifest.Find(setting) is null && !setting.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase)) return false;
        PendingScroll = setting;
        ScrollRequested?.Invoke(this, setting);
        return true;
    }

    /// <summary>The view took the pending scroll.</summary>
    public void ClearPendingScroll() => PendingScroll = null;

    private async Task LoadDocumentAsync()
    {
        changed.Clear();
        try
        {
            var loaded = await profile!.Settings.LoadAsync(manifest).ConfigureAwait(true);
            version = loaded.Version;
            Document = loaded.Values;
            Notice = null;
        }
        catch (ProfileAccessDeniedException ex)
        {
            Document = null;
            version = null;
            Notice = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DesktopLog.For("Tabs").Warning("Loading {App} failed: {Type}", AppId, ex.GetType().Name);
            Document = null;
            version = null;
            Notice = $"Could not load {Title}'s settings: {ex.Message}";
        }
        RaiseDirty();
        DocumentReloaded?.Invoke(this, EventArgs.Empty);
    }

    private void SyncCredentials()
    {
        Credentials.SyncBy(manifest.Credentials.Where(c => !string.IsNullOrWhiteSpace(c.ProviderId)),
            c => Credentials_Key(c.ProviderId), r => r.ProviderId,
            c =>
            {
                var row = new CredentialAssignmentViewModel(AppId, c, context);
                row.Assigned += (provider, names) => KeysAssigned?.Invoke(this, provider, names);
                return row;
            },
            (_, _) => { });
        Raise(nameof(HasCredentials));
    }

    private static string Credentials_Key(string providerId) => Vault.Credentials.ProviderCatalog.Normalize(providerId);

    private void RaiseDirty()
    {
        Raise(nameof(IsDirty));
        Raise(nameof(ChangedFields));
        RaiseTabState();
        CommandManager.InvalidateRequerySuggested();
    }

    private static JsonObject ToJson(SettingsManifest manifest) =>
        JsonNode.Parse(SettingsManifestFile.Serialize(manifest)) as JsonObject ?? new JsonObject();
}
