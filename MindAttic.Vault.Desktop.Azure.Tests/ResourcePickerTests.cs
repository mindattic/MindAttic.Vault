using System.Net;
using System.Text.Json.Nodes;
using MindAttic.Vault.Desktop.Azure.Environments;
using MindAttic.Vault.Desktop.Azure.Resources;
using MindAttic.Vault.Desktop.Azure.Setup;
using MindAttic.Vault.Desktop.Azure.Tests.Fakes;
using MindAttic.Vault.Desktop.Profiles;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Azure.Tests;

[TestFixture]
public sealed class ResourcePickerTests
{
    private const string SubA = "11111111-1111-1111-1111-111111111111";
    private const string SubB = "22222222-2222-2222-2222-222222222222";
    private const string SubC = "33333333-3333-3333-3333-333333333333";

    private static FakeAzure Populated()
    {
        var fake = new FakeAzure { ArmPageSize = 1 };
        fake.Subscriptions.AddRange(new[] { (SubB, "Beta"), (SubA, "alpha"), (SubC, "Gamma") });
        fake.ResourceGroups[SubA] = new() { "rg-prod", "rg-dev", "rg-empty" };
        fake.KeyVaults[$"{SubA}/rg-prod"] = new() { "kv-prod-2", "kv-prod" };
        fake.AppConfigurationStores[$"{SubA}/rg-prod"] = new() { "ac-prod" };
        fake.KeyVaults[$"{SubA}/rg-dev"] = new() { "kv-dev" };
        fake.AppConfigurationStores[$"{SubA}/rg-dev"] = new() { "ac-dev" };
        return fake;
    }

    [Test]
    public async Task Catalog_FollowsEveryPage_AtEveryLevel()
    {
        var fake = Populated();
        var catalog = new ArmResourceCatalog(fake.Clients);

        var subs = await catalog.ListSubscriptionsAsync();
        var groups = await catalog.ListResourceGroupsAsync(SubA);
        var vaults = await catalog.ListKeyVaultsAsync(SubA, "rg-prod");
        var stores = await catalog.ListAppConfigurationStoresAsync(SubA, "rg-prod");

        Assert.Multiple(() =>
        {
            Assert.That(subs.Select(s => s.DisplayName), Is.EqualTo(new[] { "alpha", "Beta", "Gamma" }));
            Assert.That(subs[0].SubscriptionId, Is.EqualTo(SubA));
            Assert.That(groups.Select(g => g.Name), Is.EqualTo(new[] { "rg-dev", "rg-empty", "rg-prod" }));
            Assert.That(vaults.Select(v => v.Name), Is.EqualTo(new[] { "kv-prod", "kv-prod-2" }));
            Assert.That(vaults[0].Endpoint, Is.EqualTo("https://kv-prod.vault.azure.net/"));
            Assert.That(vaults[0].ResourceId, Is.EqualTo($"/subscriptions/{SubA}/resourceGroups/rg-prod/providers/Microsoft.KeyVault/vaults/kv-prod"));
            Assert.That(stores.Single().Endpoint, Is.EqualTo("https://ac-prod.azconfig.io"));
        });

        var armGets = fake.Handler.Requests.Where(r => r.Uri.Host == FakeAzure.ArmHost).ToList();
        Assert.That(armGets.Count(r => r.Query("$skiptoken") is not null), Is.EqualTo(2 + 2 + 1), "every nextLink was followed");
        Assert.That(armGets.All(r => r.Header("Authorization") == "Bearer fake-token"), Is.True);
    }

    [Test]
    public async Task Catalog_EmptyResourceGroup_ListsNothing()
    {
        var catalog = new ArmResourceCatalog(Populated().Clients);
        Assert.That(await catalog.ListKeyVaultsAsync(SubA, "rg-empty"), Is.Empty);
        Assert.That(await catalog.ListAppConfigurationStoresAsync(SubA, "rg-empty"), Is.Empty);
    }

    [Test]
    public void Catalog_Forbidden_IsAPlainRoleMessage()
    {
        var fake = Populated();
        fake.Handler.Route(r => r.Uri.Host == FakeAzure.ArmHost
            ? FakeAzure.Json(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"AuthorizationFailed\",\"message\":\"no\"}}")
            : null);
        var ex = Assert.ThrowsAsync<ProfileAccessDeniedException>(() => new ArmResourceCatalog(fake.Clients).ListResourceGroupsAsync(SubA));
        Assert.That(ex!.Message, Does.Contain("'Reader' role"));
    }

    [Test]
    public async Task ViewModel_SignIn_Pick_Save_RemembersTheEnvironment()
    {
        using var temp = new TempDirectory();
        var fake = Populated();
        var session = new FakeSession();
        var store = new AzureEnvironmentStore(temp.Path);
        var vm = new AzureSetupViewModel(session, new ArmResourceCatalog(fake.Clients), store);
        Assert.That(vm.IsSignedIn, Is.False);
        Assert.That(vm.SignedInAs, Is.EqualTo("Not signed in"));

        await vm.SignInAsync();
        Assert.That(vm.SignedInAs, Is.EqualTo("Signed in as ryan@contoso.com"));
        Assert.That(vm.Subscriptions.Select(s => s.DisplayName), Is.EqualTo(new[] { "alpha", "Beta", "Gamma" }));
        Assert.That(vm.SelectedSubscription, Is.Null, "several subscriptions: the user picks");

        vm.SelectedSubscription = vm.Subscriptions[0];
        await vm.PendingLoad;
        Assert.That(vm.ResourceGroups.Select(g => g.Name), Is.EqualTo(new[] { "rg-dev", "rg-empty", "rg-prod" }));

        vm.SelectedResourceGroup = vm.ResourceGroups.Single(g => g.Name == "rg-prod");
        await vm.PendingLoad;
        Assert.That(vm.KeyVaults.Select(v => v.Name), Is.EqualTo(new[] { "kv-prod", "kv-prod-2" }));
        Assert.That(vm.SelectedKeyVault, Is.Null, "two vaults: the user picks");
        Assert.That(vm.SelectedAppConfigurationStore?.Name, Is.EqualTo("ac-prod"), "the only store is preselected");
        Assert.That(vm.CanSaveEnvironment, Is.False);

        vm.SelectedKeyVault = vm.KeyVaults[0];
        vm.EnvironmentName = "Production";
        Assert.That(vm.CanSaveEnvironment, Is.True);
        var saved = vm.SaveEnvironment();

        Assert.That(saved, Is.Not.Null);
        Assert.That(vm.Environments.Select(e => e.Name), Is.EqualTo(new[] { "Production" }));
        Assert.That(vm.SelectedEnvironment?.Name, Is.EqualTo("Production"));
        Assert.That(vm.Error, Is.Null);

        // Remembered on disk: resource ids and URIs only.
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(temp.Path, "azure-environments.json")))!;
        var env = json["environments"]![0]!;
        Assert.Multiple(() =>
        {
            Assert.That(json["selected"]!.GetValue<string>(), Is.EqualTo("Production"));
            Assert.That(env["keyVaultUri"]!.GetValue<string>(), Is.EqualTo("https://kv-prod.vault.azure.net/"));
            Assert.That(env["appConfigurationEndpoint"]!.GetValue<string>(), Is.EqualTo("https://ac-prod.azconfig.io"));
            Assert.That(env["subscriptionId"]!.GetValue<string>(), Is.EqualTo(SubA));
            Assert.That(env.AsObject().Select(p => p.Key), Has.None.Contains("token").And.None.Contains("secret"));
        });

        // A new view model (next start) preselects the remembered pick.
        var again = new AzureSetupViewModel(session, new ArmResourceCatalog(fake.Clients), new AzureEnvironmentStore(temp.Path));
        await again.LoadSubscriptionsAsync();
        Assert.That(again.SelectedSubscription?.SubscriptionId, Is.EqualTo(SubA));
        Assert.That(again.SelectedResourceGroup?.Name, Is.EqualTo("rg-prod"));
        Assert.That(again.SelectedKeyVault?.Name, Is.EqualTo("kv-prod"));
        Assert.That(again.SelectedAppConfigurationStore?.Name, Is.EqualTo("ac-prod"));
        Assert.That(again.EnvironmentName, Is.EqualTo("Production"));
        Assert.That(again.IsBusy, Is.False);
    }

    [Test]
    public async Task ViewModel_SingleChoices_AreSelectedAutomatically()
    {
        using var temp = new TempDirectory();
        var fake = new FakeAzure();
        fake.Subscriptions.Add((SubA, "only"));
        fake.ResourceGroups[SubA] = new() { "rg" };
        fake.KeyVaults[$"{SubA}/rg"] = new() { "kv" };
        fake.AppConfigurationStores[$"{SubA}/rg"] = new() { "ac" };
        var vm = new AzureSetupViewModel(new FakeSession(), new ArmResourceCatalog(fake.Clients), new AzureEnvironmentStore(temp.Path));

        await vm.SignInAsync();

        Assert.That(vm.SelectedKeyVault?.Name, Is.EqualTo("kv"));
        Assert.That(vm.SelectedAppConfigurationStore?.Name, Is.EqualTo("ac"));
        Assert.That(vm.EnvironmentName, Is.EqualTo("rg"));
        Assert.That(vm.CanSaveEnvironment, Is.True);
    }

    [Test]
    public async Task ViewModel_Forbidden_ShowsTheRoleMessage_NotAStackTrace()
    {
        using var temp = new TempDirectory();
        var fake = Populated();
        fake.Handler.Route(r => r.Uri.Host == FakeAzure.ArmHost && r.Uri.AbsolutePath.Contains("resourcegroups", StringComparison.OrdinalIgnoreCase)
            ? FakeAzure.Json(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"AuthorizationFailed\",\"message\":\"no\"}}")
            : null);
        var vm = new AzureSetupViewModel(new FakeSession(), new ArmResourceCatalog(fake.Clients), new AzureEnvironmentStore(temp.Path));
        await vm.SignInAsync();

        vm.SelectedSubscription = vm.Subscriptions[0];
        await vm.PendingLoad;

        Assert.That(vm.Error, Does.Contain("'Reader' role"));
        Assert.That(vm.Error, Does.Not.Contain("   at "));
        Assert.That(vm.IsBusy, Is.False);
    }

    [Test]
    public async Task ViewModel_SignInCancelled_SaysSo()
    {
        using var temp = new TempDirectory();
        var session = new FakeSession { SignInFailure = new global::Azure.Identity.AuthenticationFailedException("user closed the browser") };
        var vm = new AzureSetupViewModel(session, new ArmResourceCatalog(Populated().Clients), new AzureEnvironmentStore(temp.Path));

        await vm.SignInAsync();

        Assert.That(vm.IsSignedIn, Is.False);
        Assert.That(vm.Error, Is.EqualTo("Sign-in didn't complete. Try again."));
        Assert.That(vm.Subscriptions, Is.Empty);
    }

    [Test]
    public async Task ViewModel_SignOut_ClearsThePicker()
    {
        using var temp = new TempDirectory();
        var session = new FakeSession();
        var vm = new AzureSetupViewModel(session, new ArmResourceCatalog(Populated().Clients), new AzureEnvironmentStore(temp.Path));
        await vm.SignInAsync();

        vm.SignOut();

        Assert.That(vm.IsSignedIn, Is.False);
        Assert.That(vm.Subscriptions, Is.Empty);
        Assert.That(vm.SelectedSubscription, Is.Null);
    }

    [Test]
    public async Task ViewModel_SelectAndRemoveEnvironments()
    {
        using var temp = new TempDirectory();
        var store = new AzureEnvironmentStore(temp.Path);
        store.Save(Env("Dev"));
        store.Save(Env("Prod"));
        var vm = new AzureSetupViewModel(new FakeSession(), new ArmResourceCatalog(Populated().Clients), store);
        Assert.That(vm.SelectedEnvironment?.Name, Is.EqualTo("Prod"));

        vm.SelectedEnvironment = vm.Environments.Single(e => e.Name == "Dev");
        Assert.That(store.Current()?.Name, Is.EqualTo("Dev"));

        vm.RemoveEnvironmentCommand.Execute(null);
        Assert.That(vm.Environments.Select(e => e.Name), Is.EqualTo(new[] { "Prod" }));
        Assert.That(store.Current()?.Name, Is.EqualTo("Prod"));
        await Task.CompletedTask;
    }

    internal static AzureEnvironment Env(string name, string vaultHost = "kv.vault.azure.net", string storeHost = "ac.azconfig.io") => new()
    {
        Name = name,
        SubscriptionId = SubA,
        KeyVaultName = vaultHost.Split('.')[0],
        KeyVaultResourceId = $"/subscriptions/{SubA}/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/{vaultHost.Split('.')[0]}",
        KeyVaultUri = $"https://{vaultHost}/",
        AppConfigurationName = storeHost.Split('.')[0],
        AppConfigurationResourceId = $"/subscriptions/{SubA}/resourceGroups/rg/providers/Microsoft.AppConfiguration/configurationStores/{storeHost.Split('.')[0]}",
        AppConfigurationEndpoint = $"https://{storeHost}",
    };
}
