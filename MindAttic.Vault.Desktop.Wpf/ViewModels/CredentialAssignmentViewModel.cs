using System.Collections.ObjectModel;
using System.Windows.Input;
using MindAttic.Components.Wpf.Controls;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Vault.Desktop.ViewModels;

/// <summary>A key name in a picker, with its health light. Never the key's value.</summary>
public sealed record KeyOption(string Name, MaStatus? Status, string StatusLabel)
{
    public override string ToString() => Name;
}

/// <summary>One of an app's fallback keys, in order.</summary>
public sealed class FallbackKeyViewModel
{
    public FallbackKeyViewModel(CredentialAssignmentViewModel owner, KeyOption key)
    {
        Key = key;
        RemoveCommand = new AsyncRelayCommand(() => owner.RemoveFallbackAsync(key.Name));
        MoveUpCommand = new AsyncRelayCommand(() => owner.MoveFallbackAsync(key.Name, -1));
        MoveDownCommand = new AsyncRelayCommand(() => owner.MoveFallbackAsync(key.Name, +1));
    }

    public KeyOption Key { get; }
    public string Name => Key.Name;
    public MaStatus? Status => Key.Status;
    public string StatusLabel => Key.StatusLabel;
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
}

/// <summary>
/// The "Keys used" row for one provider an app declared with <c>[UsesCredential]</c>: its Active key and ordered
/// fallbacks, chosen by name from the provider's shared list. Writes only names, through
/// <see cref="IKeyBackend.SetAssignmentAsync"/>; "Use shared default" clears the assignment.
/// </summary>
public sealed class CredentialAssignmentViewModel : ObservableObject
{
    private readonly string appId;
    private readonly DesktopContext context;
    private IKeyBackend? keys;
    private KeyOption? active;
    private KeyOption? fallbackToAdd;
    private bool loading;
    private string summary = "";

    public CredentialAssignmentViewModel(string appId, CredentialUse use, DesktopContext context)
    {
        this.appId = appId;
        this.context = context;
        Use = use;
        ProviderId = ProviderCatalog.Normalize(use.ProviderId);
        UseSharedDefaultCommand = new AsyncRelayCommand(UseSharedDefaultAsync, () => !UsesSharedDefault);
        AddFallbackCommand = new AsyncRelayCommand(() => FallbackToAdd is { } k ? AddFallbackAsync(k.Name) : Task.CompletedTask,
            () => Active is not null && FallbackToAdd is not null);
    }

    /// <summary>Raised after an assignment is written: provider id and the names (empty = shared default).</summary>
    public event Action<string, IReadOnlyList<string>>? Assigned;

    public CredentialUse Use { get; }
    public string ProviderId { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(Use.DisplayName) ? ProviderCatalog.Get(ProviderId).DisplayName : Use.DisplayName;
    public string Caption => (Use.Required ? "Required" : "Optional") + (string.IsNullOrWhiteSpace(Use.Purpose) ? "" : " · " + Use.Purpose);

    /// <summary>The provider's keys, each with its health light.</summary>
    public ObservableCollection<KeyOption> Options { get; } = new();

    /// <summary>Fallbacks after the active key, in order.</summary>
    public ObservableCollection<FallbackKeyViewModel> Fallbacks { get; } = new();

    /// <summary>The app's active key; null means it uses the shared list in its default order.</summary>
    public KeyOption? Active
    {
        get => active;
        set
        {
            if (loading)
            {
                Set(ref active, value);
                return;
            }
            if (Equals(active, value)) return;
            _ = SetActiveAsync(value?.Name);
        }
    }

    /// <summary>The key the Add fallback button adds.</summary>
    public KeyOption? FallbackToAdd
    {
        get => fallbackToAdd;
        set => Set(ref fallbackToAdd, value);
    }

    public bool UsesSharedDefault => Active is null;

    /// <summary>What the app will try, in words (names only).</summary>
    public string Summary
    {
        get => summary;
        private set => Set(ref summary, value);
    }

    public ICommand UseSharedDefaultCommand { get; }
    public ICommand AddFallbackCommand { get; }

    /// <summary>The assigned names, active first.</summary>
    public IReadOnlyList<string> AssignedNames =>
        Active is null ? Array.Empty<string>() : new[] { Active.Name }.Concat(Fallbacks.Select(f => f.Name)).ToList();

    public async Task LoadAsync(IKeyBackend keyBackend)
    {
        keys = keyBackend;
        var all = await keyBackend.GetKeysAsync(ProviderId).ConfigureAwait(true);
        var assigned = await keyBackend.GetAssignmentAsync(appId, ProviderId).ConfigureAwait(true);
        Apply(all, assigned);
    }

    public Task SetActiveAsync(string? name)
    {
        if (name is null) return WriteAsync(Array.Empty<string>());
        return WriteAsync(new[] { name }.Concat(Fallbacks.Select(f => f.Name)
            .Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase))).ToList());
    }

    public Task AddFallbackAsync(string name)
    {
        if (Active is null || AssignedNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return Task.CompletedTask;
        return WriteAsync(AssignedNames.Append(name).ToList());
    }

    public Task RemoveFallbackAsync(string name) =>
        WriteAsync(AssignedNames.Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase) || n == Active?.Name).ToList());

    public Task MoveFallbackAsync(string name, int delta)
    {
        var fallbacks = Fallbacks.Select(f => f.Name).ToList();
        var i = fallbacks.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        var j = i + delta;
        if (i < 0 || j < 0 || j >= fallbacks.Count) return Task.CompletedTask;
        (fallbacks[i], fallbacks[j]) = (fallbacks[j], fallbacks[i]);
        return WriteAsync(new[] { Active!.Name }.Concat(fallbacks).ToList());
    }

    public Task UseSharedDefaultAsync() => WriteAsync(Array.Empty<string>());

    private async Task WriteAsync(IReadOnlyList<string> names)
    {
        if (keys is null) return;
        try
        {
            await keys.SetAssignmentAsync(appId, ProviderId, names).ConfigureAwait(true);
        }
        catch (ProfileAccessDeniedException ex)
        {
            context.Dialogs.Error(ex.Message, "Access denied");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            DesktopLog.For("Keys").Warning("Assigning {Provider} keys to {App} failed: {Type}", ProviderId, appId, ex.GetType().Name);
            context.Dialogs.Error($"Could not save {appId}'s {DisplayName} keys: {ex.Message}", "Keys used");
        }
        finally
        {
            await LoadAsync(keys).ConfigureAwait(true);
        }
        if (AssignedNames.SequenceEqual(names, StringComparer.OrdinalIgnoreCase)) Assigned?.Invoke(ProviderId, names);
    }

    private void Apply(IReadOnlyList<NamedKey> all, IReadOnlyList<string> assigned)
    {
        var now = context.Time.GetUtcNow();
        KeyOption Option(NamedKey k)
        {
            var status = context.Health.Get(ProviderId, k.Name).StatusAt(now, k.Disabled);
            return new KeyOption(k.Name, KeyStatusDisplay.Light(status), KeyStatusDisplay.Label(status));
        }
        KeyOption ByName(string name) =>
            all.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)) is { } key
                ? Option(key)
                : new KeyOption(name, MaStatus.Bad, "Missing");

        var chosen = assigned.Select(ByName).ToList();
        loading = true;
        try
        {
            Options.SyncBy(all.Select(Option), o => o, o => o, o => o, (_, _) => { });
            Active = chosen.FirstOrDefault();
            Fallbacks.Clear();
            foreach (var f in chosen.Skip(1)) Fallbacks.Add(new FallbackKeyViewModel(this, f));
            if (FallbackToAdd is not null && !Options.Contains(FallbackToAdd)) FallbackToAdd = null;
        }
        finally
        {
            loading = false;
        }

        Summary = chosen.Count > 0
            ? "Uses " + string.Join(", then ", chosen.Select(c => c.Name)) + "."
            : all.Count == 0
                ? $"No {DisplayName} key yet. Add one on the Keys tab."
                : "Shared default: " + string.Join(", then ", all.Where(k => !k.Disabled).Select(k => k.Name)) + ".";
        Raise(nameof(UsesSharedDefault));
        Raise(nameof(AssignedNames));
    }
}
