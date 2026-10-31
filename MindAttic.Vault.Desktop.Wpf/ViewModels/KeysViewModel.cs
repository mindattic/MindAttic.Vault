using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using MindAttic.Components.Wpf.Controls;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Vault.Desktop.ViewModels;

/// <summary>
/// The Keys tab: every provider's shared named keys, grouped by category, in failover order. Keys are managed only
/// here; apps are assigned keys on their own tabs.
/// </summary>
public sealed class KeysViewModel : TabViewModel
{
    public const string TabId = "keys";

    private static readonly ProviderCategory[] CategoryOrder =
        { ProviderCategory.Llm, ProviderCategory.Broker, ProviderCategory.Tts, ProviderCategory.Media, ProviderCategory.Other };

    private readonly DesktopContext context;
    private readonly KeyOperations operations;
    private IProfile? profile;
    private bool anyFailed;

    public KeysViewModel(DesktopContext context, KeyOperations operations)
    {
        this.context = context;
        this.operations = operations;
    }

    public override string Id => TabId;
    public override string Title => "Keys";
    public override MaStatus? Status => anyFailed ? MaStatus.Bad : null;

    /// <summary>The apps' manifests, for "used by" (set by the window).</summary>
    public Func<IEnumerable<SettingsManifest>> Manifests { get; set; } = () => Array.Empty<SettingsManifest>();

    /// <summary>Group titles the user collapsed (remembered state).</summary>
    public ISet<string> CollapsedGroups { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when a group is collapsed or expanded.</summary>
    public event EventHandler? CollapsedGroupsChanged;

    public ObservableCollection<KeyCategoryViewModel> Groups { get; } = new();

    public IProfile? Profile => profile;
    internal KeyOperations Operations => operations;
    internal DesktopContext Context => context;

    public IEnumerable<ProviderKeysViewModel> Providers => Groups.SelectMany(g => g.Providers);

    public ProviderKeysViewModel? Provider(string providerId) =>
        Providers.FirstOrDefault(p => string.Equals(p.Id, ProviderCatalog.Normalize(providerId), StringComparison.OrdinalIgnoreCase));

    public override async Task LoadAsync(IProfile target, ProfileReadiness readiness)
    {
        profile = target;
        if (!readiness.IsReady)
        {
            Groups.Clear();
            Notice = readiness.Message ?? $"{target.DisplayName} isn't ready.";
            return;
        }
        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>Re-reads every provider's keys, health and assignments.</summary>
    public async Task ReloadAsync()
    {
        if (profile is null) return;
        var keys = profile.Keys;
        var manifests = Manifests().ToList();
        try
        {
            var withKeys = await keys.ProvidersWithKeysAsync().ConfigureAwait(true);
            var ids = ProviderCatalog.All.Select(p => p.Id)
                .Concat(withKeys)
                .Concat(manifests.SelectMany(m => m.Credentials).Select(c => c.ProviderId).Where(id => !string.IsNullOrWhiteSpace(id)))
                .Select(ProviderCatalog.Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sections = new List<ProviderKeysViewModel>();
            foreach (var id in ids)
            {
                var section = Provider(id) ?? new ProviderKeysViewModel(this, ProviderCatalog.Get(id));
                await section.LoadAsync(keys, manifests).ConfigureAwait(true);
                sections.Add(section);
            }

            var groups = sections
                .GroupBy(s => s.Info.Category)
                .OrderBy(g => Array.IndexOf(CategoryOrder, g.Key))
                .Select(g => (Category: g.Key, Providers: g
                    .OrderByDescending(p => p.Keys.Count > 0 || p.UsedByApps)
                    .ThenBy(p => ProviderCatalog.IsKnown(p.Id) ? ProviderCatalog.All.ToList().FindIndex(i => i.Id == p.Id) : int.MaxValue)
                    .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
                .ToList();
            Groups.SyncBy(groups, g => g.Category, g => g.Category, g => new KeyCategoryViewModel(this, g.Category), (_, _) => { });
            foreach (var (category, providers) in groups)
                Groups.First(g => g.Category == category).Providers.SyncBy(providers, p => p.Id, p => p.Id, p => p, (_, _) => { });

            anyFailed = Providers.Any(p => p.Keys.Any(k => k.Status == KeyStatus.Failed));
            Notice = null;
        }
        catch (ProfileAccessDeniedException ex)
        {
            Groups.Clear();
            Notice = ex.Message;
            anyFailed = false;
        }
        RaiseTabState();
    }

    internal void SetCollapsed(string group, bool collapsed)
    {
        var changed = collapsed ? CollapsedGroups.Add(group) : CollapsedGroups.Remove(group);
        if (changed) CollapsedGroupsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static string CategoryTitle(ProviderCategory category) => category switch
    {
        ProviderCategory.Llm => "LLM providers",
        ProviderCategory.Broker => "Brokers",
        ProviderCategory.Tts => "Text to speech",
        ProviderCategory.Media => "Media",
        _ => "Other services",
    };
}

/// <summary>A category of providers (LLM, Broker, ...), collapsible.</summary>
public sealed class KeyCategoryViewModel : ObservableObject
{
    private readonly KeysViewModel owner;

    public KeyCategoryViewModel(KeysViewModel owner, ProviderCategory category)
    {
        this.owner = owner;
        Category = category;
        Title = KeysViewModel.CategoryTitle(category);
    }

    public ProviderCategory Category { get; }
    public string Title { get; }
    public ObservableCollection<ProviderKeysViewModel> Providers { get; } = new();

    public bool IsExpanded
    {
        get => !owner.CollapsedGroups.Contains(Title);
        set
        {
            if (value == IsExpanded) return;
            owner.SetCollapsed(Title, !value);
            Raise();
        }
    }
}

/// <summary>One provider's named keys in failover order, with Add.</summary>
public sealed class ProviderKeysViewModel : ObservableObject
{
    private readonly KeysViewModel owner;
    private string newKeyName = "";
    private string summary = "";
    private string testMessage = "";

    public ProviderKeysViewModel(KeysViewModel owner, ProviderInfo info)
    {
        this.owner = owner;
        Info = info;
        OpenKeyPageCommand = new RelayCommand(() => owner.Context.Dialogs.OpenUrl(info.KeyPageUrl!), () => HasKeyPage);
    }

    public ProviderInfo Info { get; }
    public string Id => Info.Id;
    public string DisplayName => Info.DisplayName;
    public bool HasKeyPage => !string.IsNullOrWhiteSpace(Info.KeyPageUrl);
    public bool CanTest => owner.Operations.Tester.CanTest(Id);

    public ObservableCollection<KeyRowViewModel> Keys { get; } = new();

    /// <summary>True when some app's manifest declares this provider.</summary>
    public bool UsedByApps { get; private set; }

    /// <summary>"2 keys · used by Alpha, Beta" (names only).</summary>
    public string Summary
    {
        get => summary;
        private set => Set(ref summary, value);
    }

    /// <summary>Name for the key being added (names aren't secret).</summary>
    public string NewKeyName
    {
        get => newKeyName;
        set => Set(ref newKeyName, value);
    }

    /// <summary>The last test result for this provider, in words.</summary>
    public string TestMessage
    {
        get => testMessage;
        set => Set(ref testMessage, value);
    }

    public ICommand OpenKeyPageCommand { get; }

    /// <summary>Adds a key from the add row's secret box. The value is only passed through.</summary>
    public async Task<bool> AddAsync(string secret)
    {
        if (owner.Profile is null) return false;
        var name = string.IsNullOrWhiteSpace(NewKeyName) ? NextName() : NewKeyName;
        if (!await owner.Operations.AddAsync(owner.Profile, Id, name, secret).ConfigureAwait(true)) return false;
        NewKeyName = "";
        return true;
    }

    internal async Task LoadAsync(IKeyBackend backend, IReadOnlyList<SettingsManifest> manifests)
    {
        var keys = await backend.GetKeysAsync(Id).ConfigureAwait(true);
        var users = manifests.Where(m => m.Credentials.Any(c => string.Equals(ProviderCatalog.Normalize(c.ProviderId), Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        UsedByApps = users.Count > 0;

        // Apps on the shared order use every enabled key; apps with an assignment use the keys they name.
        var shared = new List<string>();
        foreach (var app in users)
            if ((await backend.GetAssignmentAsync(app.AppId, Id).ConfigureAwait(true)).Count == 0) shared.Add(NameOf(app));

        var now = owner.Context.Time.GetUtcNow();
        var rows = new List<KeyRowViewModel>();
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            var assigned = (await backend.AppsUsingAsync(Id, key.Name).ConfigureAwait(true))
                .Select(a => users.FirstOrDefault(m => string.Equals(m.AppId, a, StringComparison.OrdinalIgnoreCase)) is { } m ? NameOf(m) : a)
                .ToList();
            var usedBy = assigned.Concat(key.Disabled ? Array.Empty<string>() : shared.Select(s => s + " (shared)")).ToList();
            var health = owner.Context.Health.Get(Id, key.Name);
            rows.Add(new KeyRowViewModel(owner, this, key, health.StatusAt(now, key.Disabled), health, usedBy, i == 0, i == keys.Count - 1));
        }
        Keys.Clear(); // rows are rebuilt so each view's secret box reloads its value
        foreach (var row in rows) Keys.Add(row);

        Summary = (keys.Count switch { 0 => "No keys", 1 => "1 key", _ => $"{keys.Count} keys" })
                  + (users.Count > 0 ? " · used by " + string.Join(", ", users.Select(NameOf)) : "");
        Raise(nameof(UsedByApps));
    }

    private static string NameOf(SettingsManifest m) => string.IsNullOrWhiteSpace(m.DisplayName) ? m.AppId : m.DisplayName;

    private string NextName()
    {
        for (var n = 1; ; n++)
            if (!Keys.Any(k => string.Equals(k.Name, $"key{n}", StringComparison.OrdinalIgnoreCase))) return $"key{n}";
    }
}

/// <summary>
/// One named key. Its value is never a property: the view reads it with <see cref="ReadSecret"/> to fill the masked box
/// and hands an edited one to <see cref="SetValueAsync"/>.
/// </summary>
public sealed class KeyRowViewModel : ObservableObject
{
    private readonly KeysViewModel owner;
    private readonly ProviderKeysViewModel provider;
    private readonly NamedKey key;
    private string renameText;
    private string testMessage = "";

    internal KeyRowViewModel(KeysViewModel owner, ProviderKeysViewModel provider, NamedKey key, KeyStatus status, KeyHealth health,
        IReadOnlyList<string> usedBy, bool first, bool last)
    {
        this.owner = owner;
        this.provider = provider;
        this.key = key;
        renameText = key.Name;
        Status = status;
        UsedByApps = usedBy;
        LastUsed = health.LastSuccessUtc is { } ok
            ? "Last worked " + ok.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : health.LastFailureUtc is { } bad ? "Last failed " + bad.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "";
        CanMoveUp = !first;
        CanMoveDown = !last;
        TestCommand = new AsyncRelayCommand(() => TestAsync(), () => provider.CanTest);
        RenameCommand = new AsyncRelayCommand(() => RenameAsync(RenameText), () => !string.IsNullOrWhiteSpace(RenameText) && RenameText.Trim() != Name);
        ToggleDisabledCommand = new AsyncRelayCommand(() => SetDisabledAsync(!Disabled));
        RemoveCommand = new AsyncRelayCommand(RemoveAsync);
        MoveUpCommand = new AsyncRelayCommand(() => MoveAsync(-1), () => CanMoveUp);
        MoveDownCommand = new AsyncRelayCommand(() => MoveAsync(+1), () => CanMoveDown);
    }

    public string ProviderId => provider.Id;
    public string Name => key.Name;
    public bool Disabled => key.Disabled;
    public KeyStatus Status { get; }
    public MaStatus Light => KeyStatusDisplay.Light(Status);
    public string StatusLabel => KeyStatusDisplay.Label(Status);
    public string LastUsed { get; }

    /// <summary>"Healthy · Last worked 10/31/2026 3:12 PM".</summary>
    public string StatusLine => LastUsed.Length == 0 ? StatusLabel : $"{StatusLabel} · {LastUsed}";
    public IReadOnlyList<string> UsedByApps { get; }
    public string UsedBy => UsedByApps.Count == 0 ? "Not used by any app" : "Used by " + string.Join(", ", UsedByApps);
    public bool CanMoveUp { get; }
    public bool CanMoveDown { get; }
    public string DisableLabel => Disabled ? "Enable" : "Disable";

    /// <summary>The new name typed in the rename box.</summary>
    public string RenameText
    {
        get => renameText;
        set => Set(ref renameText, value);
    }

    public string TestMessage
    {
        get => testMessage;
        private set => Set(ref testMessage, value);
    }

    public ICommand TestCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand ToggleDisabledCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }

    /// <summary>The stored value, for the masked box only. A method, so no binding, trace or log can read it.</summary>
    public string ReadSecret() => key.Value;

    public override string ToString() => Name;

    public async Task<bool> SetValueAsync(string secret) =>
        owner.Profile is not null && await owner.Operations.SetValueAsync(owner.Profile, ProviderId, Name, secret).ConfigureAwait(true);

    public async Task<bool> RenameAsync(string newName) =>
        owner.Profile is not null && await owner.Operations.RenameAsync(owner.Profile, ProviderId, Name, newName).ConfigureAwait(true);

    public async Task<bool> SetDisabledAsync(bool disabled) =>
        owner.Profile is not null && await owner.Operations.SetDisabledAsync(owner.Profile, ProviderId, Name, disabled).ConfigureAwait(true);

    public async Task<bool> RemoveAsync() =>
        owner.Profile is not null && await owner.Operations.RemoveAsync(owner.Profile, ProviderId, Name).ConfigureAwait(true);

    public async Task<bool> MoveAsync(int delta) =>
        owner.Profile is not null && await owner.Operations.MoveAsync(owner.Profile, ProviderId, Name, delta).ConfigureAwait(true);

    public async Task<KeyTestResult> TestAsync()
    {
        if (owner.Profile is null) return new KeyTestResult(KeyTestOutcome.Inconclusive, "No Profile.");
        TestMessage = "Testing…";
        var result = await owner.Operations.TestAsync(owner.Profile, ProviderId, Name).ConfigureAwait(true);
        TestMessage = result.Message;
        provider.TestMessage = $"{Name}: {result.Message}";
        return result;
    }
}
