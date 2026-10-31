using System.Text.Json.Nodes;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Vault.Desktop.Tests;

/// <summary>Records every question and notice; answers Confirm from a queue (default: <see cref="DefaultAnswer"/>).</summary>
public sealed class FakeDialogs : IDialogs
{
    public Queue<bool> Answers { get; } = new();
    public bool DefaultAnswer { get; set; } = true;
    public List<string> Confirms { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Infos { get; } = new();
    public List<string> Opened { get; } = new();

    public bool Confirm(string message, string caption)
    {
        Confirms.Add(message);
        return Answers.Count > 0 ? Answers.Dequeue() : DefaultAnswer;
    }

    public void Error(string message, string caption) => Errors.Add(message);
    public void Info(string message, string caption) => Infos.Add(message);
    public void OpenUrl(string url) => Opened.Add(url);
}

/// <summary>Runs posted work inline, under one lock so a test can read view-model state between watcher callbacks.</summary>
public sealed class InlineDispatcher : IUiDispatcher
{
    public object Gate { get; } = new();

    public void Post(Action action)
    {
        lock (Gate) action();
    }
}

/// <summary>A fixed set of manifests; <see cref="RaiseChanged"/> plays the watcher.</summary>
public sealed class FakeCatalog : IManifestCatalog
{
    public List<SettingsManifest> Manifests { get; } = new();
    public bool Started { get; private set; }
    public event EventHandler? Changed;
    public IReadOnlyList<SettingsManifest> Discover() => Manifests.ToList();
    public void Start() => Started = true;
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>An in-memory Profile: per-app documents with a counter version, and in-memory named keys.</summary>
public sealed class FakeProfile : IProfile
{
    public FakeProfile(string id, string? displayName = null)
    {
        Id = id;
        DisplayName = displayName ?? id;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public ProfileReadiness Readiness { get; set; } = ProfileReadiness.Ready;
    public FakeSettingsBackend SettingsStore { get; } = new();
    public FakeKeyBackend KeyStore { get; } = new();
    public ISettingsBackend Settings => SettingsStore;
    public IKeyBackend Keys => KeyStore;
    public string Actor { get; set; } = "TEST\\tester";
    public Task<ProfileReadiness> GetReadinessAsync(CancellationToken cancellationToken = default) => Task.FromResult(Readiness);
    public override string ToString() => DisplayName;
}

public sealed record SaveCall(string AppId, JsonObject Values, string? Version, IReadOnlyList<string> Changed);

public sealed class FakeSettingsBackend : ISettingsBackend
{
    private int version = 1;
    public Dictionary<string, JsonObject> Documents { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SaveCall> Saves { get; } = new();
    public int Loads { get; private set; }
    public Exception? ThrowOnSave { get; set; }
    public Exception? ThrowOnLoad { get; set; }
    public string WhenAppsSeeChanges => "after the fake refresh";

    public string CurrentVersion => "v" + version;

    /// <summary>Simulates another editor changing the stored values.</summary>
    public void ChangeElsewhere(string appId, JsonObject values)
    {
        Documents[appId] = values;
        version++;
    }

    public Task<SettingsDocument> LoadAsync(SettingsManifest manifest, CancellationToken cancellationToken = default)
    {
        Loads++;
        if (ThrowOnLoad is { } ex) return Task.FromException<SettingsDocument>(ex);
        var values = Documents.TryGetValue(manifest.AppId, out var d) ? (JsonObject)d.DeepClone() : DesktopSettingsBackend.Defaults(manifest);
        return Task.FromResult(new SettingsDocument(values, CurrentVersion));
    }

    public Task<string?> SaveAsync(SettingsManifest manifest, SettingsDocument document, IReadOnlyCollection<string> changedFields,
        CancellationToken cancellationToken = default)
    {
        if (ThrowOnSave is { } ex) return Task.FromException<string?>(ex);
        if (document.Version != CurrentVersion) return Task.FromException<string?>(new ConcurrencyConflictException("changed elsewhere"));
        Saves.Add(new SaveCall(manifest.AppId, (JsonObject)document.Values.DeepClone(), document.Version, changedFields.ToList()));
        var target = Documents.TryGetValue(manifest.AppId, out var d) ? d : new JsonObject();
        foreach (var name in changedFields) SettingsDocumentMerge.Apply(document.Values, target, manifest.ConfigurationPrefix, name);
        Documents[manifest.AppId] = target;
        version++;
        return Task.FromResult<string?>(CurrentVersion);
    }
}

public sealed class FakeKeyBackend : IKeyBackend
{
    public Dictionary<string, List<NamedKey>> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<(string App, string Provider), List<string>> Assignments { get; } = new();

    private List<NamedKey> List(string provider)
    {
        var id = ProviderCatalog.Normalize(provider);
        if (!Keys.TryGetValue(id, out var list)) Keys[id] = list = new List<NamedKey>();
        return list;
    }

    private int IndexOf(string provider, string name)
    {
        var i = List(provider).FindIndex(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : throw new KeyNotFoundException($"No key named '{name}'.");
    }

    public Task<IReadOnlyList<string>> ProvidersWithKeysAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Keys.Where(k => k.Value.Count > 0).Select(k => k.Key).ToList());

    public Task<IReadOnlyList<NamedKey>> GetKeysAsync(string providerId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NamedKey>>(List(providerId).ToList());

    public Task AddAsync(string providerId, string name, string value, CancellationToken cancellationToken = default)
    {
        var clean = NamedKeyStore.ValidateName(name, List(providerId), null);
        List(providerId).Add(new NamedKey(clean, value));
        return Task.CompletedTask;
    }

    public Task SetValueAsync(string providerId, string name, string value, CancellationToken cancellationToken = default)
    {
        var i = IndexOf(providerId, name);
        List(providerId)[i] = List(providerId)[i] with { Value = value };
        return Task.CompletedTask;
    }

    public Task RenameAsync(string providerId, string oldName, string newName, CancellationToken cancellationToken = default)
    {
        var i = IndexOf(providerId, oldName);
        var clean = NamedKeyStore.ValidateName(newName, List(providerId), List(providerId)[i].Name);
        List(providerId)[i] = List(providerId)[i] with { Name = clean };
        foreach (var names in Assignments.Values)
            for (var n = 0; n < names.Count; n++)
                if (string.Equals(names[n], oldName, StringComparison.OrdinalIgnoreCase)) names[n] = clean;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string providerId, string name, CancellationToken cancellationToken = default)
    {
        var apps = Assignments.Where(a => a.Key.Provider == ProviderCatalog.Normalize(providerId) && a.Value.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Select(a => a.Key.App).ToList();
        if (apps.Count > 0) throw new KeyInUseException(providerId, name, apps);
        List(providerId).RemoveAt(IndexOf(providerId, name));
        return Task.CompletedTask;
    }

    public Task SetDisabledAsync(string providerId, string name, bool disabled, CancellationToken cancellationToken = default)
    {
        var i = IndexOf(providerId, name);
        List(providerId)[i] = List(providerId)[i] with { Disabled = disabled };
        return Task.CompletedTask;
    }

    public Task ReorderAsync(string providerId, IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        var list = List(providerId);
        var ordered = names.Select(n => list[IndexOf(providerId, n)]).ToList();
        list.Clear();
        list.AddRange(ordered);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetAssignmentAsync(string appId, string providerId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Assignments.TryGetValue((appId, ProviderCatalog.Normalize(providerId)), out var n) ? n.ToList() : new List<string>());

    public Task SetAssignmentAsync(string appId, string providerId, IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        if (names.Count == 0) Assignments.Remove((appId, ProviderCatalog.Normalize(providerId)));
        else Assignments[(appId, ProviderCatalog.Normalize(providerId))] = names.ToList();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> AppsUsingAsync(string providerId, string name, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Assignments
            .Where(a => a.Key.Provider == ProviderCatalog.Normalize(providerId) && a.Value.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Select(a => a.Key.App).OrderBy(a => a).ToList());
}
