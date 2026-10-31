using MindAttic.Vault.Paths;

namespace MindAttic.Vault.Credentials;

/// <summary>One key in a provider's shared list. Its <see cref="Name"/> is its identity everywhere.</summary>
public sealed record NamedKey(string Name, string Value, bool Disabled = false)
{
    /// <summary>Never includes the value, so a logged key can't leak.</summary>
    public override string ToString() => Disabled ? $"{Name} (disabled)" : Name;
}

/// <summary>Thrown when removing a key that apps still have assigned.</summary>
public sealed class KeyInUseException : InvalidOperationException
{
    public KeyInUseException(string providerId, string keyName, IReadOnlyList<string> apps)
        : base($"Key '{keyName}' ({providerId}) is assigned to {string.Join(", ", apps)}; reassign those apps first.")
    {
        ProviderId = providerId;
        KeyName = keyName;
        Apps = apps;
    }

    public string ProviderId { get; }
    public string KeyName { get; }
    public IReadOnlyList<string> Apps { get; }
}

/// <summary>
/// The workspace-wide named key list per provider: the existing provider pools in each bucket's
/// <c>providers.json</c>, with every entry given a unique name (unlabeled entries become
/// <c>key1</c>, <c>key2</c>â€¦) and aliases (<c>anthropic</c> â†’ <c>claude</c>) merged into one list.
/// List order is the default failover order. Writes consolidate onto the canonical provider id.
/// Keys are only ever stored here; apps reference them by name through <see cref="KeyAssignmentStore"/>.
/// </summary>
public sealed class NamedKeyStore
{
    public const int MaxNameLength = 100;

    private readonly string roamingRoot;
    private readonly object gate = new();

    public NamedKeyStore(string roamingRoot, KeyAssignmentStore assignments)
    {
        if (string.IsNullOrWhiteSpace(roamingRoot)) throw new ArgumentException("Root is required.", nameof(roamingRoot));
        this.roamingRoot = roamingRoot;
        Assignments = assignments ?? throw new ArgumentNullException(nameof(assignments));
    }

    /// <summary>The store under the current roaming root.</summary>
    public static NamedKeyStore ForRoamingRoot() =>
        new(VaultPaths.RoamingRoot, new KeyAssignmentStore(Path.Combine(VaultPaths.RoamingRoot, KeyAssignmentStore.Bucket)));

    public KeyAssignmentStore Assignments { get; }

    /// <summary>Raised after a rename, with (provider, oldName, newName).</summary>
    public event Action<string, string, string>? KeyRenamed;

    /// <summary>Raised after a removal, with (provider, name).</summary>
    public event Action<string, string>? KeyRemoved;

    /// <summary>The credential store backing a provider's bucket.</summary>
    public CredentialStore StoreFor(string providerId)
    {
        var info = ProviderCatalog.Get(providerId);
        var dir = Path.Combine(roamingRoot, info.Bucket);
        return info.Bucket switch
        {
            LlmCredentialStore.Bucket => new LlmCredentialStore(dir),
            BrokerCredentialStore.Bucket => new BrokerCredentialStore(dir),
            _ => new CredentialStore(dir),
        };
    }

    /// <summary>The provider's keys in failover order, aliases merged, every one named.</summary>
    public IReadOnlyList<NamedKey> GetKeys(string providerId)
    {
        var info = ProviderCatalog.Get(providerId);
        var store = StoreFor(info.Id);
        var entries = new List<CredentialPoolEntry>();
        foreach (var id in ProviderCatalog.IdsFor(info.Id))
            foreach (var e in store.GetKeys(id))
                if (!entries.Any(x => x.Key == e.Key)) entries.Add(e);
        return Name(entries);
    }

    /// <summary>The provider's key with this name, or null.</summary>
    public NamedKey? Find(string providerId, string name) =>
        GetKeys(providerId).FirstOrDefault(k => string.Equals(k.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Providers that have at least one key, canonical ids.</summary>
    public IReadOnlyList<string> ProvidersWithKeys()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bucket in new[] { LlmCredentialStore.Bucket, BrokerCredentialStore.Bucket, ProviderCatalog.ServicesBucket })
        {
            var dir = Path.Combine(roamingRoot, bucket);
            if (!System.IO.Directory.Exists(dir)) continue;
            foreach (var id in new CredentialStore(dir).LoadAllRaw().Keys)
            {
                var canonical = ProviderCatalog.Normalize(id);
                if (GetKeys(canonical).Count > 0) ids.Add(canonical);
            }
        }
        return ids.OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Adds a key at the end of the list (or at <paramref name="index"/>).</summary>
    public NamedKey Add(string providerId, string name, string value, int? index = null)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Key value is required.", nameof(value));
        lock (gate)
        {
            var keys = GetKeys(providerId).ToList();
            var clean = ValidateName(name, keys, except: null);
            var key = new NamedKey(clean, value.Trim());
            keys.Insert(Math.Clamp(index ?? keys.Count, 0, keys.Count), key);
            Write(providerId, keys);
            return key;
        }
    }

    /// <summary>Replaces a key's value, keeping its name, position and assignments.</summary>
    public void SetValue(string providerId, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Key value is required.", nameof(value));
        Change(providerId, name, k => k with { Value = value.Trim() });
    }

    /// <summary>Disables or re-enables a key without removing it.</summary>
    public void SetDisabled(string providerId, string name, bool disabled) =>
        Change(providerId, name, k => k with { Disabled = disabled });

    /// <summary>Renames a key; every app assignment follows it.</summary>
    public void Rename(string providerId, string oldName, string newName)
    {
        var provider = ProviderCatalog.Normalize(providerId);
        string clean;
        lock (gate)
        {
            var keys = GetKeys(provider).ToList();
            var i = IndexOf(keys, oldName);
            clean = ValidateName(newName, keys, except: keys[i].Name);
            var old = keys[i].Name;
            if (old == clean) return;
            keys[i] = keys[i] with { Name = clean };
            Write(provider, keys);
            Assignments.RenameKey(provider, old, clean);
            oldName = old;
        }
        KeyRenamed?.Invoke(provider, oldName, clean);
    }

    /// <summary>Removes a key. Refused while any app has it assigned.</summary>
    /// <exception cref="KeyInUseException">The key is assigned to one or more apps.</exception>
    public void Remove(string providerId, string name)
    {
        var provider = ProviderCatalog.Normalize(providerId);
        string removed;
        lock (gate)
        {
            var keys = GetKeys(provider).ToList();
            var i = IndexOf(keys, name);
            removed = keys[i].Name;
            var apps = Assignments.AppsUsing(provider, removed);
            if (apps.Count > 0) throw new KeyInUseException(provider, removed, apps);
            keys.RemoveAt(i);
            Write(provider, keys);
        }
        KeyRemoved?.Invoke(provider, removed);
    }

    /// <summary>Sets the default failover order. <paramref name="names"/> must name every key exactly once.</summary>
    public void Reorder(string providerId, IReadOnlyList<string> names)
    {
        lock (gate)
        {
            var keys = GetKeys(providerId).ToList();
            if (names.Count != keys.Count || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Count)
                throw new ArgumentException("Reorder must name every key exactly once.", nameof(names));
            Write(providerId, names.Select(n => keys[IndexOf(keys, n)]).ToList());
        }
    }

    /// <summary>The key value used for a name check: trimmed, non-empty, no ':', unique per provider.</summary>
    public static string ValidateName(string name, IEnumerable<NamedKey> existing, string? except)
    {
        var clean = name?.Trim() ?? "";
        if (clean.Length == 0) throw new ArgumentException("Key name is required.", nameof(name));
        if (clean.Contains(':')) throw new ArgumentException("Key names can't contain ':'.", nameof(name));
        if (clean.Length > MaxNameLength) throw new ArgumentException($"Key names are at most {MaxNameLength} characters.", nameof(name));
        if (existing.Any(k => !string.Equals(k.Name, except, StringComparison.OrdinalIgnoreCase)
                              && string.Equals(k.Name, clean, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A key named '{clean}' already exists.", nameof(name));
        return clean;
    }

    private void Change(string providerId, string name, Func<NamedKey, NamedKey> change)
    {
        lock (gate)
        {
            var keys = GetKeys(providerId).ToList();
            var i = IndexOf(keys, name);
            keys[i] = change(keys[i]);
            Write(providerId, keys);
        }
    }

    private static int IndexOf(List<NamedKey> keys, string name)
    {
        var i = keys.FindIndex(k => string.Equals(k.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (i < 0) throw new KeyNotFoundException($"No key named '{name}'.");
        return i;
    }

    private void Write(string providerId, IReadOnlyList<NamedKey> keys)
    {
        var info = ProviderCatalog.Get(providerId);
        var store = StoreFor(info.Id);
        store.SetKeys(info.Id, keys.Select(k => new CredentialPoolEntry(k.Value, k.Name, k.Disabled)).ToList());

        // Alias entries were merged into the canonical list on read; drop them so they can't diverge.
        var raw = store.LoadAllRaw();
        var aliases = info.Aliases.Where(a => raw.ContainsKey(a)).ToList();
        if (aliases.Count > 0)
        {
            foreach (var a in aliases) raw.Remove(a);
            store.SaveAllRaw(raw);
        }
    }

    internal static List<NamedKey> Name(List<CredentialPoolEntry> entries)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var named = new List<NamedKey>(entries.Count);
        var pending = new List<int>();
        for (var i = 0; i < entries.Count; i++)
        {
            var label = entries[i].Label?.Trim();
            if (string.IsNullOrEmpty(label) || label.Contains(':'))
            {
                named.Add(null!);
                pending.Add(i);
                continue;
            }
            var unique = label;
            for (var n = 2; used.Contains(unique); n++) unique = $"{label} ({n})";
            used.Add(unique);
            named.Add(new NamedKey(unique, entries[i].Key, entries[i].Disabled));
        }

        var next = 1;
        foreach (var i in pending)
        {
            while (used.Contains($"key{next}")) next++;
            var name = $"key{next++}";
            used.Add(name);
            named[i] = new NamedKey(name, entries[i].Key, entries[i].Disabled);
        }
        return named;
    }
}
