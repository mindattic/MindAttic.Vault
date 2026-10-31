using System.IO.Pipes;
using System.Text;
using MindAttic.Vault.Launch;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public sealed class VaultOpenRequestTests
{
    [TestCase(new[] { "--app", "IdiotProof" }, "IdiotProof", null)]
    [TestCase(new[] { "--app", "IdiotProof", "--setting", "MindAttic:Settings:IdiotProof:maxLossPerTrade" }, "IdiotProof", "MindAttic:Settings:IdiotProof:maxLossPerTrade")]
    [TestCase(new[] { "--setting", "a b", "--app", "MindAttic.Tickets" }, "MindAttic.Tickets", "a b")]
    public void ValidArgs_Parse(string[] args, string app, string? setting)
    {
        Assert.That(VaultOpenRequest.TryParseArgs(args, out var r), Is.True);
        Assert.That((r!.AppId, r.Setting), Is.EqualTo((app, setting)));
    }

    private static readonly object[] InvalidArgSets =
    {
        new object[] { new string[0] },
        new object[] { new[] { "--app" } },
        new object[] { new[] { "--app", "x", "--app", "y" } },
        new object[] { new[] { "--app", "bad id" } },
        new object[] { new[] { "--app", "x;calc" } },
        new object[] { new[] { "--app", "x", "--evil", "y" } },
        new object[] { new[] { "--app", "x", "--setting" } },
        new object[] { new[] { "--app", "x", "--setting", "line\nbreak" } },
        new object[] { new[] { "/c", "calc" } },
    };

    [TestCaseSource(nameof(InvalidArgSets))]
    public void InvalidArgs_AreRejected(string[] args) =>
        Assert.That(VaultOpenRequest.TryParseArgs(args, out _), Is.False);

    [Test]
    public void OverlongValues_AreRejected()
    {
        Assert.That(VaultOpenRequest.TryParseArgs(new[] { "--app", new string('a', 65) }, out _), Is.False);
        Assert.That(VaultOpenRequest.TryParseArgs(new[] { "--app", "a", "--setting", new string('s', 257) }, out _), Is.False);
    }

    [Test]
    public void Url_RoundTripsThroughLinkAndParse()
    {
        var link = VaultSettings.Link("IdiotProof", "MindAttic:Settings:IdiotProof:risk limits");
        Assert.That(link, Does.StartWith("mindattic-vault://open?app=IdiotProof&setting="));
        Assert.That(VaultOpenRequest.TryParseArgs(new[] { link }, out var r), Is.True);
        Assert.That(r!.Setting, Is.EqualTo("MindAttic:Settings:IdiotProof:risk limits"));
    }

    [TestCase("https://open?app=x")]
    [TestCase("mindattic-vault://delete?app=x")]
    [TestCase("mindattic-vault://open/extra?app=x")]
    [TestCase("mindattic-vault://open?app=x&cmd=calc")]
    [TestCase("mindattic-vault://open?app=bad%20id")]
    [TestCase("mindattic-vault://open?setting=y")]
    [TestCase("not a url")]
    public void BadUrls_AreRejected(string url) => Assert.That(VaultOpenRequest.TryParseUrl(url, out _), Is.False);

    [Test]
    public void ToArgs_MatchesTheGrammar() =>
        Assert.That(new VaultOpenRequest("App", "S").ToArgs(), Is.EqualTo(new[] { "--app", "App", "--setting", "S" }));

    [Test]
    public void Constructor_RejectsInvalidInput()
    {
        Assert.Throws<ArgumentException>(() => new VaultOpenRequest("a b"));
        Assert.Throws<ArgumentException>(() => new VaultOpenRequest("a", "x\ty"));
    }
}

[TestFixture]
public sealed class VaultSettingsOpenTests
{
    private sealed class FakeLauncher : IVaultDesktopLauncher
    {
        public bool Running { get; set; }
        public bool Installed { get; set; }
        public List<string> Calls { get; } = new();
        public bool TrySend(VaultOpenRequest r) { Calls.Add("send " + string.Join(' ', r.ToArgs())); return Running; }
        public bool TryStart(VaultOpenRequest r) { Calls.Add("start " + string.Join(' ', r.ToArgs())); return Installed; }
    }

    private IVaultDesktopLauncher original = null!;
    [SetUp] public void SetUp() => original = VaultSettings.Launcher;
    [TearDown] public void TearDown() => VaultSettings.Launcher = original;

    [Test]
    public void Running_SendsAndDoesNotStartASecondInstance()
    {
        var fake = new FakeLauncher { Running = true };
        VaultSettings.Launcher = fake;
        Assert.That(VaultSettings.Open("Tutor", "MindAttic:Settings:Tutor:x"), Is.True);
        Assert.That(fake.Calls, Is.EqualTo(new[] { "send --app Tutor --setting MindAttic:Settings:Tutor:x" }));
    }

    [Test]
    public void NotRunning_Starts()
    {
        var fake = new FakeLauncher { Installed = true };
        VaultSettings.Launcher = fake;
        Assert.That(VaultSettings.Open("Tutor"), Is.True);
        Assert.That(fake.Calls, Is.EqualTo(new[] { "send --app Tutor", "start --app Tutor" }));
    }

    [Test]
    public void NotInstalled_ReturnsFalse()
    {
        VaultSettings.Launcher = new FakeLauncher();
        Assert.That(VaultSettings.Open("Tutor"), Is.False);
    }

    [Test]
    public void FindExe_PrefersEnvThenDefaultThenPath()
    {
        var env = new Dictionary<string, string?> { ["PATH"] = @"C:\bin" };
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\bin\" + VaultDesktopContract.ExeName };
        Assert.That(VaultDesktopContract.FindExe(k => env.GetValueOrDefault(k), present.Contains), Is.EqualTo(@"C:\bin\" + VaultDesktopContract.ExeName));
        present.Add(VaultDesktopContract.DefaultExePath);
        Assert.That(VaultDesktopContract.FindExe(k => env.GetValueOrDefault(k), present.Contains), Is.EqualTo(VaultDesktopContract.DefaultExePath));
        env[VaultDesktopContract.ExePathEnvVar] = @"D:\x.exe";
        present.Add(@"D:\x.exe");
        Assert.That(VaultDesktopContract.FindExe(k => env.GetValueOrDefault(k), present.Contains), Is.EqualTo(@"D:\x.exe"));
        Assert.That(VaultDesktopContract.FindExe(_ => null, _ => false), Is.Null);
    }
}

[TestFixture]
public sealed class VaultDesktopPipeServerTests
{
    private static async Task Send(string pipe, string payload)
    {
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);
        var bytes = Encoding.UTF8.GetBytes(payload);
        await client.WriteAsync(bytes);
    }

    [Test]
    public async Task ValidRequestIsDelivered_InvalidIsRejected()
    {
        var pipe = "MindAttic.Vault.Test." + Guid.NewGuid().ToString("N");
        var received = new List<VaultOpenRequest>();
        var rejected = new List<string>();
        using var cts = new CancellationTokenSource();
        var server = VaultDesktopPipeServer.RunAsync(r => { lock (received) received.Add(r); }, r => { lock (rejected) rejected.Add(r); }, pipe, cts.Token);

        await Send(pipe, "--app\nIdiotProof\n--setting\nMindAttic:Settings:IdiotProof:mode\n");
        await Send(pipe, "--app\nbad id\n");
        try { await Send(pipe, new string('x', VaultDesktopPipeServer.MaxMessageBytes + 10)); }
        catch (IOException) { /* the server stops reading at the cap and hangs up, as intended */ }
        for (var i = 0; i < 50 && received.Count + rejected.Count < 3; i++) await Task.Delay(50);
        cts.Cancel();
        await server;

        Assert.That(received.Single().Setting, Is.EqualTo("MindAttic:Settings:IdiotProof:mode"));
        Assert.That(rejected, Is.EquivalentTo(new[] { "unrecognized request", "message too large" }));
    }

    [Test]
    public async Task RealLauncher_SendReachesTheServer()
    {
        var received = new TaskCompletionSource<VaultOpenRequest>();
        using var cts = new CancellationTokenSource();
        var server = VaultDesktopPipeServer.RunAsync(r => received.TrySetResult(r), cancellationToken: cts.Token);
        await Task.Delay(100);
        Assert.That(new VaultDesktopLauncher().TrySend(new VaultOpenRequest("Tutor")), Is.True);
        var r = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(r.AppId, Is.EqualTo("Tutor"));
        cts.Cancel();
        await server;
    }
}
