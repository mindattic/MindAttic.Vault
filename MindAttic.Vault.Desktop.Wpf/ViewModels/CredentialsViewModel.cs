using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Desktop.Services;

namespace MindAttic.Vault.Desktop.ViewModels;

/// <summary>
/// The Credentials tab (M.VALT-T62): the structured credential groups several apps share (Notifications, Subtitles,
/// AudioStore, Brokers, Ftp, Tokens, Hosting, and any other bucket on disk), one section per group, each entry's fields
/// as rows. Secret-looking fields are edited only through a masked box: their values never sit in a bindable property,
/// a log line or an audit row. Save writes the group's whole document through the Profile, keeping every field the
/// form didn't touch (and its JSON type) as it was.
/// </summary>
public sealed class CredentialsViewModel : TabViewModel
{
    public const string TabId = "credentials";

    private readonly DesktopContext context;
    private IProfile? profile;

    public CredentialsViewModel(DesktopContext context) => this.context = context;

    public override string Id => TabId;
    public override string Title => "Credentials";
    public override string? Trailing => IsDirty ? "unsaved" : null;

    public ObservableCollection<CredentialGroupViewModel> Groups { get; } = new();

    public bool IsDirty => Groups.Any(g => g.IsDirty);

    internal IProfile? Profile => profile;
    internal DesktopContext Context => context;

    /// <summary>The group for <paramref name="bucket"/> (and <paramref name="fileName"/> when a bucket has two files).</summary>
    public CredentialGroupViewModel? Group(string bucket, string? fileName = null) =>
        Groups.FirstOrDefault(g => string.Equals(g.Bucket, bucket, StringComparison.OrdinalIgnoreCase)
                                   && (fileName is null || string.Equals(g.FileName, fileName, StringComparison.OrdinalIgnoreCase)));

    public override async Task LoadAsync(IProfile target, ProfileReadiness readiness)
    {
        profile = target;
        if (!readiness.IsReady)
        {
            Groups.Clear();
            Notice = readiness.Message ?? $"{target.DisplayName} isn't ready.";
            RaiseTabState();
            return;
        }
        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>Re-reads every group from the Profile (edits are dropped).</summary>
    public async Task ReloadAsync()
    {
        if (profile is null) return;
        var backend = profile.CredentialGroups;
        if (backend.ReadOnlyReason is { } reason)
        {
            Groups.Clear();
            Notice = reason;
            RaiseTabState();
            return;
        }
        try
        {
            var infos = await backend.ListAsync().ConfigureAwait(true);
            var groups = new List<CredentialGroupViewModel>();
            foreach (var info in infos)
            {
                var group = Groups.FirstOrDefault(g => g.Info.Id == info.Id) ?? new CredentialGroupViewModel(this, info);
                await group.LoadAsync().ConfigureAwait(true);
                groups.Add(group);
            }
            Groups.SyncBy(groups, g => g.Info.Id, g => g.Info.Id, g => g, (_, _) => { });
            Notice = null;
        }
        catch (ProfileAccessDeniedException ex)
        {
            Groups.Clear();
            Notice = ex.Message;
        }
        RaiseTabState();
    }

    internal void RaiseDirty() => RaiseTabState();
}

/// <summary>One credential group: its entries, Add entry, Save and Revert.</summary>
public sealed class CredentialGroupViewModel : ObservableObject
{
    private readonly CredentialsViewModel owner;
    private readonly List<string> removedEntries = new();
    private JsonObject loaded = new();
    private string? version;
    private string newEntryName = "";
    private string statusText = "";
    private bool isExpanded = true;

    internal CredentialGroupViewModel(CredentialsViewModel owner, CredentialGroupInfo info)
    {
        this.owner = owner;
        Info = info;
        AddEntryCommand = new RelayCommand(() => AddEntry(NewEntryName), () => CanAddEntries && !string.IsNullOrWhiteSpace(NewEntryName));
        SaveCommand = new AsyncRelayCommand(() => SaveAsync(), () => IsDirty);
        RevertCommand = new AsyncRelayCommand(RevertAsync, () => IsDirty);
    }

    public CredentialGroupInfo Info { get; }
    public string Bucket => Info.Bucket;
    public string FileName => Info.FileName;
    public string Title => Info.DisplayName;
    public string? Description => Info.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    /// <summary>Where apps read it and where it lives: <c>MindAttic:Vault:Notifications · Notifications\providers.json</c>.</summary>
    public string Location => $"{Info.FullName} · {Bucket}\\{FileName}" + (version == DesktopCredentialGroupBackend.AbsentVersion ? " (not created yet)" : "");

    /// <summary>Only <c>providers.json</c>-shaped groups have named entries to add; token maps add tokens on their one entry.</summary>
    public bool CanAddEntries => Info.Shape == CredentialGroupShape.Entries;

    public ObservableCollection<CredentialEntryViewModel> Entries { get; } = new();

    public bool IsDirty => removedEntries.Count > 0 || Entries.Any(e => e.IsDirty);
    public string? Trailing => IsDirty ? "unsaved" : null;

    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }

    /// <summary>Name for the entry being added (e.g. <c>alpaca-paper</c>); names aren't secret.</summary>
    public string NewEntryName
    {
        get => newEntryName;
        set => Set(ref newEntryName, value);
    }

    /// <summary>What the last Save or Revert did, in words (never a value).</summary>
    public string StatusText
    {
        get => statusText;
        set => Set(ref statusText, value);
    }

    public ICommand AddEntryCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand RevertCommand { get; }

    public CredentialEntryViewModel? Entry(string name) =>
        Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The entry holding the group's own top-level fields (Notifications' <c>to</c>, a token map's tokens, ftp.json's fields).</summary>
    public CredentialEntryViewModel? Root => Entries.FirstOrDefault(e => e.IsRoot);

    public override string ToString() => Title;

    internal async Task LoadAsync()
    {
        if (owner.Profile is null) return;
        var document = await owner.Profile.CredentialGroups.LoadAsync(Info).ConfigureAwait(true);
        loaded = document.Values;
        version = document.Version;
        Build();
    }

    /// <summary>Rebuilds the rows from the loaded document (each view's secret box reloads its value).</summary>
    private void Build()
    {
        removedEntries.Clear();
        Entries.Clear();
        var allSecret = Info.Shape == CredentialGroupShape.Tokens;
        var rootFields = Info.Shape == CredentialGroupShape.Entries
            ? loaded.Where(p => p.Value is not JsonObject).ToList()
            : loaded.ToList();
        // A token map and a single object always show their one entry (that's where you add); a providers.json shows
        // its top-level fields only when it has some.
        if (Info.Shape != CredentialGroupShape.Entries || rootFields.Count > 0)
            Entries.Add(new CredentialEntryViewModel(this, "", isNew: false, allSecret, rootFields));
        if (Info.Shape == CredentialGroupShape.Entries)
            foreach (var (name, node) in loaded)
                if (node is JsonObject entry)
                    Entries.Add(new CredentialEntryViewModel(this, name, isNew: false, allSecret, entry.ToList()));
        Raise(nameof(Location));
        RaiseDirty();
    }

    /// <summary>Adds an empty entry (Save writes it). False, with a message, for a blank or taken name.</summary>
    public bool AddEntry(string name)
    {
        if (!CanAddEntries) return false;
        var clean = (name ?? "").Trim();
        if (clean.Length == 0)
        {
            StatusText = "Type a name for the new entry.";
            return false;
        }
        if (Entry(clean) is not null || loaded.Any(p => p.Value is not JsonObject && string.Equals(p.Key, clean, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = $"{Title} already has {clean}.";
            return false;
        }
        // A removed entry added back again starts empty: Save removes the old one first.
        Entries.Add(new CredentialEntryViewModel(this, clean, isNew: true, allSecret: false, Array.Empty<KeyValuePair<string, JsonNode?>>()));
        NewEntryName = "";
        StatusText = $"Added {clean}; add its fields, then Save.";
        RaiseDirty();
        return true;
    }

    internal void RemoveEntry(CredentialEntryViewModel entry)
    {
        if (entry.IsRoot || !Entries.Remove(entry)) return;
        if (!entry.IsNew) removedEntries.Add(entry.Name);
        RaiseDirty();
    }

    /// <summary>Validates and writes the group. False when nothing was saved (no edits, invalid field, conflict, failure).</summary>
    public async Task<bool> SaveAsync()
    {
        var profile = owner.Profile;
        if (profile is null || version is null || !IsDirty) return false;
        var invalid = Entries.SelectMany(e => e.Fields).Where(f => f.Error is not null).ToList();
        if (invalid.Count > 0)
        {
            StatusText = string.Join(" ", invalid.Select(f => f.Error));
            return false;
        }

        var document = (JsonObject)loaded.DeepClone();
        var changed = new List<string>();
        var secrets = new List<string>();
        var notes = new List<string>();
        foreach (var name in removedEntries)
        {
            var key = KeyOf(document, name);
            if (key is null) continue;
            document.Remove(key);
            changed.Add(FullName(name, null));
            notes.Add($"removed {name}");
        }
        foreach (var entry in Entries)
        {
            JsonObject target;
            if (entry.IsRoot) target = document;
            else if (KeyOf(document, entry.Name) is { } key && document[key] is JsonObject existing) target = existing;
            else
            {
                target = new JsonObject();
                document[entry.Name] = target;
                changed.Add(FullName(entry.Name, null));
                notes.Add($"added {entry.Name}");
            }
            entry.ApplyTo(target, changed, secrets, notes);
        }
        if (secrets.Count > 0) notes.Add("secrets changed: " + string.Join(", ", secrets));

        try
        {
            version = await profile.CredentialGroups.SaveAsync(Info, document, version).ConfigureAwait(true);
        }
        catch (ConcurrencyConflictException ex)
        {
            StatusText = ex.Message + " Revert to load the current values, then make your change again.";
            owner.Context.Dialogs.Error(StatusText, "Credentials");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProfileAccessDeniedException or NotSupportedException or InvalidOperationException)
        {
            // The type only: an exception message can quote a path, never needed to say what went wrong.
            DesktopLog.For("Credentials").Warning("Saving credential group {Group} failed: {Type}", Info.Id, ex.GetType().Name);
            StatusText = ex is ProfileAccessDeniedException ? ex.Message : $"Couldn't save {Title} ({ex.GetType().Name}).";
            owner.Context.Dialogs.Error(StatusText, "Credentials");
            return false;
        }

        // One audit row per Save: full names only, secrets as "changed", never a value.
        owner.Context.Record(profile, "credentials/" + Info.Id, AuditActions.CredentialsSave, changed.Distinct(StringComparer.OrdinalIgnoreCase),
            notes.Count == 0 ? null : string.Join("; ", notes));
        DesktopLog.For("Credentials").Information("Saved credential group {Group}: {Count} change(s)", Info.Id, changed.Count);
        loaded = document;
        Build();
        StatusText = $"Saved {Title} ({changed.Count} change{(changed.Count == 1 ? "" : "s")}). Apps see it on their next read.";
        return true;
    }

    /// <summary>Drops the edits and reloads the stored values.</summary>
    public async Task RevertAsync()
    {
        try
        {
            await LoadAsync().ConfigureAwait(true);
            StatusText = "Reverted to the saved values.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProfileAccessDeniedException)
        {
            DesktopLog.For("Credentials").Warning("Reloading credential group {Group} failed: {Type}", Info.Id, ex.GetType().Name);
            StatusText = $"Couldn't reload {Title} ({ex.GetType().Name}).";
        }
    }

    /// <summary><c>MindAttic:Vault:{Bucket}[:{entry}][:{field}]</c>.</summary>
    internal string FullName(string? entry, string? field)
    {
        var name = Info.FullName;
        if (!string.IsNullOrEmpty(entry)) name += ":" + entry;
        if (!string.IsNullOrEmpty(field)) name += ":" + field;
        return name;
    }

    internal void RaiseDirty()
    {
        Raise(nameof(IsDirty));
        Raise(nameof(Trailing));
        owner.RaiseDirty();
    }

    internal static string? KeyOf(JsonObject o, string name)
    {
        if (o.ContainsKey(name)) return name;
        foreach (var (key, _) in o)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return key;
        return null;
    }
}

/// <summary>One entry of a group (e.g. Notifications' <c>email</c>), or the group's own top-level fields (<see cref="IsRoot"/>).</summary>
public sealed class CredentialEntryViewModel : ObservableObject
{
    private readonly CredentialGroupViewModel group;
    private readonly List<string> removedFields = new();
    private readonly bool allSecret;
    private string newFieldName = "";

    internal CredentialEntryViewModel(CredentialGroupViewModel group, string name, bool isNew, bool allSecret,
        IEnumerable<KeyValuePair<string, JsonNode?>> fields)
    {
        this.group = group;
        this.allSecret = allSecret;
        Name = name;
        IsNew = isNew;
        foreach (var (field, node) in fields)
            Fields.Add(new CredentialFieldViewModel(this, field, node, isNew: false, allSecret || CredentialGroupCatalog.LooksSecret(field)));
        AddFieldCommand = new RelayCommand(() => AddField(NewFieldName), () => !string.IsNullOrWhiteSpace(NewFieldName));
        RemoveCommand = new RelayCommand(() => group.RemoveEntry(this), () => !IsRoot);
    }

    /// <summary>The entry's key in the file, or empty for the group's own fields.</summary>
    public string Name { get; }
    public bool IsRoot => Name.Length == 0;
    public bool IsNew { get; }
    public bool CanRemove => !IsRoot;
    public string Title => IsRoot ? (group.Info.Shape == CredentialGroupShape.Entries ? "Group fields" : group.Title) : Name;
    public string FullName => group.FullName(IsRoot ? null : Name, null);

    /// <summary>"Add token" on a token map, "Add field" elsewhere.</summary>
    public string AddLabel => allSecret ? "Add token" : "Add field";

    public ObservableCollection<CredentialFieldViewModel> Fields { get; } = new();

    public bool IsDirty => IsNew || removedFields.Count > 0 || Fields.Any(f => f.IsDirty);

    /// <summary>Name for the field (or token) being added; names aren't secret.</summary>
    public string NewFieldName
    {
        get => newFieldName;
        set => Set(ref newFieldName, value);
    }

    public ICommand AddFieldCommand { get; }
    public ICommand RemoveCommand { get; }

    public CredentialFieldViewModel? Field(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Title;

    /// <summary>Adds an empty text field (masked when its name looks secret, always on a token map). False for a blank or taken name.</summary>
    public bool AddField(string name)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length == 0) return false;
        if (Field(clean) is not null || (IsRoot && group.Info.Shape == CredentialGroupShape.Entries && group.Entry(clean) is not null))
        {
            group.StatusText = $"{Title} already has {clean}.";
            return false;
        }
        Fields.Add(new CredentialFieldViewModel(this, clean, JsonValue.Create(""), isNew: true, allSecret || CredentialGroupCatalog.LooksSecret(clean)));
        NewFieldName = "";
        Changed();
        return true;
    }

    internal void RemoveField(CredentialFieldViewModel field)
    {
        if (!Fields.Remove(field)) return;
        if (!field.IsNew) removedFields.Add(field.Name);
        Changed();
    }

    /// <summary>Writes this entry's edits into <paramref name="target"/>; untouched fields keep their stored value and type.</summary>
    internal void ApplyTo(JsonObject target, List<string> changed, List<string> secrets, List<string> notes)
    {
        foreach (var name in removedFields)
        {
            if (CredentialGroupViewModel.KeyOf(target, name) is not { } key) continue;
            target.Remove(key);
            changed.Add(FieldName(name));
            notes.Add($"removed {FieldName(name)}");
        }
        foreach (var field in Fields.Where(f => f.IsDirty))
        {
            var key = CredentialGroupViewModel.KeyOf(target, field.Name) ?? field.Name;
            target[key] = field.BuildValue();
            changed.Add(field.FullName);
            if (field.IsSecret) secrets.Add(field.FullName);
        }
    }

    internal string FieldName(string field) => group.FullName(IsRoot ? null : Name, field);

    internal void Changed()
    {
        Raise(nameof(IsDirty));
        group.RaiseDirty();
    }
}

/// <summary>
/// One field. A plain field is edited through <see cref="Text"/> and saved back as its stored JSON type (a number stays
/// a number). A secret field has no value property at all: the view fills its masked box from <see cref="ReadSecret"/>
/// and hands edits to <see cref="EditSecret"/>; <see cref="Text"/> stays empty. A nested object or list is shown by size
/// only and kept as it is.
/// </summary>
public sealed class CredentialFieldViewModel : ObservableObject
{
    private readonly CredentialEntryViewModel entry;
    private readonly JsonNode? original;
    private readonly JsonValueKind kind;
    private readonly string originalText;
    private string text;
    private string? editedSecret;

    internal CredentialFieldViewModel(CredentialEntryViewModel entry, string name, JsonNode? value, bool isNew, bool isSecret)
    {
        this.entry = entry;
        Name = name;
        IsNew = isNew;
        original = value?.DeepClone();
        kind = value?.GetValueKind() ?? JsonValueKind.Null;
        IsNested = kind is JsonValueKind.Object or JsonValueKind.Array;
        IsSecret = isSecret && !IsNested;
        originalText = IsSecret || IsNested ? "" : TextOf(value);
        text = originalText;
        RemoveCommand = new RelayCommand(() => entry.RemoveField(this));
    }

    public string Name { get; }
    public string FullName => entry.FieldName(Name);
    public bool IsNew { get; }
    public bool IsSecret { get; }

    /// <summary>An object or list: shown by size, never edited or opened (a list of keys is still keys).</summary>
    public bool IsNested { get; }

    public bool ShowTextBox => !IsSecret && !IsNested;
    public bool ShowSecretBox => IsSecret;
    public bool ShowNested => IsNested;

    /// <summary>"number", "true / false", "list of 2, kept as is"... (never a value).</summary>
    public string KindLabel => kind switch
    {
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "true / false",
        JsonValueKind.Null => "empty",
        JsonValueKind.Object => $"object of {original!.AsObject().Count}, kept as is",
        JsonValueKind.Array => $"list of {original!.AsArray().Count}, kept as is",
        _ => IsSecret ? "secret" : "",
    };

    /// <summary>A plain field's text. Always empty for a secret or nested field.</summary>
    public string Text
    {
        get => text;
        set
        {
            if (!ShowTextBox) return;
            if (!Set(ref text, value ?? "")) return;
            Raise(nameof(Error));
            Raise(nameof(IsDirty));
            entry.Changed();
        }
    }

    public bool IsDirty => IsNew || (IsSecret ? editedSecret is not null : ShowTextBox && !string.Equals(text, originalText, StringComparison.Ordinal));

    /// <summary>Why the text can't be saved as the field's type, or null.</summary>
    public string? Error => ShowTextBox && IsDirty && !TryBuild(out _) ? kind switch
    {
        JsonValueKind.Number => $"{FullName} must be a number.",
        _ => $"{FullName} must be true or false.",
    } : null;

    public ICommand RemoveCommand { get; }

    /// <summary>The stored secret, for the masked box only. A method, so no binding, trace or log can read it.</summary>
    public string ReadSecret() => !IsSecret || original is null ? "" : original.GetValueKind() == JsonValueKind.String ? original.GetValue<string>() : original.ToJsonString();

    /// <summary>The masked box's current value; the same as the stored one counts as unchanged.</summary>
    public void EditSecret(string? value)
    {
        if (!IsSecret) return;
        var next = value ?? "";
        editedSecret = !IsNew && next == ReadSecret() ? null : next;
        Raise(nameof(IsDirty));
        entry.Changed();
    }

    public override string ToString() => Name;

    /// <summary>The value to store: the stored JSON type for plain text, a string for a secret.</summary>
    internal JsonNode? BuildValue()
    {
        if (IsSecret) return JsonValue.Create(editedSecret ?? ReadSecret());
        if (IsNested) return original?.DeepClone();
        return TryBuild(out var node) ? node : JsonValue.Create(text);
    }

    private bool TryBuild(out JsonNode? node)
    {
        var trimmed = text.Trim();
        switch (kind)
        {
            case JsonValueKind.Number:
                node = null;
                try
                {
                    if (JsonNode.Parse(trimmed) is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
                    {
                        node = v;
                        return true;
                    }
                }
                catch (JsonException) { }
                return false;
            case JsonValueKind.True or JsonValueKind.False:
                node = bool.TryParse(trimmed, out var b) ? JsonValue.Create(b) : null;
                return node is not null;
            case JsonValueKind.Null:
                node = text.Length == 0 ? null : JsonValue.Create(text);
                return true;
            default:
                node = JsonValue.Create(text);
                return true;
        }
    }

    private static string TextOf(JsonNode? value) => value?.GetValueKind() switch
    {
        null or JsonValueKind.Null => "",
        JsonValueKind.String => value.GetValue<string>(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.ToJsonString(),
    };
}
