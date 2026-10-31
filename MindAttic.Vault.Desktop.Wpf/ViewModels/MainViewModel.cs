using System.Collections.ObjectModel;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Launch;
using MindAttic.Vault.Settings;
using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Vault.Desktop.ViewModels;

/// <summary>
/// The window: the global Profile picker and the tabs (Global, then one per app manifest, kept in sync with the
/// manifests on disk). Switching Profile reloads every tab from the new backend, after asking when there are edits.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IManifestCatalog catalog;
    private readonly DesktopContext context;
    private readonly KeyOperations operations;
    private IProfile? profile;
    private ProfileReadiness readiness = ProfileReadiness.Ready;
    private TabViewModel? selectedTab;
    private string statusText = "";
    private string? profileNotice;
    private bool started;

    public MainViewModel(IEnumerable<IProfile> profiles, IManifestCatalog catalog, DesktopContext context, KeyOperations operations)
    {
        this.catalog = catalog;
        this.context = context;
        this.operations = operations;
        foreach (var p in profiles.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())) Profiles.Add(p);
        Keys = new KeysViewModel(context, operations) { Manifests = () => AppTabs.Select(t => t.Manifest).ToList() };
        // A key added, renamed, removed or tested anywhere: every tab that shows keys reloads.
        operations.Changed += (_, _) => context.Ui.Post(() => _ = KeysChangedAsync());
    }

    /// <summary>The Keys tab.</summary>
    public KeysViewModel Keys { get; }

    public string Title => "MindAttic Vault";

    public ObservableCollection<IProfile> Profiles { get; } = new();

    /// <summary>The Profile every tab loads from and saves to. Setting it switches (asking first when there are edits).</summary>
    public IProfile? SelectedProfile
    {
        get => profile;
        set
        {
            if (value is null || ReferenceEquals(value, profile)) return;
            _ = SwitchProfileAsync(value);
        }
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    public TabViewModel? SelectedTab
    {
        get => selectedTab;
        set => Set(ref selectedTab, value);
    }

    public IEnumerable<AppSettingsViewModel> AppTabs => Tabs.OfType<AppSettingsViewModel>();

    public string StatusText
    {
        get => statusText;
        set => Set(ref statusText, value);
    }

    /// <summary>Why the selected Profile can't load (e.g. Azure not signed in), or null.</summary>
    public string? ProfileNotice
    {
        get => profileNotice;
        private set
        {
            if (Set(ref profileNotice, value)) Raise(nameof(HasProfileNotice));
        }
    }

    public bool HasProfileNotice => !string.IsNullOrEmpty(ProfileNotice);

    /// <summary>Picks the first Profile (or <paramref name="profileId"/>), builds the tabs and starts watching manifests.</summary>
    public async Task InitializeAsync(string? profileId = null)
    {
        profile = Profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase))
                  ?? Profiles.FirstOrDefault(p => p.Id == DesktopProfile.ProfileId)
                  ?? Profiles.FirstOrDefault();
        Raise(nameof(SelectedProfile));
        await CheckReadinessAsync().ConfigureAwait(true);
        await RefreshAppsAsync().ConfigureAwait(true);
        SelectedTab ??= Tabs.FirstOrDefault();
        if (!started)
        {
            started = true;
            catalog.Changed += (_, _) => context.Ui.Post(() => _ = RefreshAppsAsync());
            catalog.Start();
        }
    }

    /// <summary>Switches every tab to <paramref name="target"/>. False when the user kept their edits instead.</summary>
    public async Task<bool> SwitchProfileAsync(IProfile target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(target, profile)) return true;
        var dirty = AppTabs.Where(t => t.IsDirty).Select(t => t.Title).ToList();
        if (dirty.Count > 0 && !context.Dialogs.Confirm(
                $"Discard unsaved changes in {string.Join(", ", dirty)} and switch to {target.DisplayName}?", "Switch Profile"))
        {
            Raise(nameof(SelectedProfile)); // the picker goes back
            return false;
        }

        profile = target;
        Raise(nameof(SelectedProfile));
        DesktopLog.For("Profiles").Information("Switched to Profile {Profile}", target.Id);
        await CheckReadinessAsync().ConfigureAwait(true);
        await RefreshAppsAsync().ConfigureAwait(true);
        foreach (var tab in Tabs.ToList()) await LoadTabAsync(tab).ConfigureAwait(true);
        StatusText = $"Showing {target.DisplayName}.";
        return true;
    }

    /// <summary>Syncs the tab list with the manifests on disk: new apps get a tab, removed apps lose theirs.</summary>
    public async Task RefreshAppsAsync()
    {
        IReadOnlyList<SettingsManifest> manifests;
        try { manifests = catalog.Discover(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DesktopLog.For("Tabs").Warning("Discovering manifests failed: {Type}", ex.GetType().Name);
            return;
        }

        var global = manifests.FirstOrDefault(m => string.Equals(m.AppId, LampNames.GlobalApp, StringComparison.OrdinalIgnoreCase))
                     ?? SettingsManifestBuilder.Build<MindAtticGlobalSettings>();
        var apps = manifests.Where(m => !ReferenceEquals(m, global) && !string.Equals(m.AppId, LampNames.GlobalApp, StringComparison.OrdinalIgnoreCase));

        var created = new List<TabViewModel>();
        var updated = new List<(AppSettingsViewModel Tab, SettingsManifest Manifest)>();
        var desired = FixedTabsBefore().Select(t => (t.Id, Tab: (TabViewModel?)t, Manifest: (SettingsManifest?)null))
            .Concat(new[] { global }.Concat(apps).Select(m => (Id: AppSettingsViewModel.TabIdFor(m.AppId), Tab: (TabViewModel?)null, Manifest: (SettingsManifest?)m)))
            .ToList();

        Tabs.SyncBy(desired, d => d.Id, t => t.Id,
            d =>
            {
                var tab = d.Tab ?? CreateAppTab(d.Manifest!);
                created.Add(tab);
                return tab;
            },
            (tab, d) =>
            {
                if (tab is AppSettingsViewModel app && d.Manifest is not null) updated.Add((app, d.Manifest));
            });

        var appsBefore = appCount;
        appCount = AppTabs.Count();
        if (SelectedTab is not null && !Tabs.Contains(SelectedTab)) SelectedTab = Tabs.FirstOrDefault();
        foreach (var tab in created) await LoadTabAsync(tab).ConfigureAwait(true);
        foreach (var (tab, manifest) in updated) await tab.UpdateManifestAsync(manifest).ConfigureAwait(true);
        // Which apps use which keys changed: the Keys tab's "used by" follows.
        if ((created.Any(t => t is AppSettingsViewModel) || appCount != appsBefore || updated.Count > 0) && !created.Contains(Keys))
            await LoadTabAsync(Keys).ConfigureAwait(true);
    }

    private int appCount;

    /// <summary>After any key change: the Keys tab and every app's "Keys used" reload.</summary>
    public async Task KeysChangedAsync()
    {
        if (profile is null || !readiness.IsReady) return;
        await LoadTabAsync(Keys).ConfigureAwait(true);
        foreach (var tab in AppTabs.ToList()) await tab.ReloadKeysAsync().ConfigureAwait(true);
    }

    /// <summary>Raised when a request asks for the window to come forward.</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>
    /// Shows the tab of <paramref name="request"/>'s app (validated against the discovered manifests; an unknown app is
    /// ignored and logged) and scrolls to its setting. Brings the window forward either way.
    /// </summary>
    public async Task<bool> OpenAsync(VaultOpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ActivationRequested?.Invoke(this, EventArgs.Empty);
        var tab = FindAppTab(request.AppId);
        if (tab is null)
        {
            // The app may have written its manifest a moment ago, before the watcher caught up.
            await RefreshAppsAsync().ConfigureAwait(true);
            tab = FindAppTab(request.AppId);
        }
        if (tab is null)
        {
            DesktopLog.For("Open").Information("Open request for unknown app {App} ignored", request.AppId);
            StatusText = $"No app named {request.AppId} has settings here.";
            return false;
        }

        SelectedTab = tab;
        if (request.Setting is { } setting && !tab.RequestScroll(setting))
            DesktopLog.For("Open").Information("Open request for an unknown setting of {App} ignored", tab.AppId);
        DesktopLog.For("Open").Information("Opened {App}", tab.AppId);
        return true;
    }

    private AppSettingsViewModel? FindAppTab(string appId) =>
        AppTabs.FirstOrDefault(t => string.Equals(t.AppId, appId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The fixed tabs that come before Global (Get Started, Keys), in order.</summary>
    private IEnumerable<TabViewModel> FixedTabsBefore()
    {
        yield return Keys;
    }

    private AppSettingsViewModel CreateAppTab(SettingsManifest manifest)
    {
        var tab = new AppSettingsViewModel(manifest, context);
        // One audit row per Save and per assignment: field and key names only, never values.
        tab.Saved += (t, names) =>
        {
            if (profile is not null) context.Record(profile, t.AppId, AuditActions.SettingsSave, names);
        };
        tab.KeysAssigned += (t, provider, names) =>
        {
            if (profile is not null)
                context.Record(profile, $"{t.AppId}/{provider}", AuditActions.KeysAssign, names, names.Count == 0 ? "shared default" : null);
            context.Ui.Post(() => _ = LoadTabAsync(Keys));
        };
        return tab;
    }

    private async Task LoadTabAsync(TabViewModel tab)
    {
        if (profile is null) return;
        try { await tab.LoadAsync(profile, readiness).ConfigureAwait(true); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DesktopLog.For("Tabs").Warning("Loading tab {Tab} failed: {Type}", tab.Id, ex.GetType().Name);
        }
    }

    private async Task CheckReadinessAsync()
    {
        if (profile is null) return;
        try { readiness = await profile.GetReadinessAsync().ConfigureAwait(true); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            readiness = ProfileReadiness.NeedsSetup($"{profile.DisplayName} isn't available: {ex.Message}");
        }
        ProfileNotice = readiness.IsReady ? null : readiness.Message;
    }
}
