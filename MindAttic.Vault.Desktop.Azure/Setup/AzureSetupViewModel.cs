using System.Collections.ObjectModel;
using System.Windows.Input;
using Azure;
using Azure.Identity;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Vault.Desktop.Azure.Environments;
using MindAttic.Vault.Desktop.Azure.Identity;
using MindAttic.Vault.Desktop.Azure.Resources;
using MindAttic.Vault.Desktop.Profiles;

namespace MindAttic.Vault.Desktop.Azure.Setup;

/// <summary>
/// Sign-in and the resource picker for the Azure Profile: subscription → resource group → Key Vault + App
/// Configuration store, saved under a name as an <see cref="AzureEnvironment"/>. Selecting a level loads the next
/// (<see cref="PendingLoad"/> is the load in flight, for tests and busy indicators).
/// </summary>
public sealed class AzureSetupViewModel : ObservableObject
{
    private readonly IAzureSession session;
    private readonly IAzureResourceCatalog catalog;
    private readonly AzureEnvironmentStore store;

    private AzureSubscriptionInfo? selectedSubscription;
    private AzureResourceGroupInfo? selectedResourceGroup;
    private AzureResourceInfo? selectedKeyVault;
    private AzureResourceInfo? selectedAppConfigurationStore;
    private AzureEnvironment? selectedEnvironment;
    private string environmentName = "";
    private bool isBusy;
    private string? status;
    private string? error;
    private int loadGeneration;

    public AzureSetupViewModel(IAzureSession session, IAzureResourceCatalog catalog, AzureEnvironmentStore store)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.store = store ?? throw new ArgumentNullException(nameof(store));

        SignInCommand = new AsyncRelayCommand(SignInAsync, () => !IsBusy);
        SignOutCommand = new RelayCommand(SignOut, () => IsSignedIn && !IsBusy);
        RefreshCommand = new AsyncRelayCommand(LoadSubscriptionsAsync, () => IsSignedIn && !IsBusy);
        SaveEnvironmentCommand = new RelayCommand(() => SaveEnvironment(), () => CanSaveEnvironment);
        RemoveEnvironmentCommand = new RelayCommand(RemoveSelectedEnvironment, () => SelectedEnvironment is not null);

        session.Changed += (_, _) => RaiseSession();
        ReloadEnvironments();
    }

    public ObservableCollection<AzureSubscriptionInfo> Subscriptions { get; } = new();
    public ObservableCollection<AzureResourceGroupInfo> ResourceGroups { get; } = new();
    public ObservableCollection<AzureResourceInfo> KeyVaults { get; } = new();
    public ObservableCollection<AzureResourceInfo> AppConfigurationStores { get; } = new();
    public ObservableCollection<AzureEnvironment> Environments { get; } = new();

    public ICommand SignInCommand { get; }
    public ICommand SignOutCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SaveEnvironmentCommand { get; }
    public ICommand RemoveEnvironmentCommand { get; }

    /// <summary>The load started by the last selection (completed when nothing is loading).</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    public bool IsSignedIn => session.IsSignedIn;
    public string SignedInAs => session.UserPrincipalName is { } upn ? $"Signed in as {upn}" : "Not signed in";

    public bool IsBusy
    {
        get => isBusy;
        private set { if (Set(ref isBusy, value)) Requery(); }
    }

    /// <summary>Plain-words progress or result.</summary>
    public string? Status { get => status; private set => Set(ref status, value); }

    /// <summary>Plain-words failure (role needed, sign-in cancelled); never a stack trace or a value.</summary>
    public string? Error { get => error; private set => Set(ref error, value); }

    public AzureSubscriptionInfo? SelectedSubscription
    {
        get => selectedSubscription;
        set
        {
            if (!Set(ref selectedSubscription, value)) return;
            PendingLoad = LoadResourceGroupsAsync();
        }
    }

    public AzureResourceGroupInfo? SelectedResourceGroup
    {
        get => selectedResourceGroup;
        set
        {
            if (!Set(ref selectedResourceGroup, value)) return;
            PendingLoad = LoadResourcesAsync();
        }
    }

    public AzureResourceInfo? SelectedKeyVault
    {
        get => selectedKeyVault;
        set { if (Set(ref selectedKeyVault, value)) Requery(); }
    }

    public AzureResourceInfo? SelectedAppConfigurationStore
    {
        get => selectedAppConfigurationStore;
        set { if (Set(ref selectedAppConfigurationStore, value)) Requery(); }
    }

    /// <summary>The name to save the picked pair under (e.g. <c>Production</c>).</summary>
    public string EnvironmentName
    {
        get => environmentName;
        set { if (Set(ref environmentName, value ?? "")) Requery(); }
    }

    /// <summary>The environment the Azure Profile uses; choosing one here selects it.</summary>
    public AzureEnvironment? SelectedEnvironment
    {
        get => selectedEnvironment;
        set
        {
            if (!Set(ref selectedEnvironment, value)) return;
            if (value is not null && !string.Equals(store.Current()?.Name, value.Name, StringComparison.OrdinalIgnoreCase))
                store.Select(value.Name);
            Requery();
        }
    }

    public bool CanSaveEnvironment =>
        !IsBusy && !string.IsNullOrWhiteSpace(EnvironmentName) && SelectedKeyVault is not null && SelectedAppConfigurationStore is not null
        && SelectedSubscription is not null;

    public async Task SignInAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            Status = "Waiting for you to sign in in the browser…";
            await session.SignInAsync().ConfigureAwait(true);
            RaiseSession();
            Status = SignedInAs;
        }
        catch (Exception ex) when (ex is AuthenticationFailedException or OperationCanceledException or RequestFailedException)
        {
            Status = null;
            Error = ex is OperationCanceledException ? "Sign-in was cancelled." : "Sign-in didn't complete. Try again.";
            return;
        }
        finally { IsBusy = false; }
        await LoadSubscriptionsAsync().ConfigureAwait(true);
    }

    public void SignOut()
    {
        session.SignOut();
        Subscriptions.Clear();
        ResourceGroups.Clear();
        KeyVaults.Clear();
        AppConfigurationStores.Clear();
        selectedSubscription = null;
        selectedResourceGroup = null;
        selectedKeyVault = null;
        selectedAppConfigurationStore = null;
        Raise(nameof(SelectedSubscription));
        Raise(nameof(SelectedResourceGroup));
        Raise(nameof(SelectedKeyVault));
        Raise(nameof(SelectedAppConfigurationStore));
        RaiseSession();
        Status = "Signed out.";
    }

    public Task LoadSubscriptionsAsync() => Load("Loading subscriptions…", async () =>
    {
        var subs = await catalog.ListSubscriptionsAsync().ConfigureAwait(true);
        Replace(Subscriptions, subs);
        var current = store.Current();
        SelectedSubscription = subs.FirstOrDefault(s => s.SubscriptionId == current?.SubscriptionId) ?? (subs.Count == 1 ? subs[0] : null);
        await PendingLoad.ConfigureAwait(true);
        return subs.Count == 0 ? "No subscriptions are visible to this account." : $"{subs.Count} subscription(s).";
    });

    private Task LoadResourceGroupsAsync() => Load("Loading resource groups…", async () =>
    {
        ResourceGroups.Clear();
        KeyVaults.Clear();
        AppConfigurationStores.Clear();
        if (SelectedSubscription is not { } sub) return null;
        var groups = await catalog.ListResourceGroupsAsync(sub.SubscriptionId).ConfigureAwait(true);
        if (!ReferenceEquals(sub, SelectedSubscription)) return null;
        Replace(ResourceGroups, groups);
        var current = store.Current();
        var match = current is null ? null : groups.FirstOrDefault(g => ResourceGroupOf(current.KeyVaultResourceId) is { } rg && string.Equals(rg, g.Name, StringComparison.OrdinalIgnoreCase));
        SelectedResourceGroup = match ?? (groups.Count == 1 ? groups[0] : null);
        await PendingLoad.ConfigureAwait(true);
        return $"{groups.Count} resource group(s) in {sub.DisplayName}.";
    });

    private Task LoadResourcesAsync() => Load("Loading Key Vaults and App Configuration stores…", async () =>
    {
        KeyVaults.Clear();
        AppConfigurationStores.Clear();
        if (SelectedSubscription is not { } sub || SelectedResourceGroup is not { } rg) return null;
        var vaults = await catalog.ListKeyVaultsAsync(sub.SubscriptionId, rg.Name).ConfigureAwait(true);
        var stores = await catalog.ListAppConfigurationStoresAsync(sub.SubscriptionId, rg.Name).ConfigureAwait(true);
        if (!ReferenceEquals(rg, SelectedResourceGroup)) return null;
        Replace(KeyVaults, vaults);
        Replace(AppConfigurationStores, stores);
        var current = store.Current();
        SelectedKeyVault = vaults.FirstOrDefault(v => string.Equals(v.ResourceId, current?.KeyVaultResourceId, StringComparison.OrdinalIgnoreCase))
                           ?? (vaults.Count == 1 ? vaults[0] : null);
        SelectedAppConfigurationStore = stores.FirstOrDefault(s => string.Equals(s.ResourceId, current?.AppConfigurationResourceId, StringComparison.OrdinalIgnoreCase))
                                        ?? (stores.Count == 1 ? stores[0] : null);
        if (string.IsNullOrWhiteSpace(EnvironmentName)) EnvironmentName = current?.Name ?? rg.Name;
        return $"{vaults.Count} Key Vault(s), {stores.Count} App Configuration store(s) in {rg.Name}.";
    });

    /// <summary>Saves the picked Key Vault + App Configuration store as <see cref="EnvironmentName"/> and selects it.</summary>
    public AzureEnvironment? SaveEnvironment()
    {
        if (!CanSaveEnvironment) return null;
        var env = new AzureEnvironment
        {
            Name = EnvironmentName.Trim(),
            SubscriptionId = SelectedSubscription!.SubscriptionId,
            TenantId = SelectedSubscription.TenantId ?? session.TenantId,
            KeyVaultResourceId = SelectedKeyVault!.ResourceId,
            KeyVaultName = SelectedKeyVault.Name,
            KeyVaultUri = SelectedKeyVault.Endpoint,
            AppConfigurationResourceId = SelectedAppConfigurationStore!.ResourceId,
            AppConfigurationName = SelectedAppConfigurationStore.Name,
            AppConfigurationEndpoint = SelectedAppConfigurationStore.Endpoint,
        };
        try
        {
            store.Save(env);
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
            return null;
        }
        Error = null;
        Status = $"Saved environment '{env.Name}'. The Azure Profile now uses it.";
        ReloadEnvironments();
        return env;
    }

    private void RemoveSelectedEnvironment()
    {
        if (SelectedEnvironment is not { } env) return;
        store.Remove(env.Name);
        Status = $"Removed environment '{env.Name}'.";
        ReloadEnvironments();
    }

    private void ReloadEnvironments()
    {
        Replace(Environments, store.All());
        var current = store.Current();
        selectedEnvironment = current is null ? null : Environments.FirstOrDefault(e => string.Equals(e.Name, current.Name, StringComparison.OrdinalIgnoreCase));
        Raise(nameof(SelectedEnvironment));
        Requery();
    }

    private async Task Load(string progress, Func<Task<string?>> action)
    {
        var generation = ++loadGeneration;
        Error = null;
        IsBusy = true;
        Status = progress;
        try
        {
            var done = await action().ConfigureAwait(true);
            if (generation == loadGeneration && done is not null) Status = done;
        }
        catch (ProfileAccessDeniedException ex)
        {
            Error = ex.Message;
            Status = null;
        }
        catch (RequestFailedException ex)
        {
            Error = $"Azure returned an error ({ex.Status}{(string.IsNullOrEmpty(ex.ErrorCode) ? "" : ", " + ex.ErrorCode)}).";
            Status = null;
        }
        catch (AuthenticationFailedException)
        {
            Error = "Your Azure sign-in needs renewing. Sign in again.";
            Status = null;
        }
        finally
        {
            if (generation == loadGeneration) IsBusy = false;
        }
    }

    private static string? ResourceGroupOf(string resourceId)
    {
        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.FindIndex(parts, p => string.Equals(p, "resourceGroups", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < parts.Length ? parts[i + 1] : null;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private void RaiseSession()
    {
        Raise(nameof(IsSignedIn));
        Raise(nameof(SignedInAs));
        Requery();
    }

    private void Requery()
    {
        Raise(nameof(CanSaveEnvironment));
        CommandManager.InvalidateRequerySuggested();
        (SignInCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (RefreshCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
    }
}
