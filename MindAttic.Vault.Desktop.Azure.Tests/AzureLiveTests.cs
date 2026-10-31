using System.Text.Json.Nodes;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Azure.Keys;
using MindAttic.Vault.Desktop.Azure.Resources;
using MindAttic.Vault.Desktop.Azure.Settings;
using MindAttic.Vault.Desktop.Profiles;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Azure.Tests;

/// <summary>
/// Against a real throwaway subscription. Runs only when all of
/// <c>MINDATTIC_VAULT_AZURE_TEST_SUBSCRIPTION</c> (subscription id), <c>MINDATTIC_VAULT_AZURE_TEST_KEYVAULT</c>
/// (vault name or URI) and <c>MINDATTIC_VAULT_AZURE_TEST_APPCONFIG</c> (store name or endpoint) are set; signs in
/// with <see cref="DefaultAzureCredential"/> (e.g. <c>az login</c>). The identity needs Reader on the subscription,
/// Key Vault Secrets Officer on the vault and App Configuration Data Owner on the store. Everything it writes uses a
/// unique app id / key names and is removed afterwards (secrets are soft-deleted, not purged).
/// </summary>
[TestFixture]
[Category("AzureLive")]
public sealed class AzureLiveTests
{
    private string subscription = "";
    private Uri vaultUri = null!;
    private Uri storeEndpoint = null!;
    private AzureClientFactory clients = null!;
    private readonly string run = Guid.NewGuid().ToString("N")[..8];

    [OneTimeSetUp]
    public void SetUp()
    {
        var sub = Environment.GetEnvironmentVariable("MINDATTIC_VAULT_AZURE_TEST_SUBSCRIPTION");
        var vault = Environment.GetEnvironmentVariable("MINDATTIC_VAULT_AZURE_TEST_KEYVAULT");
        var store = Environment.GetEnvironmentVariable("MINDATTIC_VAULT_AZURE_TEST_APPCONFIG");
        if (string.IsNullOrWhiteSpace(sub) || string.IsNullOrWhiteSpace(vault) || string.IsNullOrWhiteSpace(store))
            Assert.Ignore("Set MINDATTIC_VAULT_AZURE_TEST_SUBSCRIPTION, _KEYVAULT and _APPCONFIG to run the live Azure tests.");

        subscription = sub!.Trim();
        vaultUri = vault!.Contains("://") ? new Uri(vault) : new Uri($"https://{vault.Trim()}.vault.azure.net/");
        storeEndpoint = store!.Contains("://") ? new Uri(store) : new Uri($"https://{store.Trim()}.azconfig.io");
        var credential = new DefaultAzureCredential();
        clients = new AzureClientFactory(() => credential);
    }

    [Test]
    public async Task Picker_SeesTheSubscription()
    {
        var catalog = new ArmResourceCatalog(clients);
        var subs = await catalog.ListSubscriptionsAsync();
        Assert.That(subs.Select(s => s.SubscriptionId), Does.Contain(subscription));
        Assert.That(await catalog.ListResourceGroupsAsync(subscription), Is.Not.Empty);
    }

    [Test]
    public async Task Settings_RoundTrip_AndCleanUp()
    {
        var manifest = Harness.Manifest;
        manifest.AppId = $"VaultDesktopLive{run}";
        manifest.ConfigurationPrefix = $"MindAttic:Settings:{manifest.AppId}";
        var backend = new AppConfigurationSettingsBackend(clients.CreateConfigurationClient(storeEndpoint), storeEndpoint.Host);
        var all = manifest.Fields.Select(f => SettingsKeyMap.PathOf(manifest, f)).ToArray();
        try
        {
            var doc = await backend.LoadAsync(manifest);
            doc.Values["count"] = 77;
            doc.Values["endpoints"]!.AsArray().Add(new JsonObject { ["name"] = "live", ["port"] = 1, ["secure"] = false, ["tags"] = new JsonArray() });
            await backend.SaveAsync(manifest, doc, all);

            var reloaded = await new AppConfigurationSettingsBackend(clients.CreateConfigurationClient(storeEndpoint), storeEndpoint.Host).LoadAsync(manifest);
            Assert.That(JsonNode.DeepEquals(reloaded.Values, doc.Values), Is.True, reloaded.Values.ToJsonString());
        }
        finally
        {
            var client = clients.CreateConfigurationClient(storeEndpoint);
            await foreach (var s in client.GetConfigurationSettingsAsync(new global::Azure.Data.AppConfiguration.SettingSelector { KeyFilter = manifest.ConfigurationPrefix + ":*" }))
                await client.DeleteConfigurationSettingAsync(s.Key, s.Label);
        }
    }

    [Test]
    public async Task Keys_AddRenameAssignRemove_AndConfigurationKeySourceAgrees()
    {
        var provider = $"livetest{run}";
        var app = $"VaultDesktopLive{run}";
        var keys = new KeyVaultKeyBackend(clients.CreateConfigurationClient(storeEndpoint), clients.CreateSecretClient(vaultUri), storeEndpoint.Host, vaultUri.Host);
        var value = "live-" + Guid.NewGuid().ToString("N");
        try
        {
            await keys.AddAsync(provider, "first key", value);
            await keys.SetValueAsync(provider, "first key", value + "-2");
            await keys.RenameAsync(provider, "first key", "renamed key");
            await keys.SetAssignmentAsync(app, provider, new[] { "renamed key" });
            Assert.ThrowsAsync<KeyInUseException>(() => keys.RemoveAsync(provider, "renamed key"));

            var listed = await keys.GetKeysAsync(provider);
            Assert.That(listed, Is.EqualTo(new[] { new NamedKey("renamed key", value + "-2") }));

            // What a deployed app would read (Key Vault references resolved).
            var client = clients.CreateConfigurationClient(storeEndpoint);
            var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prefix in new[] { LampNames.ProviderSection(provider), LampNames.Assignment(app, provider) })
                await foreach (var s in client.GetConfigurationSettingsAsync(new global::Azure.Data.AppConfiguration.SettingSelector { KeyFilter = prefix + ":*" }))
                    data[s.Key] = KeyVaultReference.TryParse(s) is { } r
                        ? (await clients.CreateSecretClient(vaultUri).GetSecretAsync(r.SecretName)).Value.Value
                        : s.Value;
            var source = new ConfigurationKeySource(new ConfigurationBuilder().AddInMemoryCollection(data).Build());
            Assert.That(source.GetKeys(provider), Is.EqualTo(listed));
            Assert.That(source.GetAssignment(app, provider), Is.EqualTo(new[] { "renamed key" }));
        }
        finally
        {
            try { await keys.SetAssignmentAsync(app, provider, Array.Empty<string>()); } catch (ProfileAccessDeniedException) { }
            foreach (var k in await keys.GetKeysAsync(provider))
                await keys.RemoveAsync(provider, k.Name);
        }
    }
}
