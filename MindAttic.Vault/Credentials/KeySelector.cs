using System.Net;
using System.Net.Http;

namespace MindAttic.Vault.Credentials;

/// <summary>Where <see cref="KeySelector"/> reads keys and app assignments from.</summary>
public interface IKeySource
{
    /// <summary>The provider's shared named keys in default failover order.</summary>
    IReadOnlyList<NamedKey> GetKeys(string providerId);

    /// <summary>The app's ordered key names for the provider (first = active); empty = shared order.</summary>
    IReadOnlyList<string> GetAssignment(string appId, string providerId);
}

/// <summary>Keys and assignments from the local files (Desktop Profile, every desktop app).</summary>
public sealed class FileKeySource : IKeySource
{
    private readonly NamedKeyStore keys;

    public FileKeySource(NamedKeyStore keys) => this.keys = keys ?? throw new ArgumentNullException(nameof(keys));

    public IReadOnlyList<NamedKey> GetKeys(string providerId) => keys.GetKeys(providerId);
    public IReadOnlyList<string> GetAssignment(string appId, string providerId) => keys.Assignments.Get(appId, providerId);
}

/// <summary>Reads from the first source that has any key for the provider (e.g. configuration over files).</summary>
public sealed class CompositeKeySource : IKeySource
{
    private readonly IReadOnlyList<IKeySource> sources;

    public CompositeKeySource(params IKeySource[] sources) => this.sources = sources;

    public IReadOnlyList<NamedKey> GetKeys(string providerId) =>
        Pick(providerId)?.GetKeys(providerId) ?? Array.Empty<NamedKey>();

    public IReadOnlyList<string> GetAssignment(string appId, string providerId) =>
        Pick(providerId)?.GetAssignment(appId, providerId) ?? Array.Empty<string>();

    private IKeySource? Pick(string providerId) => sources.FirstOrDefault(s => s.GetKeys(providerId).Count > 0);
}

/// <summary>Throw from a call to tell <see cref="KeySelector"/> the key itself failed.</summary>
public sealed class KeyFailureException : Exception
{
    public KeyFailureException(KeyFailureKind kind, string message, TimeSpan? retryAfter = null) : base(message)
    {
        Kind = kind;
        RetryAfter = retryAfter;
    }

    public KeyFailureKind Kind { get; }
    public TimeSpan? RetryAfter { get; }
}

/// <summary>One key tried (or skipped) during a failover.</summary>
public sealed record KeyAttempt(string KeyName, string Outcome);

/// <summary>Every usable key for a provider failed or none is configured. Names keys, never their values.</summary>
public sealed class AllKeysFailedException : Exception
{
    public AllKeysFailedException(string providerId, string? appId, IReadOnlyList<KeyAttempt> attempts)
        : base(Describe(providerId, appId, attempts))
    {
        ProviderId = providerId;
        AppId = appId;
        Attempts = attempts;
    }

    public string ProviderId { get; }
    public string? AppId { get; }
    public IReadOnlyList<KeyAttempt> Attempts { get; }

    /// <summary>True when there was nothing to try: no key is configured or assigned.</summary>
    public bool NoKeys => Attempts.Count == 0;

    private static string Describe(string provider, string? app, IReadOnlyList<KeyAttempt> attempts) =>
        attempts.Count == 0
            ? $"No {provider} key is configured{(app is null ? "" : $" for {app}")}. Add one in MindAttic Vault (Keys tab)."
            : $"Every {provider} key{(app is null ? "" : $" assigned to {app}")} failed: "
              + string.Join(", ", attempts.Select(a => $"{a.KeyName} ({a.Outcome})"))
              + ". Fix or add a key in MindAttic Vault (Keys tab).";
}

/// <summary>The keys an app would try for a provider, and why others were skipped.</summary>
public sealed record KeyPlan(IReadOnlyList<NamedKey> Usable, IReadOnlyList<KeyAttempt> Skipped, IReadOnlyList<string> MissingAssigned);

/// <summary>
/// Calls a provider with an app's active key, failing over to its fallbacks (or the shared order).
/// 401/403 mark a key failed, 402/quota too, 429 cools it down (honoring Retry-After); network
/// errors are not the key's fault and are rethrown without blame. Health is shared machine-wide.
/// </summary>
public sealed class KeySelector
{
    private readonly IKeySource source;
    private readonly KeyHealthStore? health;
    private readonly TimeProvider time;
    private readonly Func<Exception, KeyFailureException?>? classifier;

    public KeySelector(IKeySource source, KeyHealthStore? health = null, TimeProvider? time = null,
        Func<Exception, KeyFailureException?>? classifier = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.health = health;
        this.time = time ?? TimeProvider.System;
        this.classifier = classifier;
    }

    /// <summary>Local files for keys and assignments, local health. What desktop apps use.</summary>
    public static KeySelector ForCurrentUser(Func<Exception, KeyFailureException?>? classifier = null) =>
        new(new FileKeySource(NamedKeyStore.ForRoamingRoot()), KeyHealthStore.ForLocalRoot(), classifier: classifier);

    /// <summary>The keys an app will try for a provider, in order.</summary>
    public KeyPlan Plan(string providerId, string? appId)
    {
        var provider = ProviderCatalog.Normalize(providerId);
        var all = source.GetKeys(provider);
        var assigned = string.IsNullOrWhiteSpace(appId) ? Array.Empty<string>() : source.GetAssignment(appId!, provider);
        var missing = new List<string>();
        IEnumerable<NamedKey> ordered;
        if (assigned.Count > 0)
        {
            var list = new List<NamedKey>();
            foreach (var name in assigned)
            {
                var key = all.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));
                if (key is null) missing.Add(name);
                else list.Add(key);
            }
            ordered = list;
        }
        else ordered = all;

        var now = time.GetUtcNow();
        var usable = new List<NamedKey>();
        var skipped = new List<KeyAttempt>();
        foreach (var key in ordered)
        {
            var status = health?.Get(provider, key.Name).StatusAt(now, key.Disabled)
                         ?? (key.Disabled ? KeyStatus.Disabled : KeyStatus.NeverUsed);
            if (status is KeyStatus.Disabled or KeyStatus.Failed or KeyStatus.CoolingDown)
                skipped.Add(new KeyAttempt(key.Name, status.ToString()));
            else usable.Add(key);
        }
        return new KeyPlan(usable, skipped, missing);
    }

    /// <summary>The key an app should use right now, or null. For call paths that can't be wrapped (e.g. streaming).</summary>
    public NamedKey? GetActiveKey(string providerId, string? appId) => Plan(providerId, appId).Usable.FirstOrDefault();

    /// <summary>Runs <paramref name="call"/> with each usable key until one succeeds.</summary>
    /// <exception cref="AllKeysFailedException">No key worked or none is configured.</exception>
    public async Task<T> ExecuteAsync<T>(string providerId, string? appId, Func<string, CancellationToken, Task<T>> call,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        var provider = ProviderCatalog.Normalize(providerId);
        var plan = Plan(provider, appId);
        var attempts = new List<KeyAttempt>(plan.Skipped);
        foreach (var key in plan.Usable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await call(key.Value, cancellationToken).ConfigureAwait(false);
                ReportSuccess(provider, key.Name);
                return result;
            }
            catch (Exception ex) when (Classify(ex) is { } failure && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                ReportFailure(provider, key.Name, failure.Kind, failure.RetryAfter);
                attempts.Add(new KeyAttempt(key.Name, failure.Kind.ToString()));
            }
        }
        throw new AllKeysFailedException(provider, appId, attempts);
    }

    /// <summary>
    /// Sends an HTTP request with each usable key until one returns a non-key-failure status.
    /// 401/402/403/429 responses are disposed and the next key is tried; any other response
    /// (success or not) is returned to the caller.
    /// </summary>
    public Task<HttpResponseMessage> SendAsync(string providerId, string? appId,
        Func<string, CancellationToken, Task<HttpResponseMessage>> send, CancellationToken cancellationToken = default) =>
        ExecuteAsync(providerId, appId, async (key, ct) =>
        {
            var response = await send(key, ct).ConfigureAwait(false);
            var kind = Classify(response.StatusCode);
            if (kind is null) return response;
            var retryAfter = RetryAfter(response, time.GetUtcNow());
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new KeyFailureException(kind.Value, $"HTTP {status}", retryAfter);
        }, cancellationToken);

    /// <summary>Records that a key worked (clears any failure).</summary>
    public void ReportSuccess(string providerId, string keyName) =>
        health?.RecordSuccess(providerId, keyName, time.GetUtcNow());

    /// <summary>Records that a key failed.</summary>
    public void ReportFailure(string providerId, string keyName, KeyFailureKind kind, TimeSpan? retryAfter = null) =>
        health?.RecordFailure(providerId, keyName, kind, time.GetUtcNow(), retryAfter);

    /// <summary>The key failure an HTTP status means, or null when the status isn't the key's fault.</summary>
    public static KeyFailureKind? Classify(HttpStatusCode status) => (int)status switch
    {
        401 or 403 => KeyFailureKind.Auth,
        402 => KeyFailureKind.Quota,
        429 => KeyFailureKind.RateLimited,
        _ => null,
    };

    private KeyFailureException? Classify(Exception ex)
    {
        if (ex is KeyFailureException kfe) return kfe;
        if (classifier?.Invoke(ex) is { } custom) return custom;
        if (ex is HttpRequestException { StatusCode: { } status } && Classify(status) is { } kind)
            return new KeyFailureException(kind, ex.GetType().Name);
        return null;
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) return date - now;
        return null;
    }
}
