using Microsoft.Extensions.DependencyInjection;
using MindAttic.Vault.Desktop.Azure.Environments;
using MindAttic.Vault.Desktop.Azure.Identity;
using MindAttic.Vault.Desktop.Azure.Setup;
using MindAttic.Vault.Desktop.Azure.Tests.Fakes;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Paths;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Azure.Tests;

[TestFixture]
public sealed class AzureProfileTests
{
    [Test]
    public async Task Readiness_NeedsSetupUntilSignedInAndAnEnvironmentIsPicked()
    {
        using var temp = new TempDirectory();
        var session = new FakeSession();
        var store = new AzureEnvironmentStore(temp.Path);
        var profile = new AzureProfile(session, store, new FakeAzure().Clients);

        var notReady = await profile.GetReadinessAsync();
        Assert.That(notReady.IsReady, Is.False);
        Assert.That(notReady.Message, Is.EqualTo("Sign in and pick a Key Vault and App Configuration store"));
        Assert.That(profile.Id, Is.EqualTo("Azure"));
        Assert.That(profile.DisplayName, Is.EqualTo("Azure"));
        Assert.Throws<InvalidOperationException>(() => profile.Settings.LoadAsync(Harness.Manifest));

        await session.SignInAsync();
        Assert.That((await profile.GetReadinessAsync()).IsReady, Is.False, "signed in but nothing picked");

        store.Save(ResourcePickerTests.Env("Production", FakeAzure.VaultHost, FakeAzure.StoreHost));
        Assert.That((await profile.GetReadinessAsync()).IsReady, Is.True);
        Assert.That(profile.DisplayName, Is.EqualTo("Azure (Production)"));
        Assert.That(profile.Actor, Is.EqualTo("ryan@contoso.com"));

        session.SignOut();
        Assert.That((await profile.GetReadinessAsync()).IsReady, Is.False);
        Assert.That(profile.Actor, Is.EqualTo(Environment.UserName));
    }

    [Test]
    public async Task SettingsAndKeys_UseTheSelectedEnvironment()
    {
        using var temp = new TempDirectory();
        var fake = new FakeAzure();
        var session = new FakeSession { IsSignedIn = true, UserPrincipalName = "ops@contoso.com" };
        var store = new AzureEnvironmentStore(temp.Path);
        store.Save(ResourcePickerTests.Env("Production", FakeAzure.VaultHost, FakeAzure.StoreHost));
        var profile = new AzureProfile(session, store, fake.Clients);

        var doc = await profile.Settings.LoadAsync(Harness.Manifest);
        doc.Values["count"] = 11;
        await profile.Settings.SaveAsync(Harness.Manifest, doc, new[] { Harness.Name("count") });
        await profile.Keys.AddAsync("claude", "k", "v");

        Assert.That(fake.Settings[Harness.Name("count")].Value, Is.EqualTo("11"));
        Assert.That((await profile.Keys.GetKeysAsync("claude")).Single().Value, Is.EqualTo("v"));
        Assert.That(profile.Settings.WhenAppsSeeChanges, Is.EqualTo("after the app's App Configuration refresh interval, or its next restart"));

        // Switching to an environment on other hosts switches every call.
        store.Save(ResourcePickerTests.Env("Elsewhere", "other.vault.azure.net", "other.azconfig.io"));
        var hostsBefore = fake.Handler.Requests.Count;
        try { await profile.Settings.LoadAsync(Harness.Manifest); } catch { /* the fake doesn't serve other hosts */ }
        Assert.That(fake.Handler.Requests.Skip(hostsBefore).Select(r => r.Uri.Host).Distinct(), Is.EqualTo(new[] { "other.azconfig.io" }));
    }

    [Test]
    public void AddAzureProfile_RegistersTheProfileAndTheSetupView()
    {
        using var roaming = new TempDirectory();
        using var local = new TempDirectory();
        var previousRoaming = Environment.GetEnvironmentVariable(VaultPaths.RoamingRootEnvVar);
        var previousLocal = Environment.GetEnvironmentVariable(VaultPaths.LocalRootEnvVar);
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, roaming.Path);
        Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, local.Path);
        try
        {
            var services = new ServiceCollection();
            services.AddAzureProfile();
            services.AddAzureProfile(); // idempotent
            using var provider = services.BuildServiceProvider();

            var profiles = provider.GetServices<IProfile>().ToList();
            Assert.That(profiles, Has.Count.EqualTo(1));
            Assert.That(profiles[0], Is.SameAs(provider.GetRequiredService<AzureProfile>()));
            Assert.That(profiles[0].Id, Is.EqualTo("Azure"));
            Assert.That(provider.GetRequiredService<IAzureSession>(), Is.InstanceOf<InteractiveAzureSession>());
            Assert.That(provider.GetRequiredService<IAzureSession>().IsSignedIn, Is.False);
            Assert.That(provider.GetRequiredService<AzureEnvironmentStore>().FilePath, Does.StartWith(roaming.Path));
            Assert.That(provider.GetRequiredService<AzureSetupViewModel>(), Is.Not.Null);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, previousRoaming);
            Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, previousLocal);
        }
    }

    [Test]
    public void AddAzureProfile_KeepsSubstitutesRegisteredFirst()
    {
        using var temp = new TempDirectory();
        var session = new FakeSession();
        var services = new ServiceCollection();
        services.AddSingleton<IAzureSession>(session);
        services.AddSingleton(new AzureEnvironmentStore(temp.Path));
        services.AddAzureProfile();
        using var provider = services.BuildServiceProvider();

        Assert.That(provider.GetRequiredService<IAzureSession>(), Is.SameAs(session));
        Assert.That(provider.GetRequiredService<AzureEnvironmentStore>().FilePath, Does.StartWith(temp.Path));
    }

    [Test]
    public void SetupView_LoadsItsXaml()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var temp = new TempDirectory();
                var view = new AzureSetupView
                {
                    DataContext = new AzureSetupViewModel(new FakeSession(), new Resources.ArmResourceCatalog(new FakeAzure().Clients), new AzureEnvironmentStore(temp.Path)),
                };
                Assert.That(view.Content, Is.Not.Null);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }
}
