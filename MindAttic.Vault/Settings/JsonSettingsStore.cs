using System.Text.Json;
using MindAttic.Vault.Paths;

namespace MindAttic.Vault.Settings;

/// <summary>
/// Generic JSON settings file backed by a single object of type <typeparamref name="T"/>.
/// Replaces hand-rolled <c>Load()/Save()</c> code in every MindAttic app's
/// <c>AppSettings</c> / <c>SettingsService</c> implementation.
///
/// File location: <c>{directory}/{fileName}</c>. By convention:
/// <list type="bullet">
///   <item>Per-app config: <c>%LOCALAPPDATA%\MindAttic\&lt;app&gt;\settings.json</c></item>
///   <item>Roaming config: <c>%APPDATA%\MindAttic\&lt;bucket&gt;\settings.json</c></item>
/// </list>
///
/// Reads are best-effort: a missing or malformed file yields a default-constructed
/// <typeparamref name="T"/>. Writes are pretty-printed with camelCase property names
/// and serialized under a per-instance lock so concurrent saves don't tear the file.
/// </summary>
public class JsonSettingsStore<T> where T : class, new()
{
    public const string DefaultFileName = "settings.json";

    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object writeLock = new();
    private readonly JsonSerializerOptions jsonOptions;

    public string Directory { get; }
    public string FileName  { get; }
    public string FilePath  => Path.Combine(Directory, FileName);

    public JsonSettingsStore(string directory, string fileName = DefaultFileName, JsonSerializerOptions? jsonOptions = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Directory is required.", nameof(directory));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("File name is required.", nameof(fileName));

        Directory        = directory;
        FileName         = fileName;
        this.jsonOptions = jsonOptions ?? DefaultJsonOptions;
    }

    /// <summary>
    /// Convenience factory for per-app settings under <c>%APPDATA%\MindAttic\&lt;app&gt;\</c>.
    /// Settings stay roaming by design: only secrets move into <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
    /// for the cloud-native flow; user-facing preferences (theme, layout, etc.) follow the user across machines.
    /// </summary>
    public static JsonSettingsStore<T> ForApp(string app, string fileName = DefaultFileName) =>
        new(VaultPaths.RoamingBucket(app), fileName);

    /// <summary>
    /// Convenience factory for an explicit local-only path under <c>%LOCALAPPDATA%\MindAttic\&lt;app&gt;\</c>.
    /// Use for caches, evidence files, or any per-machine state that should NOT roam.
    /// </summary>
    public static JsonSettingsStore<T> ForLocalApp(string app, string fileName = DefaultFileName) =>
        new(VaultPaths.LocalApp(app), fileName);

    /// <summary>Convenience factory for roaming settings under <c>%APPDATA%\MindAttic\&lt;bucket&gt;\</c>.</summary>
    public static JsonSettingsStore<T> ForBucket(string bucket, string fileName = DefaultFileName) =>
        new(VaultPaths.RoamingBucket(bucket), fileName);

    /// <summary>True if the underlying settings file exists.</summary>
    public bool Exists() => File.Exists(FilePath);

    /// <summary>
    /// Loads settings from disk, returning a default-constructed <typeparamref name="T"/>
    /// if the file is missing or unparseable. Does not create the directory.
    /// </summary>
    public T Load()
    {
        if (!File.Exists(FilePath)) return new T();

        try
        {
            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json)) return new T();
            return JsonSerializer.Deserialize<T>(json, jsonOptions) ?? new T();
        }
        catch
        {
            return new T();
        }
    }

    /// <summary>
    /// Loads settings, then invokes <paramref name="overlay"/> to layer environment
    /// variables (or any other source) on top before returning.
    /// </summary>
    public T LoadWithOverlay(Action<T> overlay)
    {
        var settings = Load();
        overlay?.Invoke(settings);
        return settings;
    }

    /// <summary>Persists <paramref name="settings"/> to disk, creating the directory if needed.</summary>
    public void Save(T settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));

        lock (writeLock)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var json = JsonSerializer.Serialize(settings, jsonOptions);
            File.WriteAllText(FilePath, json);
        }
    }

    /// <summary>
    /// Read-modify-write helper: loads the current settings, applies <paramref name="mutate"/>,
    /// and saves the result. Useful for one-shot updates from UI code.
    /// </summary>
    public T Update(Action<T> mutate)
    {
        if (mutate is null) throw new ArgumentNullException(nameof(mutate));
        lock (writeLock)
        {
            var settings = Load();
            mutate(settings);
            Save(settings);
            return settings;
        }
    }
}
