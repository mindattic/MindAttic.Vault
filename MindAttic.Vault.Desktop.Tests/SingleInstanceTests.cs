using System.IO.Pipes;
using System.Text;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Desktop.ViewModels;
using MindAttic.Vault.Launch;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Tests;

[TestFixture]
public sealed class SingleInstanceTests
{
    private VaultSandbox sandbox = null!;
    private MainViewModel vm = null!;

    [SetUp]
    public async Task SetUp()
    {
        sandbox = new VaultSandbox();
        vm = Samples.Shell(new FakeCatalog { Manifests = { Samples.Alpha, Samples.Beta } }, Samples.Context(localRoot: sandbox.Local),
            new FakeProfile("Desktop"));
        await vm.InitializeAsync();
    }

    [TearDown]
    public void TearDown() => sandbox.Dispose();

    private sealed class FakeLauncher : IVaultDesktopLauncher
    {
        public int FailuresBeforeSuccess { get; set; }
        public List<VaultOpenRequest> Sent { get; } = new();
        public bool TrySend(VaultOpenRequest request)
        {
            Sent.Add(request);
            return FailuresBeforeSuccess-- <= 0;
        }
        public bool TryStart(VaultOpenRequest request) => false;
    }

    [TestCase(new[] { "--app", "Global" }, "Global", null)]
    [TestCase(new[] { "--setting", "MindAttic:Settings:Alpha:name", "--app", "Alpha" }, "Alpha", "MindAttic:Settings:Alpha:name")]
    [TestCase(new[] { "mindattic-vault://open?app=Alpha&setting=MindAttic%3ASettings%3AAlpha%3Aname" }, "Alpha", "MindAttic:Settings:Alpha:name")]
    public void Startup_args_accept_only_the_request_shapes(string[] args, string app, string? setting)
    {
        Assert.That(VaultOpenRequest.TryParseArgs(args, out var request), Is.True);
        Assert.That(request!.AppId, Is.EqualTo(app));
        Assert.That(request.Setting, Is.EqualTo(setting));
    }

    private static readonly object[] Rejected =
    {
        new object[] { new[] { "--app" } },
        new object[] { new[] { "--app", "../evil" } },
        new object[] { new[] { "--app", "Alpha", "--debug", "x" } },
        new object[] { new[] { "https://example.com/?app=Alpha" } },
        new object[] { new[] { "--register", "x" } },
    };

    [TestCaseSource(nameof(Rejected))]
    public void Anything_else_is_rejected(string[] args) =>
        Assert.That(VaultOpenRequest.TryParseArgs(args, out _), Is.False);

    [Test]
    public async Task A_request_for_a_known_app_selects_its_tab_scrolls_and_activates()
    {
        var activated = 0;
        vm.ActivationRequested += (_, _) => activated++;

        Assert.That(await vm.OpenAsync(new VaultOpenRequest("alpha", Samples.AlphaHost)), Is.True);

        var alpha = (AppSettingsViewModel)vm.SelectedTab!;
        Assert.That(alpha.AppId, Is.EqualTo("Alpha"), "app ids match case-insensitively");
        Assert.That(alpha.PendingScroll, Is.EqualTo(Samples.AlphaHost));
        Assert.That(activated, Is.EqualTo(1));
    }

    [Test]
    public async Task A_request_for_an_unknown_app_is_ignored()
    {
        var before = vm.SelectedTab;

        Assert.That(await vm.OpenAsync(new VaultOpenRequest("NotInstalled")), Is.False);

        Assert.That(vm.SelectedTab, Is.SameAs(before));
        Assert.That(vm.StatusText, Does.Contain("NotInstalled"));
    }

    [Test]
    public async Task A_setting_that_is_not_the_apps_is_not_scrolled_to()
    {
        await vm.OpenAsync(new VaultOpenRequest("Beta", Samples.AlphaName));

        Assert.That(((AppSettingsViewModel)vm.SelectedTab!).AppId, Is.EqualTo("Beta"));
        Assert.That(((AppSettingsViewModel)vm.SelectedTab!).PendingScroll, Is.Null);
    }

    [Test]
    public async Task A_request_for_an_app_whose_manifest_just_appeared_finds_it_after_a_rescan()
    {
        var catalog = new FakeCatalog { Manifests = { Samples.Alpha } };
        var main = Samples.Shell(catalog, Samples.Context(localRoot: sandbox.Local), new FakeProfile("Desktop"));
        await main.InitializeAsync();
        catalog.Manifests.Add(Samples.Beta); // no Changed event yet

        Assert.That(await main.OpenAsync(new VaultOpenRequest("Beta")), Is.True);
        Assert.That(((AppSettingsViewModel)main.SelectedTab!).AppId, Is.EqualTo("Beta"));
    }

    [Test]
    public void Only_the_first_start_owns_the_instance()
    {
        var name = @"Local\MindAttic.Vault.Desktop.Test." + Guid.NewGuid().ToString("N");
        using var first = SingleInstance.TryClaim(name);
        Assert.That(first, Is.Not.Null);

        SingleInstance? second = null;
        var thread = new Thread(() => second = SingleInstance.TryClaim(name));
        thread.Start();
        thread.Join();
        Assert.That(second, Is.Null);
    }

    [Test]
    public void A_second_start_hands_its_request_over_and_retries_while_the_first_starts()
    {
        var launcher = new FakeLauncher { FailuresBeforeSuccess = 2 };
        var raised = 0;

        var delivered = SingleInstance.HandOff(new VaultOpenRequest("Global"), launcher, TimeSpan.FromSeconds(5), () => raised++);

        Assert.That(delivered, Is.True);
        Assert.That(launcher.Sent, Has.Count.EqualTo(3));
        Assert.That(launcher.Sent.All(r => r.AppId == "Global"), Is.True);
        Assert.That(raised, Is.Zero);
    }

    [Test]
    public void A_second_start_without_a_request_just_brings_the_first_forward()
    {
        var launcher = new FakeLauncher();
        var raised = 0;

        Assert.That(SingleInstance.HandOff(null, launcher, TimeSpan.FromSeconds(1), () => raised++), Is.False);

        Assert.That(launcher.Sent, Is.Empty);
        Assert.That(raised, Is.EqualTo(1));
    }

    [Test]
    public async Task The_pipe_routes_valid_requests_and_rejects_the_rest()
    {
        var pipe = "MindAttic.Vault.Desktop.Test." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = VaultDesktopPipeServer.RunAsync(
            r => opened.TrySetResult(vm.OpenAsync(r).GetAwaiter().GetResult()),
            reason => rejected.TrySetResult(reason), pipe, stop.Token);

        await Send(pipe, "--app\nBeta\n");
        Assert.That(await opened.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(((AppSettingsViewModel)vm.SelectedTab!).AppId, Is.EqualTo("Beta"));

        await Send(pipe, "--evil\nrm -rf\n");
        Assert.That(await rejected.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo("unrecognized request"));

        stop.Cancel();
        await server.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task Send(string pipe, string text)
    {
        await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);
        var bytes = Encoding.UTF8.GetBytes(text);
        await client.WriteAsync(bytes);
        await client.FlushAsync();
    }
}
