using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using MindAttic.Vault.Credentials;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

internal sealed class FakeTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

[TestFixture]
public sealed class KeySelectorTests
{
    private TempDirectory tmp = null!;
    private NamedKeyStore keys = null!;
    private KeyHealthStore health = null!;
    private FakeTime time = null!;
    private KeySelector selector = null!;

    [SetUp]
    public void SetUp()
    {
        tmp = new TempDirectory("selector");
        keys = new NamedKeyStore(tmp.Combine("roaming"), new KeyAssignmentStore(tmp.Combine("roaming", "Vault")));
        health = new KeyHealthStore(tmp.Combine("local", "Vault"));
        health.Track(keys);
        time = new FakeTime();
        selector = new KeySelector(new FileKeySource(keys), health, time);
        keys.Add("claude", "bad", "sk-bad");
        keys.Add("claude", "good", "sk-good");
    }

    [TearDown] public void TearDown() => tmp.Dispose();

    private static Func<string, CancellationToken, Task<string>> Server(Dictionary<string, HttpStatusCode> byKey, List<string>? tried = null) =>
        (key, _) =>
        {
            tried?.Add(key);
            var status = byKey.TryGetValue(key, out var s) ? s : HttpStatusCode.OK;
            if (status != HttpStatusCode.OK) throw new HttpRequestException("failed", null, status);
            return Task.FromResult("ok:" + key);
        };

    [Test]
    public async Task Unauthorized_FailsOverAndMarksTheKeyFailed()
    {
        var result = await selector.ExecuteAsync("claude", "IdiotProof", Server(new() { ["sk-bad"] = HttpStatusCode.Unauthorized }));
        Assert.That(result, Is.EqualTo("ok:sk-good"));
        var h = health.Get("claude", "bad");
        Assert.That(h.StatusAt(time.Now, false), Is.EqualTo(KeyStatus.Failed));
        Assert.That(h.FailureKind, Is.EqualTo(KeyFailureKind.Auth));
        Assert.That(health.Get("claude", "good").StatusAt(time.Now, false), Is.EqualTo(KeyStatus.Healthy));
    }

    [Test]
    public async Task FailedKey_IsSkippedByEveryAppAfterwards()
    {
        await selector.ExecuteAsync("claude", "A", Server(new() { ["sk-bad"] = HttpStatusCode.Forbidden }));
        var tried = new List<string>();
        var other = new KeySelector(new FileKeySource(keys), new KeyHealthStore(health.Directory), time);
        await other.ExecuteAsync("claude", "B", Server(new(), tried));
        Assert.That(tried, Is.EqualTo(new[] { "sk-good" }));
    }

    [Test]
    public async Task RateLimited_CoolsDownForRetryAfterThenIsTriedAgain()
    {
        await selector.SendAsync("claude", null, (key, _) =>
        {
            var r = new HttpResponseMessage(key == "sk-bad" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            if (key == "sk-bad") r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(r);
        });
        var h = health.Get("claude", "bad");
        Assert.That(h.CoolDownUntilUtc, Is.EqualTo(time.Now.AddSeconds(120)));
        Assert.That(h.Failed, Is.False);
        Assert.That(selector.Plan("claude", null).Usable.Select(k => k.Name), Is.EqualTo(new[] { "good" }));
        time.Now = time.Now.AddSeconds(121);
        Assert.That(selector.Plan("claude", null).Usable.Select(k => k.Name), Is.EqualTo(new[] { "bad", "good" }));
    }

    [Test]
    public async Task RateLimitedWithoutRetryAfter_UsesTheDefaultCoolDown()
    {
        await selector.ExecuteAsync("claude", null, Server(new() { ["sk-bad"] = HttpStatusCode.TooManyRequests }));
        Assert.That(health.Get("claude", "bad").CoolDownUntilUtc, Is.EqualTo(time.Now + KeyHealthStore.DefaultCoolDown));
    }

    [Test]
    public async Task PaymentRequired_IsAQuotaFailure()
    {
        await selector.ExecuteAsync("claude", null, Server(new() { ["sk-bad"] = HttpStatusCode.PaymentRequired }));
        Assert.That(health.Get("claude", "bad").FailureKind, Is.EqualTo(KeyFailureKind.Quota));
    }

    [Test]
    public void NetworkError_IsRethrownAndNeverBlamesTheKey()
    {
        var tried = new List<string>();
        Assert.ThrowsAsync<HttpRequestException>(() => selector.ExecuteAsync<string>("claude", null, (key, _) =>
        {
            tried.Add(key);
            throw new HttpRequestException("no route");
        }));
        Assert.That(tried, Is.EqualTo(new[] { "sk-bad" }));
        Assert.That(health.Get("claude", "bad").StatusAt(time.Now, false), Is.EqualTo(KeyStatus.NeverUsed));
    }

    [Test]
    public void ServerError_IsNotTheKeysFault()
    {
        Assert.ThrowsAsync<HttpRequestException>(() =>
            selector.ExecuteAsync("claude", null, Server(new() { ["sk-bad"] = HttpStatusCode.InternalServerError })));
        Assert.That(health.Get("claude", "bad").Failed, Is.False);
    }

    [Test]
    public async Task DisabledKey_IsSkipped()
    {
        keys.SetDisabled("claude", "bad", true);
        var tried = new List<string>();
        await selector.ExecuteAsync("claude", null, Server(new(), tried));
        Assert.That(tried, Is.EqualTo(new[] { "sk-good" }));
    }

    [Test]
    public void AllFail_ThrowsWithNamesAndKindsButNoKeyText()
    {
        var ex = Assert.ThrowsAsync<AllKeysFailedException>(() => selector.ExecuteAsync("claude", "Tutor",
            Server(new() { ["sk-bad"] = HttpStatusCode.Unauthorized, ["sk-good"] = HttpStatusCode.TooManyRequests })))!;
        Assert.That(ex.Attempts.Select(a => (a.KeyName, a.Outcome)), Is.EqualTo(new[] { ("bad", "Auth"), ("good", "RateLimited") }));
        Assert.That(ex.Message, Does.Contain("bad (Auth)").And.Contain("Tutor"));
        Assert.That(ex.Message, Does.Not.Contain("sk-"));
        Assert.That(ex.InnerException, Is.Null);
    }

    [Test]
    public void NoKeys_ThrowsNoKeys()
    {
        var ex = Assert.ThrowsAsync<AllKeysFailedException>(() => selector.ExecuteAsync("openai", null, Server(new())))!;
        Assert.That(ex.NoKeys, Is.True);
    }

    [Test]
    public void AppAssignment_UsesExactlyThoseKeysInOrder()
    {
        keys.Add("claude", "2026-10-31 IdiotProof", "sk-ip");
        keys.Assignments.Set("IdiotProof", "claude", new[] { "2026-10-31 IdiotProof", "bad" });
        Assert.That(selector.Plan("claude", "IdiotProof").Usable.Select(k => k.Name), Is.EqualTo(new[] { "2026-10-31 IdiotProof", "bad" }));
        Assert.That(selector.Plan("claude", "Tutor").Usable.Select(k => k.Name), Is.EqualTo(new[] { "bad", "good", "2026-10-31 IdiotProof" }));
        Assert.That(selector.GetActiveKey("anthropic", "IdiotProof")!.Value, Is.EqualTo("sk-ip"));
    }

    [Test]
    public void AssignedNameThatNoLongerExists_IsSkippedAndReported()
    {
        keys.Assignments.Set("IdiotProof", "claude", new[] { "gone", "good" });
        var plan = selector.Plan("claude", "IdiotProof");
        Assert.That(plan.Usable.Select(k => k.Name), Is.EqualTo(new[] { "good" }));
        Assert.That(plan.MissingAssigned, Is.EqualTo(new[] { "gone" }));
    }

    [Test]
    public async Task ExplicitKeyFailureException_IsHonored()
    {
        await selector.ExecuteAsync<string>("claude", null, (key, _) =>
            key == "sk-bad" ? throw new KeyFailureException(KeyFailureKind.Quota, "insufficient_quota") : Task.FromResult("ok"));
        Assert.That(health.Get("claude", "bad").FailureKind, Is.EqualTo(KeyFailureKind.Quota));
    }

    [Test]
    public async Task CustomClassifier_MapsSdkExceptions()
    {
        var custom = new KeySelector(new FileKeySource(keys), health, time,
            ex => ex is InvalidOperationException { Message: "auth" } ? new KeyFailureException(KeyFailureKind.Auth, "sdk") : null);
        await custom.ExecuteAsync<string>("claude", null, (key, _) =>
            key == "sk-bad" ? throw new InvalidOperationException("auth") : Task.FromResult("ok"));
        Assert.That(health.Get("claude", "bad").Failed, Is.True);
    }

    [Test]
    public async Task Success_ClearsAnEarlierFailure()
    {
        health.RecordFailure("claude", "bad", KeyFailureKind.Auth, time.Now);
        health.Reset("claude", "bad");
        await selector.ExecuteAsync("claude", null, Server(new()));
        Assert.That(health.Get("claude", "bad").StatusAt(time.Now, false), Is.EqualTo(KeyStatus.Healthy));
    }

    [Test]
    public void Cancellation_IsNotAKeyFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.CatchAsync<OperationCanceledException>(() => selector.ExecuteAsync("claude", null, Server(new()), cts.Token));
        Assert.That(health.Get("claude", "bad").Failed, Is.False);
    }

    [Test]
    public async Task HealthFile_NeverContainsKeyValuesAndIsWrittenAtomically()
    {
        await selector.ExecuteAsync("claude", null, Server(new() { ["sk-bad"] = HttpStatusCode.Unauthorized }));
        var text = File.ReadAllText(health.FilePath);
        Assert.That(text, Does.Contain("\"bad\"").And.Not.Contain("sk-"));
        Assert.That(Directory.GetFiles(health.Directory, "*.tmp"), Is.Empty);
    }

    [Test]
    public async Task RenameAndRemove_FollowIntoHealth()
    {
        await selector.ExecuteAsync("claude", null, Server(new() { ["sk-bad"] = HttpStatusCode.Unauthorized }));
        keys.Rename("claude", "bad", "renamed");
        Assert.That(health.Get("claude", "renamed").Failed, Is.True);
        Assert.That(health.GetAll("claude").ContainsKey("bad"), Is.False);
        keys.Remove("claude", "renamed");
        Assert.That(health.GetAll("claude").ContainsKey("renamed"), Is.False);
    }

    [Test]
    public void CorruptHealthFile_ReadsAsUnknown()
    {
        Directory.CreateDirectory(health.Directory);
        File.WriteAllText(health.FilePath, "garbage");
        Assert.That(health.Get("claude", "bad").StatusAt(time.Now, false), Is.EqualTo(KeyStatus.NeverUsed));
        health.RecordFailure("claude", "bad", KeyFailureKind.Auth, time.Now);
        Assert.That(health.Get("claude", "bad").Failed, Is.True);
    }

    [Test]
    public void CompositeSource_PrefersTheFirstSourceWithKeys()
    {
        var empty = new NamedKeyStore(tmp.Combine("empty"), new KeyAssignmentStore(tmp.Combine("empty", "Vault")));
        var composite = new CompositeKeySource(new FileKeySource(empty), new FileKeySource(keys));
        Assert.That(composite.GetKeys("claude").Select(k => k.Name), Is.EqualTo(new[] { "bad", "good" }));
        empty.Add("claude", "cfg", "sk-cfg");
        Assert.That(composite.GetKeys("claude").Select(k => k.Name), Is.EqualTo(new[] { "cfg" }));
    }
}
