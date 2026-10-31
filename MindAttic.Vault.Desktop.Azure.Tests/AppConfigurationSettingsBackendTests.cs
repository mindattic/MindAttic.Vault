using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Desktop.Azure.Settings;
using MindAttic.Vault.Desktop.Azure.Tests.Fakes;
using MindAttic.Vault.Desktop.Profiles;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Azure.Tests;

[TestFixture]
public sealed class AppConfigurationSettingsBackendTests
{
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static JsonObject Defaults() => (JsonObject)JsonSerializer.SerializeToNode(new EveryKindSettings(), Camel)!;

    /// <summary>A document where every field differs from its default.</summary>
    private static JsonObject Edited()
    {
        var s = new EveryKindSettings
        {
            Title = "Bonjour, ça va? : yes",
            Notes = "one\r\ntwo\tthree",
            Enabled = false,
            Count = -42,
            Big = 9007199254740995,
            Price = 12.750m,
            Ratio = 1e-7,
            Mode = TestMode.Fast,
            Color = "green",
            Names = new() { "z", "", "y with space" },
            Folder = @"D:\Other Folder",
            Site = "https://contoso.example/path?q=1&r=2",
            Interval = TimeSpan.FromHours(1.5),
            Start = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Display = new TestDisplay { Theme = "light", Scale = 2.25, Inner = new TestInner { Depth = 4 } },
            Endpoints = new()
            {
                new() { Name = "first", Port = 8080, Secure = false, Tags = new() { "p", "q" } },
                new() { Name = "second", Port = 443 },
                new() { Name = "third", Port = 9, Tags = new() { "r" } },
            },
            Headers = new() { ["Authorization-Mode"] = "none", ["X-Two"] = "2" },
            Numbers = new() { 5, 6, 7 },
            Optional = 7,
            Maybe = "set",
        };
        return (JsonObject)JsonSerializer.SerializeToNode(s, Camel)!;
    }

    private static string[] AllNames() => Harness.Manifest.Fields.Select(f => f.Name).ToArray();

    [Test]
    public async Task Load_EmptyStore_ReturnsManifestDefaults()
    {
        var fake = new FakeAzure();
        var doc = await Harness.Settings(fake).LoadAsync(Harness.Manifest);

        Assert.That(JsonNode.DeepEquals(doc.Values, Defaults()), Is.True, doc.Values.ToJsonString());
        Assert.That(doc.Version, Is.Not.Null.And.StartsWith("appconfig:"));
    }

    [Test]
    public async Task Save_ThenLoad_RoundTripsEveryKindWithJsonTypes()
    {
        var fake = new FakeAzure();
        var manifest = Harness.Manifest;
        var backend = Harness.Settings(fake);
        var loaded = await backend.LoadAsync(manifest);
        var edited = Edited();

        await backend.SaveAsync(manifest, new SettingsDocument(edited, loaded.Version), AllNames());
        var reloaded = await Harness.Settings(fake).LoadAsync(manifest); // a fresh backend: nothing cached
        var v = reloaded.Values;

        Assert.That(JsonNode.DeepEquals(v, edited), Is.True, $"expected {edited.ToJsonString()}\nactual   {v.ToJsonString()}");
        Assert.Multiple(() =>
        {
            Assert.That(v["title"]!.GetValueKind(), Is.EqualTo(JsonValueKind.String));
            Assert.That(v["enabled"]!.GetValueKind(), Is.EqualTo(JsonValueKind.False));
            Assert.That(v["count"]!.GetValueKind(), Is.EqualTo(JsonValueKind.Number));
            Assert.That(v["count"]!.GetValue<int>(), Is.EqualTo(-42));
            Assert.That(v["big"]!.GetValue<long>(), Is.EqualTo(9007199254740995));
            Assert.That(v["price"]!.ToJsonString(), Is.EqualTo("12.750"));
            Assert.That(v["ratio"]!.GetValue<double>(), Is.EqualTo(1e-7));
            Assert.That(v["mode"]!.GetValueKind(), Is.EqualTo(JsonValueKind.Number));
            Assert.That(v["color"]!.GetValueKind(), Is.EqualTo(JsonValueKind.String));
            Assert.That(v["names"], Is.InstanceOf<JsonArray>());
            Assert.That(v["display"]!["scale"]!.GetValueKind(), Is.EqualTo(JsonValueKind.Number));
            Assert.That(v["display"]!["inner"]!["depth"]!.GetValue<int>(), Is.EqualTo(4));
            Assert.That(v["endpoints"]![0]!["secure"]!.GetValueKind(), Is.EqualTo(JsonValueKind.False));
            Assert.That(v["endpoints"]![0]!["port"]!.GetValueKind(), Is.EqualTo(JsonValueKind.Number));
            Assert.That(v["headers"], Is.InstanceOf<JsonObject>());
            Assert.That(v["numbers"]![2]!.GetValueKind(), Is.EqualTo(JsonValueKind.Number));
            Assert.That(v["optional"]!.GetValue<int>(), Is.EqualTo(7));
        });

        // The typed class deserializes from what came back.
        var typed = reloaded.Values.Deserialize<EveryKindSettings>(Camel)!;
        Assert.That(typed.Interval, Is.EqualTo(TimeSpan.FromHours(1.5)));
        Assert.That(typed.Endpoints.Select(e => e.Name), Is.EqualTo(new[] { "first", "second", "third" }));
    }

    [Test]
    public async Task Save_WritesKeysNamedExactlyAsIConfigurationSeesThem()
    {
        var fake = new FakeAzure();
        var manifest = Harness.Manifest;
        var backend = Harness.Settings(fake);
        var loaded = await backend.LoadAsync(manifest);
        await backend.SaveAsync(manifest, new SettingsDocument(Edited(), loaded.Version), AllNames());

        Assert.Multiple(() =>
        {
            Assert.That(fake.Settings[Harness.Name("count")].Value, Is.EqualTo("-42"));
            Assert.That(fake.Settings[Harness.Name("enabled")].Value, Is.EqualTo("false"));
            Assert.That(fake.Settings[Harness.Name("mode")].Value, Is.EqualTo("2"));
            Assert.That(fake.Settings[Harness.Name("names:2")].Value, Is.EqualTo("y with space"));
            Assert.That(fake.Settings[Harness.Name("display:inner:depth")].Value, Is.EqualTo("4"));
            Assert.That(fake.Settings[Harness.Name("endpoints:0:tags:1")].Value, Is.EqualTo("q"));
            Assert.That(fake.Settings[Harness.Name("endpoints:2:port")].Value, Is.EqualTo("9"));
            Assert.That(fake.Settings[Harness.Name("headers:X-Two")].Value, Is.EqualTo("2"));
            Assert.That(fake.Settings[Harness.Name("interval")].Value, Is.EqualTo("01:30:00"));
            Assert.That(fake.Settings[Harness.Name("maybe")].Value, Is.EqualTo("set"));
            Assert.That(fake.Settings.Values.All(kv => kv.ContentType is null), Is.True);
            // An empty item list (second endpoint has no tags) is one marker key, not missing (which would mean "default").
            Assert.That(fake.Settings[Harness.Name("endpoints:1:tags")].Value, Is.EqualTo(""));
        });

        // IConfiguration binds the same values the typed class has.
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(fake.ResolvedConfiguration()).Build();
        var bound = new EveryKindSettings();
        Microsoft.Extensions.Configuration.ConfigurationBinder.Bind(config.GetSection("MindAttic:Settings:TestApp"), bound);
        Assert.Multiple(() =>
        {
            Assert.That(bound.Count, Is.EqualTo(-42));
            Assert.That(bound.Enabled, Is.False);
            Assert.That(bound.Price, Is.EqualTo(12.750m));
            Assert.That(bound.Mode, Is.EqualTo(TestMode.Fast));
            Assert.That(bound.Display.Inner.Depth, Is.EqualTo(4));
            Assert.That(bound.Interval, Is.EqualTo(TimeSpan.FromHours(1.5)));
            Assert.That(bound.Headers["X-Two"], Is.EqualTo("2"));
        });
    }

    [Test]
    public async Task Save_WritesOnlyTheChangedFields()
    {
        var fake = new FakeAzure();
        var manifest = Harness.Manifest;
        var backend = Harness.Settings(fake);
        var first = await backend.LoadAsync(manifest);
        var version = await backend.SaveAsync(manifest, new SettingsDocument(Defaults(), first.Version), AllNames());
        var writesBefore = fake.AppConfigurationWrites.Count();

        var doc = await backend.LoadAsync(manifest);
        doc.Values["count"] = 99;
        doc.Values["title"] = "edited but not listed as changed";
        doc.Values["endpoints"]![1]!["port"] = 5000; // only one leaf inside the collection changes
        await backend.SaveAsync(manifest, doc, new[] { Harness.Name("count"), Harness.Name("endpoints:*:port") });

        var writes = fake.AppConfigurationWrites.Skip(writesBefore).ToList();
        Assert.That(writes.Select(w => w.Method + " " + Uri.UnescapeDataString(w.Path)),
            Is.EquivalentTo(new[] { "PUT /kv/" + Harness.Name("count"), "PUT /kv/" + Harness.Name("endpoints:1:port") }));
        Assert.That(fake.Settings[Harness.Name("title")].Value, Is.EqualTo("Hello"));
        Assert.That(writes.All(w => w.Header("If-Match") is not null), Is.True, "every update is guarded by the ETag seen at Load");
        Assert.That(version, Is.Not.EqualTo(first.Version));
    }

    [Test]
    public async Task Save_NothingChanged_WritesNothing()
    {
        var fake = new FakeAzure();
        var backend = Harness.Settings(fake);
        var doc = await backend.LoadAsync(Harness.Manifest);

        var version = await backend.SaveAsync(Harness.Manifest, doc, Array.Empty<string>());
        Assert.That(version, Is.EqualTo(doc.Version));
        Assert.That(fake.AppConfigurationWrites, Is.Empty, "no changed fields, no writes");

        // First save of every field stores the defaults; saving the same values again writes nothing.
        var stored = await backend.SaveAsync(Harness.Manifest, doc, AllNames());
        var writes = fake.AppConfigurationWrites.Count();
        Assert.That(writes, Is.GreaterThan(0));
        var again = await backend.LoadAsync(Harness.Manifest);
        Assert.That(again.Version, Is.EqualTo(stored));
        var unchanged = await backend.SaveAsync(Harness.Manifest, again, AllNames());
        Assert.That(fake.AppConfigurationWrites.Count(), Is.EqualTo(writes), "re-saving identical values writes nothing");
        Assert.That(unchanged, Is.EqualTo(stored));
    }

    [Test]
    public async Task Save_RemovedCollectionItems_DeletesTheirKeys()
    {
        var fake = new FakeAzure();
        var manifest = Harness.Manifest;
        var backend = Harness.Settings(fake);
        var loaded = await backend.LoadAsync(manifest);
        await backend.SaveAsync(manifest, new SettingsDocument(Edited(), loaded.Version), AllNames());
        var writesBefore = fake.AppConfigurationWrites.Count();

        var doc = await backend.LoadAsync(manifest);
        ((JsonArray)doc.Values["endpoints"]!).RemoveAt(2);
        await backend.SaveAsync(manifest, doc, new[] { Harness.Name("endpoints") });

        var writes = fake.AppConfigurationWrites.Skip(writesBefore).ToList();
        Assert.That(writes.All(w => w.Method == "DELETE"), Is.True, string.Join("\n", writes));
        Assert.That(writes.Select(w => Uri.UnescapeDataString(w.Path)), Is.EquivalentTo(new[]
        {
            "/kv/" + Harness.Name("endpoints:2:name"), "/kv/" + Harness.Name("endpoints:2:port"),
            "/kv/" + Harness.Name("endpoints:2:secure"), "/kv/" + Harness.Name("endpoints:2:tags:0"),
        }));
        Assert.That(fake.Settings.Keys.Any(k => k.StartsWith(Harness.Name("endpoints:2"), StringComparison.Ordinal)), Is.False);

        var reloaded = await Harness.Settings(fake).LoadAsync(manifest);
        Assert.That(((JsonArray)reloaded.Values["endpoints"]!).Count, Is.EqualTo(2));
    }

    [Test]
    public async Task Save_EmptiedListAndCollection_ReloadEmptyNotDefault()
    {
        var fake = new FakeAzure();
        var manifest = Harness.Manifest;
        var backend = Harness.Settings(fake);
        var doc = await backend.LoadAsync(manifest);
        doc.Values["names"] = new JsonArray();
        doc.Values["endpoints"] = new JsonArray();
        doc.Values["headers"] = new JsonObject();
        await backend.SaveAsync(manifest, doc, new[] { Harness.Name("names"), Harness.Name("endpoints"), Harness.Name("headers") });

        var reloaded = await Harness.Settings(fake).LoadAsync(manifest);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.Values["names"]!.AsArray(), Is.Empty);
            Assert.That(reloaded.Values["endpoints"]!.AsArray(), Is.Empty);
            Assert.That(reloaded.Values["headers"]!.AsObject(), Is.Empty);
        });

        // Then growing again replaces the marker with items.
        reloaded.Values["names"] = new JsonArray("k");
        await Harness.Settings(fake).SaveAsync(manifest, reloaded, new[] { Harness.Name("names") });
        Assert.That(fake.Settings.ContainsKey(Harness.Name("names")), Is.False);
        Assert.That(fake.Settings[Harness.Name("names:0")].Value, Is.EqualTo("k"));
    }

    [Test]
    public async Task Save_NullValues_RoundTripAsNull()
    {
        var fake = new FakeAzure();
        var manifest = Harness.Manifest;
        var backend = Harness.Settings(fake);
        var loaded = await backend.LoadAsync(manifest);
        await backend.SaveAsync(manifest, new SettingsDocument(Edited(), loaded.Version), AllNames());

        var doc = await backend.LoadAsync(manifest);
        doc.Values["maybe"] = null;
        doc.Values["optional"] = null;
        await backend.SaveAsync(manifest, doc, new[] { Harness.Name("maybe"), Harness.Name("optional") });

        var reloaded = await Harness.Settings(fake).LoadAsync(manifest);
        Assert.That(reloaded.Values.ContainsKey("maybe") && reloaded.Values["maybe"] is null, Is.True);
        Assert.That(reloaded.Values["optional"], Is.Null);
        Assert.That(fake.Settings[Harness.Name("maybe")].Value, Is.Null);
    }

    [Test]
    public async Task Save_SomeoneChangedTheSameKey_ThrowsConcurrencyConflict()
    {
        var fake = new FakeAzure();
        var manifest = Harness.Manifest;
        var backend = Harness.Settings(fake);
        var loaded = await backend.LoadAsync(manifest);
        await backend.SaveAsync(manifest, new SettingsDocument(Defaults(), loaded.Version), AllNames());

        var doc = await backend.LoadAsync(manifest);
        fake.Seed(Harness.Name("count"), "1234"); // the portal (or another Desktop) changed it: new ETag

        doc.Values["count"] = 5;
        var ex = Assert.ThrowsAsync<ConcurrencyConflictException>(() => backend.SaveAsync(manifest, doc, new[] { Harness.Name("count") }));
        Assert.That(ex!.Message, Does.Contain(Harness.Name("count")).And.Contain("teststore"));
        Assert.That(fake.Settings[Harness.Name("count")].Value, Is.EqualTo("1234"), "the other edit is not overwritten");
        var put = fake.Handler.Requests.Last(r => r.Method == "PUT");
        Assert.That(put.Header("If-Match"), Is.Not.Null, "the write carried the ETag seen at Load");
    }

    [Test]
    public async Task Save_SomeoneCreatedTheKeyFirst_ThrowsConcurrencyConflict()
    {
        var fake = new FakeAzure();
        var backend = Harness.Settings(fake);
        var doc = await backend.LoadAsync(Harness.Manifest);
        fake.Seed(Harness.Name("title"), "theirs");

        doc.Values["title"] = "mine";
        Assert.ThrowsAsync<ConcurrencyConflictException>(() => backend.SaveAsync(Harness.Manifest, doc, new[] { Harness.Name("title") }));
        Assert.That(fake.Settings[Harness.Name("title")].Value, Is.EqualTo("theirs"));
    }

    [Test]
    public async Task Save_StaleVersionFromAnotherInstance_ThrowsConcurrencyConflict()
    {
        var fake = new FakeAzure();
        var doc = await Harness.Settings(fake).LoadAsync(Harness.Manifest);
        fake.Seed(Harness.Name("notes"), "changed elsewhere");

        doc.Values["count"] = 5;
        Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            Harness.Settings(fake).SaveAsync(Harness.Manifest, doc, new[] { Harness.Name("count") }));
        Assert.That(fake.Settings.ContainsKey(Harness.Name("count")), Is.False);
    }

    [Test]
    public void Load_Forbidden_SaysWhichRoleOnWhichStore()
    {
        var fake = new FakeAzure();
        fake.Handler.Route(r => r.Uri.Host == FakeAzure.StoreHost
            ? FakeAzure.Json(HttpStatusCode.Forbidden, "{\"title\":\"Forbidden\",\"status\":403}")
            : null);

        var ex = Assert.ThrowsAsync<ProfileAccessDeniedException>(() => Harness.Settings(fake).LoadAsync(Harness.Manifest));
        Assert.That(ex!.Message, Is.EqualTo("You need the 'App Configuration Data Owner' role on App Configuration store 'teststore'."));
    }

    [Test]
    public async Task Save_Forbidden_SaysWhichRoleOnWhichStore()
    {
        var fake = new FakeAzure();
        var backend = Harness.Settings(fake);
        var doc = await backend.LoadAsync(Harness.Manifest);
        fake.Handler.Route(r => r.Uri.Host == FakeAzure.StoreHost && r.Method == "PUT"
            ? FakeAzure.Json(HttpStatusCode.Forbidden, "{}")
            : null);

        doc.Values["count"] = 5;
        var ex = Assert.ThrowsAsync<ProfileAccessDeniedException>(() => backend.SaveAsync(Harness.Manifest, doc, new[] { Harness.Name("count") }));
        Assert.That(ex!.Message, Does.Contain("App Configuration Data Owner").And.Contain("teststore"));
    }

    [Test]
    public async Task Save_NestedFieldName_SelectsItsTopLevelField()
    {
        var fake = new FakeAzure();
        var backend = Harness.Settings(fake);
        var doc = await backend.LoadAsync(Harness.Manifest);
        doc.Values["display"]!["inner"]!["depth"] = 9;
        await backend.SaveAsync(Harness.Manifest, doc, new[] { Harness.Name("display:inner:depth") });

        Assert.That(fake.Settings[Harness.Name("display:inner:depth")].Value, Is.EqualTo("9"));
        // Only the display subtree was written (its unchanged leaves too, since nothing was stored yet).
        Assert.That(fake.Settings.Keys.All(k => k.StartsWith(Harness.Name("display:"), StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public async Task Load_IgnoresOtherAppsAndLabeledValues()
    {
        var fake = new FakeAzure();
        fake.Seed("MindAttic:Settings:TestAppOther:count", "77");
        fake.Seed("MindAttic:Settings:TestApp:count", "8");
        var doc = await Harness.Settings(fake).LoadAsync(Harness.Manifest);

        Assert.That(doc.Values["count"]!.GetValue<long>(), Is.EqualTo(8));
        var listCall = fake.Handler.Requests.First(r => r.Uri.Host == FakeAzure.StoreHost && r.Method == "GET");
        Assert.That(listCall.Query("key"), Is.EqualTo("MindAttic:Settings:TestApp:*"));
        Assert.That(listCall.Query("label"), Is.EqualTo("\0"));
    }

    [Test]
    public async Task Load_FollowsPages()
    {
        var fake = new FakeAzure { AppConfigurationPageSize = 3 };
        var backend = Harness.Settings(fake);
        var loaded = await backend.LoadAsync(Harness.Manifest);
        await backend.SaveAsync(Harness.Manifest, new SettingsDocument(Edited(), loaded.Version), AllNames());

        var reloaded = await Harness.Settings(fake).LoadAsync(Harness.Manifest);
        Assert.That(JsonNode.DeepEquals(reloaded.Values, Edited()), Is.True);
        Assert.That(fake.Handler.Requests.Count(r => r.Method == "GET" && r.Query("after") is not null), Is.GreaterThan(5));
    }

    [Test]
    public void WhenAppsSeeChanges_IsPlainWords()
    {
        Assert.That(Harness.Settings(new FakeAzure()).WhenAppsSeeChanges,
            Is.EqualTo("after the app's App Configuration refresh interval, or its next restart"));
    }

    [Test]
    public void KeyMap_DictionaryEntryWithColon_IsRefused()
    {
        var doc = Defaults();
        doc["headers"] = new JsonObject { ["a:b"] = "x" };
        var ex = Assert.Throws<ArgumentException>(() => SettingsKeyMap.Flatten(Harness.Manifest, doc));
        Assert.That(ex!.Message, Does.Contain("a:b"));
    }
}
