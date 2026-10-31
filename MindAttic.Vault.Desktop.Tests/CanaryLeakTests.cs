using System.Net;
using System.Text;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Desktop.ViewModels;
using NUnit.Framework;
using Serilog;

namespace MindAttic.Vault.Desktop.Tests;

/// <summary>
/// RFC 0003 Security / Testing 6: a canary key pushed through add, test, set value, rename, assignment, Get Started,
/// Save, audit, the diagnostic log and the remembered state appears nowhere on disk except the provider's key file,
/// and in no message shown to the user.
/// </summary>
[TestFixture]
public sealed class CanaryLeakTests
{
    private const string Canary = "sk-ant-CANARY-7f3e9c1d-do-not-leak";
    private const string Canary2 = "sk-oa-CANARY-2b8d4a60-do-not-leak";
    private const string Canary3 = "cred-CANARY-5a1b2c3d-only-in-groups";

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                // A provider that echoes the key back must not get it into a message either.
                Content = new StringContent("invalid key " + Canary),
            });
    }

    [Test]
    public async Task The_canary_appears_only_in_the_key_store()
    {
        using var sandbox = new VaultSandbox();
        var logDb = Path.Combine(sandbox.Roaming, "Logging", DesktopLog.Application, "MindAttic.Log.db");
        var previousLogger = Serilog.Log.Logger;
        Serilog.Log.Logger = DesktopLog.Configure(new LoggerConfiguration(), logDb).CreateLogger();
        var dialogs = new FakeDialogs();
        var shown = new List<string>();
        try
        {
            Samples.Install<AlphaSettings>(sandbox.Roaming);
            Samples.Install<BetaSettings>(sandbox.Roaming);
            var store = NamedKeyStore.ForRoamingRoot();
            var audit = new SqliteAuditLog();
            var context = new DesktopContext(dialogs, new InlineDispatcher(), KeyHealthStore.ForLocalRoot(), TimeProvider.System, audit);
            var operations = new KeyOperations(context, new ProviderKeyTester(new RefusingHandler()));
            using var state = new DesktopStateKeeper(debounce: TimeSpan.FromMilliseconds(10));
            var main = new MainViewModel(new IProfile[] { new DesktopProfile(keys: store) }, new FileManifestCatalog(), context, operations,
                getStartedSources: new GetStartedSources(() => new KeyImporter(store, _ => null), () => null), state: state);
            await main.InitializeAsync();

            // Get Started: paste and test (the provider refuses it).
            var claude = main.GetStarted.Missing.Single(m => m.ProviderId == "claude");
            var result = await claude.AddAndTestAsync(Canary);
            shown.Add(result!.Message);
            shown.Add(claude.TestMessage);
            await main.KeysChangedAsync();

            // Keys tab: rename, test, set a new value, add another, disable, reorder, a refused duplicate.
            var keys = main.Keys.Provider("claude")!;
            await keys.Keys.Single().RenameAsync("canary key");
            await main.KeysChangedAsync();
            shown.Add((await main.Keys.Provider("claude")!.Keys.Single().TestAsync()).Message);
            await main.Keys.Provider("claude")!.Keys.Single().SetValueAsync(Canary + "-v2");
            await main.KeysChangedAsync();
            var openai = main.Keys.Provider("openai")!;
            openai.NewKeyName = "second";
            await openai.AddAsync(Canary2);
            openai.NewKeyName = "second";
            await openai.AddAsync(Canary2); // refused: duplicate name
            await main.KeysChangedAsync();
            await main.Keys.Provider("openai")!.Keys.Single().SetDisabledAsync(true);
            await main.KeysChangedAsync();
            shown.Add(main.Keys.Provider("claude")!.TestMessage);

            // An app assigns the key; removing it is refused; Save writes the settings tab.
            var alpha = main.AppTabs.Single(t => t.AppId == "Alpha");
            await alpha.Credentials.Single().SetActiveAsync("canary key");
            await main.Keys.Provider("claude")!.Keys.Single().RemoveAsync();
            alpha.Document!["name"] = "not a secret";
            alpha.MarkChanged(Samples.AlphaName);
            await alpha.SaveAsync();
            shown.Add(alpha.StatusText);
            main.SelectedTab = alpha;

            // Credentials tab (M.VALT-T62): a secret into a new group (from its template), a new token, a failed save
            // (stale file), and nothing secret in any bindable property or message.
            var subtitles = main.Credentials.Group("Subtitles")!;
            subtitles.Entry("OpenSubtitles")!.Field("password")!.EditSecret(Canary3);
            subtitles.Entry("OpenSubtitles")!.Field("user")!.Text = "canary user";
            await subtitles.SaveAsync();
            shown.Add(subtitles.StatusText);
            var tokens = main.Credentials.Group("Tokens")!;
            tokens.Root!.AddField("canary-token");
            tokens.Root.Field("canary-token")!.EditSecret(Canary3);
            await tokens.SaveAsync();
            shown.Add(tokens.StatusText);
            await main.Credentials.ReloadAsync();
            var again = main.Credentials.Group("Subtitles")!;
            again.Entry("OpenSubtitles")!.Field("password")!.EditSecret(Canary3 + "-stale");
            File.SetLastWriteTimeUtc(Path.Combine(sandbox.Roaming, "Subtitles", "providers.json"), DateTime.UtcNow);
            File.AppendAllText(Path.Combine(sandbox.Roaming, "Subtitles", "providers.json"), " ");
            await again.SaveAsync(); // refused: changed on disk
            shown.Add(again.StatusText);
            shown.AddRange(BindableText.Of(main.Credentials).Where(t => t.Contains("CANARY", StringComparison.Ordinal)));
            main.SelectedTab = main.Credentials;
            DesktopLog.For("Canary").Information("A key reaching a template: {@Key}", store.GetKeys("claude").Single());
            state.Flush();
        }
        finally
        {
            await Serilog.Log.CloseAndFlushAsync();
            Serilog.Log.Logger = previousLogger;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        // Sanity: the flows really ran.
        Assert.That(File.Exists(logDb), Is.True);
        Assert.That(File.Exists(Path.Combine(sandbox.Local, "Vault", "audit.db")), Is.True);
        Assert.That(File.Exists(Path.Combine(sandbox.Roaming, "Vault.Desktop", "state.json")), Is.True);
        Assert.That(File.Exists(Path.Combine(sandbox.Local, "Vault", "key-health.json")), Is.True);
        Assert.That(dialogs.Errors, Is.Not.Empty, "the refused duplicate and removal showed messages");

        var allowed = Path.Combine(sandbox.Roaming, "LLM", "providers.json");
        var holders = Directory.EnumerateFiles(sandbox.Root, "*", SearchOption.AllDirectories)
            .Where(f => Contains(File.ReadAllBytes(f), Canary) || Contains(File.ReadAllBytes(f), Canary2))
            .ToList();
        Assert.That(holders, Is.EqualTo(new[] { allowed }), "only the key store holds the key: " + string.Join(", ", holders));

        var credentialHolders = Directory.EnumerateFiles(sandbox.Root, "*", SearchOption.AllDirectories)
            .Where(f => File.ReadAllBytes(f).AsSpan().IndexOf(Encoding.UTF8.GetBytes(Canary3)) >= 0
                        || File.ReadAllBytes(f).AsSpan().IndexOf(Encoding.Unicode.GetBytes(Canary3)) >= 0)
            .Select(f => Path.GetRelativePath(sandbox.Roaming, f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.That(credentialHolders, Is.EqualTo(new[] { Path.Combine("Subtitles", "providers.json"), Path.Combine("Tokens", "tokens.json") }),
            "only the groups' own files hold a credential: " + string.Join(", ", credentialHolders));
        Assert.That(dialogs.Errors.Any(e => e.Contains("changed on disk", StringComparison.Ordinal)), Is.True, "the stale save was refused");

        var messages = shown.Concat(dialogs.Errors).Concat(dialogs.Infos).Concat(dialogs.Confirms).ToList();
        Assert.That(messages.Where(m => m.Contains("CANARY", StringComparison.Ordinal)), Is.Empty);
    }

    private static bool Contains(byte[] bytes, string text) =>
        IndexOf(bytes, Encoding.UTF8.GetBytes(text)) >= 0 || IndexOf(bytes, Encoding.Unicode.GetBytes(text)) >= 0
        || IndexOf(bytes, Encoding.UTF8.GetBytes("CANARY")) >= 0 && IndexOf(bytes, Encoding.UTF8.GetBytes(text[^20..])) >= 0;

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);
}
