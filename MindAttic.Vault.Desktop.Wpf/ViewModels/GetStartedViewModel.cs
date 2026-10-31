using System.Collections.ObjectModel;
using System.Windows.Input;
using MindAttic.Components.Wpf.Controls;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Settings;
using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Vault.Desktop.ViewModels;

/// <summary>Where Get Started looks for keys to import.</summary>
/// <param name="Importer">The importer over the local named keys (Desktop Profile only); null when there is none.</param>
/// <param name="WorkspaceRoot">The folder whose <c>*/legion.json</c> files are offered; null when there is none.</param>
public sealed record GetStartedSources(Func<KeyImporter?> Importer, Func<string?> WorkspaceRoot)
{
    /// <summary>The local named keys, and the workspace root from the Global settings (else D:\Projects\MindAttic when it exists).</summary>
    public static GetStartedSources Default { get; } = new(
        () => new KeyImporter(NamedKeyStore.ForRoamingRoot()),
        () =>
        {
            try
            {
                var configured = MindAtticGlobalSettings.Store().Load().WorkspaceRoot;
                if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            const string fallback = @"D:\Projects\MindAttic";
            return Directory.Exists(fallback) ? fallback : null;
        });
}

/// <summary>
/// Get Started (bring your own keys): shown first while any app's required provider (<c>[UsesCredential(Required = true)]</c>)
/// has no usable key. One row per such provider with who needs it, a key field, the provider's key page and Test; plus
/// keys found in environment variables, <c>legion.json</c> files and per-app copies, each with an Import button (never
/// automatic). Gone once every required provider has a working key.
/// </summary>
public sealed class GetStartedViewModel : TabViewModel
{
    public const string TabId = "getstarted";

    private readonly DesktopContext context;
    private readonly KeyOperations operations;
    private readonly GetStartedSources sources;
    private IProfile? profile;
    private bool isNeeded;

    public GetStartedViewModel(DesktopContext context, KeyOperations operations, GetStartedSources? sources = null)
    {
        this.context = context;
        this.operations = operations;
        this.sources = sources ?? GetStartedSources.Default;
    }

    public override string Id => TabId;
    public override string Title => "Get Started";
    public override MaStatus? Status => IsNeeded ? MaStatus.Warning : null;

    /// <summary>The manifests to read required providers from (set by the window).</summary>
    public Func<IEnumerable<SettingsManifest>> Manifests { get; set; } = () => Array.Empty<SettingsManifest>();

    /// <summary>True while some required provider has no usable key: the tab shows.</summary>
    public bool IsNeeded
    {
        get => isNeeded;
        private set
        {
            if (Set(ref isNeeded, value)) RaiseTabState();
        }
    }

    public ObservableCollection<RequiredProviderViewModel> Missing { get; } = new();
    public ObservableCollection<ImportCandidateViewModel> Candidates { get; } = new();
    public bool HasCandidates => Candidates.Count > 0;

    internal IProfile? Profile => profile;

    /// <summary>Each provider's last add/test outcome, so it survives the row being rebuilt as the keys change.</summary>
    internal Dictionary<string, string> LastMessages { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal void SetMessage(string providerId, string message)
    {
        LastMessages[providerId] = message;
        foreach (var row in Missing.Where(r => string.Equals(r.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))) row.RaiseMessage();
    }
    internal KeyOperations Operations => operations;
    internal DesktopContext Context => context;

    public override async Task LoadAsync(IProfile target, ProfileReadiness readiness)
    {
        profile = target;
        if (!readiness.IsReady)
        {
            Missing.Clear();
            Candidates.Clear();
            IsNeeded = false;
            return;
        }

        var now = context.Time.GetUtcNow();
        var required = Manifests()
            .SelectMany(m => m.Credentials.Where(c => c.Required && !string.IsNullOrWhiteSpace(c.ProviderId))
                .Select(c => (Provider: ProviderCatalog.Normalize(c.ProviderId), App: string.IsNullOrWhiteSpace(m.DisplayName) ? m.AppId : m.DisplayName)))
            .GroupBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missing = new List<(ProviderInfo Info, IReadOnlyList<string> Apps)>();
        foreach (var group in required)
        {
            IReadOnlyList<NamedKey> keys;
            try { keys = await target.Keys.GetKeysAsync(group.Key).ConfigureAwait(true); }
            catch (ProfileAccessDeniedException) { continue; }
            var usable = keys.Any(k => context.Health.Get(group.Key, k.Name).StatusAt(now, k.Disabled) is not (KeyStatus.Failed or KeyStatus.Disabled));
            if (!usable)
                missing.Add((ProviderCatalog.Get(group.Key), group.Select(x => x.App).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a).ToList()));
        }

        Missing.SyncBy(missing, m => m.Info.Id, r => r.ProviderId, m => new RequiredProviderViewModel(this, m.Info, m.Apps), (r, m) => r.Update(m.Apps));
        LoadCandidates(target);
        IsNeeded = Missing.Count > 0;
    }

    /// <summary>Imports one offered key (the user clicked Import).</summary>
    public async Task<bool> ImportAsync(ImportCandidate candidate)
    {
        if (profile is null || profile.Id != DesktopProfile.ProfileId || sources.Importer() is not { } importer) return false;
        ImportResult result;
        try
        {
            result = importer.Import(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            context.Dialogs.Error($"Couldn't import {candidate.SuggestedName}: {ex.GetType().Name}.", "Import");
            return false;
        }
        var names = result.Added.Concat(result.Reused).ToList();
        context.Record(profile, candidate.ProviderId, AuditActions.KeyImport, names,
            $"from {candidate.Kind}" + (candidate.AppId is null ? "" : $", active for {candidate.AppId}"));
        foreach (var name in result.Added) context.Health.Remove(candidate.ProviderId, name);
        StatusText = result.Added.Count > 0
            ? $"Imported {string.Join(", ", result.Added)} into {ProviderCatalog.Get(candidate.ProviderId).DisplayName}."
            : $"{ProviderCatalog.Get(candidate.ProviderId).DisplayName} already had that key ({string.Join(", ", result.Reused)}).";
        operations.NotifyChanged();
        await Task.CompletedTask.ConfigureAwait(true);
        return true;
    }

    private void LoadCandidates(IProfile target)
    {
        var found = new List<ImportCandidate>();
        // Import moves keys into the local named lists, so it is offered on the Desktop Profile only.
        if (target.Id == DesktopProfile.ProfileId && sources.Importer() is { } importer)
        {
            try
            {
                found.AddRange(importer.FindEnvironment());
                if (sources.WorkspaceRoot() is { } root && Directory.Exists(root))
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        var legion = Path.Combine(dir, "legion.json");
                        if (File.Exists(legion)) found.AddRange(importer.FindLegionJson(legion));
                    }
                found.AddRange(importer.FindAppScoped());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DesktopLog.For("GetStarted").Warning("Looking for keys to import failed: {Type}", ex.GetType().Name);
            }
        }
        Candidates.SyncBy(found, c => c.Kind + "|" + c.Source, r => r.Key, c => new ImportCandidateViewModel(this, c), (_, _) => { });
        Raise(nameof(HasCandidates));
    }
}

/// <summary>A required provider with no usable key: who needs it, where to get one, and a field to add it.</summary>
public sealed class RequiredProviderViewModel : ObservableObject
{
    private readonly GetStartedViewModel owner;
    private string neededBy = "";

    public RequiredProviderViewModel(GetStartedViewModel owner, ProviderInfo info, IReadOnlyList<string> apps)
    {
        this.owner = owner;
        Info = info;
        OpenKeyPageCommand = new RelayCommand(() => owner.Context.Dialogs.OpenUrl(info.KeyPageUrl!), () => HasKeyPage);
        Update(apps);
    }

    public ProviderInfo Info { get; }
    public string ProviderId => Info.Id;
    public string DisplayName => Info.DisplayName;
    public bool HasKeyPage => !string.IsNullOrWhiteSpace(Info.KeyPageUrl);
    public string? KeyPageUrl => Info.KeyPageUrl;

    /// <summary>"Claude — needed by Prose, Tutor".</summary>
    public string Heading => $"{DisplayName} — needed by {neededBy}";

    /// <summary>The last add/test outcome for this provider.</summary>
    public string TestMessage
    {
        get => owner.LastMessages.GetValueOrDefault(ProviderId, "");
        private set => owner.SetMessage(ProviderId, value);
    }

    internal void RaiseMessage() => Raise(nameof(TestMessage));

    public ICommand OpenKeyPageCommand { get; }

    internal void Update(IReadOnlyList<string> apps)
    {
        neededBy = string.Join(", ", apps);
        Raise(nameof(Heading));
    }

    /// <summary>Adds the pasted key to the provider's shared list and tests it. The value is only passed through.</summary>
    public async Task<KeyTestResult?> AddAndTestAsync(string secret)
    {
        if (owner.Profile is not { } profile) return null;
        var existing = await profile.Keys.GetKeysAsync(ProviderId).ConfigureAwait(true);
        var name = Enumerable.Range(1, 1000).Select(n => $"key{n}")
            .First(n => !existing.Any(k => string.Equals(k.Name, n, StringComparison.OrdinalIgnoreCase)));
        if (!await owner.Operations.AddAsync(profile, ProviderId, name, secret).ConfigureAwait(true)) return null;
        if (!owner.Operations.Tester.CanTest(ProviderId))
        {
            TestMessage = $"Added as {name}. Test not available for {DisplayName}.";
            return new KeyTestResult(KeyTestOutcome.NotAvailable, "Test not available");
        }
        TestMessage = $"Added as {name}. Testing…";
        var result = await owner.Operations.TestAsync(profile, ProviderId, name).ConfigureAwait(true);
        TestMessage = $"Added as {name}. {result.Message}.";
        return result;
    }
}

/// <summary>A key found outside the shared lists, offered for import. Shows where it is, never its value.</summary>
public sealed class ImportCandidateViewModel
{
    public ImportCandidateViewModel(GetStartedViewModel owner, ImportCandidate candidate)
    {
        Candidate = candidate;
        ImportCommand = new AsyncRelayCommand(() => owner.ImportAsync(candidate));
    }

    public ImportCandidate Candidate { get; }
    internal string Key => Candidate.Kind + "|" + Candidate.Source;
    public string ProviderName => ProviderCatalog.Get(Candidate.ProviderId).DisplayName;

    public string Description => Candidate.Kind switch
    {
        ImportSourceKind.Environment => $"Environment variable {Candidate.Source}",
        ImportSourceKind.LegionJson => $"{Candidate.Source[..Candidate.Source.LastIndexOf('#')]} ({Candidate.Source[(Candidate.Source.LastIndexOf('#') + 1)..]})",
        _ => $"{Candidate.AppId}'s own copy ({Candidate.Source})",
    };

    public string ImportsAs => $"Imports as '{Candidate.SuggestedName}'" + (Candidate.AppId is null ? "" : $", {Candidate.AppId}'s active key");

    public ICommand ImportCommand { get; }
}
