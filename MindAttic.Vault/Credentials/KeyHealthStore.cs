using System.Text.Json;
using System.Text.Json.Serialization;
using MindAttic.Vault.Paths;

namespace MindAttic.Vault.Credentials;

/// <summary>Why a key call failed, as far as the key is concerned.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<KeyFailureKind>))]
public enum KeyFailureKind
{
    /// <summary>401/403: the key is wrong or revoked. Needs the user.</summary>
    Auth,
    /// <summary>402 or an explicit quota/billing error. Needs the user.</summary>
    Quota,
    /// <summary>429: back off until the cool-down ends.</summary>
    RateLimited,
}

/// <summary>A key's status as Vault.Desktop shows it.</summary>
public enum KeyStatus { NeverUsed, Healthy, CoolingDown, Failed, Disabled }

/// <summary>Observed health of one named key. Holds no key material.</summary>
public sealed class KeyHealth
{
    public DateTimeOffset? LastSuccessUtc { get; set; }
    public DateTimeOffset? LastFailureUtc { get; set; }
    public KeyFailureKind? FailureKind { get; set; }
    public DateTimeOffset? CoolDownUntilUtc { get; set; }

    /// <summary>Set by an Auth or Quota failure; cleared by a success or by the user.</summary>
    public bool Failed { get; set; }

    /// <summary>The status at <paramref name="now"/> for a key that may be <paramref name="disabled"/>.</summary>
    public KeyStatus StatusAt(DateTimeOffset now, bool disabled) =>
        disabled ? KeyStatus.Disabled
        : Failed ? KeyStatus.Failed
        : CoolDownUntilUtc > now ? KeyStatus.CoolingDown
        : LastSuccessUtc is null && LastFailureUtc is null ? KeyStatus.NeverUsed
        : KeyStatus.Healthy;
}

/// <summary>
/// Key health shared by every app on the machine, keyed by (provider, key name), never by key or
/// hash: <c>%LOCALAPPDATA%\MindAttic\Vault\key-health.json</c>. A key one app finds broken is skipped
/// by all. Read-modify-writes are serialized across processes by a named mutex and written atomically.
/// </summary>
public sealed class KeyHealthStore
{
    public const string FileName = "key-health.json";
    public static readonly TimeSpan DefaultCoolDown = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxCoolDown = TimeSpan.FromHours(6);

    // A success only needs persisting when it changes something or the timestamp is stale; this keeps
    // a busy app from rewriting the file on every call.
    private static readonly TimeSpan SuccessWriteInterval = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string mutexName;

    public KeyHealthStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Directory is required.", nameof(directory));
        Directory = directory;
        mutexName = @"Local\MindAttic.Vault.KeyHealth." + StableHash(Path.GetFullPath(directory));
    }

    /// <summary>The store under the current local root.</summary>
    public static KeyHealthStore ForLocalRoot() => new(VaultPaths.LocalApp(KeyAssignmentStore.Bucket));

    public string Directory { get; }
    public string FilePath => Path.Combine(Directory, FileName);

    /// <summary>The key's health (a fresh, never-used record when none is stored).</summary>
    public KeyHealth Get(string providerId, string keyName) =>
        Read().TryGetValue(ProviderCatalog.Normalize(providerId), out var byName) && byName.TryGetValue(keyName, out var h)
            ? h
            : new KeyHealth();

    /// <summary>Every stored record for the provider, by key name.</summary>
    public IReadOnlyDictionary<string, KeyHealth> GetAll(string providerId) =>
        Read().TryGetValue(ProviderCatalog.Normalize(providerId), out var byName)
            ? byName
            : new Dictionary<string, KeyHealth>(StringComparer.OrdinalIgnoreCase);

    public void RecordSuccess(string providerId, string keyName, DateTimeOffset now)
    {
        var current = Get(providerId, keyName);
        if (!current.Failed && current.CoolDownUntilUtc is null && current.FailureKind is null
            && current.LastSuccessUtc is { } last && now - last < SuccessWriteInterval)
            return;
        Mutate(providerId, keyName, h =>
        {
            h.LastSuccessUtc = now;
            h.Failed = false;
            h.FailureKind = null;
            h.CoolDownUntilUtc = null;
        });
    }

    public void RecordFailure(string providerId, string keyName, KeyFailureKind kind, DateTimeOffset now, TimeSpan? retryAfter = null) =>
        Mutate(providerId, keyName, h =>
        {
            h.LastFailureUtc = now;
            h.FailureKind = kind;
            if (kind == KeyFailureKind.RateLimited)
            {
                var wait = retryAfter is { } r && r > TimeSpan.Zero ? (r > MaxCoolDown ? MaxCoolDown : r) : DefaultCoolDown;
                h.CoolDownUntilUtc = now + wait;
            }
            else h.Failed = true;
        });

    /// <summary>Clears a key's failure and cool-down (after the user fixes it, or a passing Test).</summary>
    public void Reset(string providerId, string keyName) =>
        Mutate(providerId, keyName, h =>
        {
            h.Failed = false;
            h.FailureKind = null;
            h.CoolDownUntilUtc = null;
        });

    public void Rename(string providerId, string oldName, string newName)
    {
        var provider = ProviderCatalog.Normalize(providerId);
        Locked(all =>
        {
            if (!all.TryGetValue(provider, out var byName) || !byName.Remove(oldName, out var h)) return false;
            byName[newName] = h;
            return true;
        });
    }

    /// <summary>Keeps health following <paramref name="keys"/>' renames and removals.</summary>
    public void Track(NamedKeyStore keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        keys.KeyRenamed += Rename;
        keys.KeyRemoved += Remove;
    }

    public void Remove(string providerId, string keyName)
    {
        var provider = ProviderCatalog.Normalize(providerId);
        Locked(all => all.TryGetValue(provider, out var byName) && byName.Remove(keyName));
    }

    private void Mutate(string providerId, string keyName, Action<KeyHealth> change)
    {
        var provider = ProviderCatalog.Normalize(providerId);
        Locked(all =>
        {
            if (!all.TryGetValue(provider, out var byName))
                all[provider] = byName = new Dictionary<string, KeyHealth>(StringComparer.OrdinalIgnoreCase);
            if (!byName.TryGetValue(keyName, out var h)) byName[keyName] = h = new KeyHealth();
            change(h);
            return true;
        });
    }

    private void Locked(Func<Dictionary<string, Dictionary<string, KeyHealth>>, bool> change)
    {
        try
        {
            using var mutex = new Mutex(false, mutexName);
            var owned = false;
            try
            {
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { owned = true; }
                var all = Read();
                if (change(all)) AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(all, Options));
            }
            finally
            {
                if (owned) mutex.ReleaseMutex();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private Dictionary<string, Dictionary<string, KeyHealth>> Read()
    {
        var result = new Dictionary<string, Dictionary<string, KeyHealth>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(FilePath)) return result;
            var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, KeyHealth>>>(File.ReadAllText(FilePath), Options);
            if (raw is null) return result;
            foreach (var (provider, byName) in raw)
                result[provider] = new Dictionary<string, KeyHealth>(byName, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Unreadable health means "nothing known yet"; it is observed state, rebuilt by use.
        }
        return result;
    }

    private static string StableHash(string text)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in text.ToUpperInvariant()) hash = (hash ^ c) * 16777619u;
            return hash.ToString("x8");
        }
    }
}
