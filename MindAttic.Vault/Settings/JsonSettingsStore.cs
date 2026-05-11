using System.Text.Json;
using MindAttic.Vault.Paths;

namespace MindAttic.Vault.Settings;

/// <summary>
/// Generic JSON settings file backed by a single object of type
/// <typeparamref name="T"/>. Replaces hand-rolled <c>Load()/Save()</c> code
/// in every MindAttic app's <c>AppSettings</c> / <c>SettingsService</c>
/// implementation.
///
/// <para><b>File location:</b> <c>{directory}/{fileName}</c>. By convention:</para>
/// <list type="bullet">
///   <item><description>Per-app config: <c>%APPDATA%\MindAttic\&lt;app&gt;\settings.json</c> (roaming, follows the user)</description></item>
///   <item><description>Local-only config: <c>%LOCALAPPDATA%\MindAttic\&lt;app&gt;\settings.json</c> (per-machine)</description></item>
/// </list>
///
/// <para><b>IO posture:</b> reads are best-effort — a missing or malformed file
/// yields a default-constructed <typeparamref name="T"/>. Writes are
/// pretty-printed with camelCase property names and serialised under a
/// per-instance lock so concurrent saves don't tear the file.</para>
///
/// <para><b>Cloud-native rationale:</b> only secrets move into
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>; user-facing
/// settings (theme, layout, preferences) stay roaming on disk so they follow
/// the user across machines.</para>
/// </summary>
/// <typeparam name="T">
/// The settings POCO. Must have a public parameterless constructor — used to
/// produce defaults when the file is missing or unparseable.
/// </typeparam>
public class JsonSettingsStore<T> where T : class, new()
{
    /// <summary>Default file name used by <see cref="ForApp"/> / <see cref="ForBucket"/>.</summary>
    public const string DefaultFileName = "settings.json";

    // Indented + camelCase output is the MindAttic-wide convention for human-edited
    // settings files. Consumers who want different output pass jsonOptions to the
    // constructor.
    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object writeLock = new();
    private readonly JsonSerializerOptions jsonOptions;

    /// <summary>The directory containing the settings file.</summary>
    public string Directory { get; }

    /// <summary>The settings file name (without directory).</summary>
    public string FileName  { get; }

    /// <summary>Absolute path to the settings file (<see cref="Directory"/> + <see cref="FileName"/>).</summary>
    public string FilePath  => Path.Combine(Directory, FileName);

    /// <summary>Construct a settings store at <paramref name="directory"/>/<paramref name="fileName"/>.</summary>
    /// <param name="directory">Containing directory. Required. Created on first save.</param>
    /// <param name="fileName">File name. Required. Defaults to <see cref="DefaultFileName"/>.</param>
    /// <param name="jsonOptions">
    /// Optional JSON serialisation options. Defaults to
    /// <c>WriteIndented=true, PropertyNamingPolicy=CamelCase</c>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="directory"/> or <paramref name="fileName"/> is null/whitespace.
    /// </exception>
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
    /// Convenience factory for per-app settings under
    /// <c>%APPDATA%\MindAttic\&lt;app&gt;\</c>. Settings stay roaming by design:
    /// only secrets move into
    /// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> for the
    /// cloud-native flow; user-facing preferences (theme, layout, etc.) follow
    /// the user across machines.
    /// </summary>
    /// <param name="app">App folder name. Required.</param>
    /// <param name="fileName">File name. Defaults to <see cref="DefaultFileName"/>.</param>
    public static JsonSettingsStore<T> ForApp(string app, string fileName = DefaultFileName) =>
        new(VaultPaths.RoamingBucket(app), fileName);

    /// <summary>
    /// Convenience factory for an explicit local-only path under
    /// <c>%LOCALAPPDATA%\MindAttic\&lt;app&gt;\</c>. Use for caches, evidence
    /// files, or any per-machine state that should NOT roam.
    /// </summary>
    /// <param name="app">App folder name. Required.</param>
    /// <param name="fileName">File name. Defaults to <see cref="DefaultFileName"/>.</param>
    public static JsonSettingsStore<T> ForLocalApp(string app, string fileName = DefaultFileName) =>
        new(VaultPaths.LocalApp(app), fileName);

    /// <summary>
    /// Convenience factory for roaming settings under
    /// <c>%APPDATA%\MindAttic\&lt;bucket&gt;\</c>.
    /// </summary>
    /// <param name="bucket">Bucket folder name. Required.</param>
    /// <param name="fileName">File name. Defaults to <see cref="DefaultFileName"/>.</param>
    public static JsonSettingsStore<T> ForBucket(string bucket, string fileName = DefaultFileName) =>
        new(VaultPaths.RoamingBucket(bucket), fileName);

    /// <summary>True if the underlying settings file exists.</summary>
    public bool Exists() => File.Exists(FilePath);

    /// <summary>
    /// Loads settings from disk, returning a default-constructed
    /// <typeparamref name="T"/> if the file is missing or unparseable. Does not
    /// create the directory.
    /// </summary>
    /// <returns>The deserialized settings, or a fresh <c>new T()</c> on any failure.</returns>
    public T Load()
    {
        if (!File.Exists(FilePath)) return new T();

        try
        {
            var json = File.ReadAllText(FilePath);
            // Empty file is a degenerate but valid state — treat as defaults.
            if (string.IsNullOrWhiteSpace(json)) return new T();
            return JsonSerializer.Deserialize<T>(json, jsonOptions) ?? new T();
        }
        catch
        {
            // Swallow: a malformed settings file should never crash the host.
            return new T();
        }
    }

    /// <summary>
    /// Loads settings, then invokes <paramref name="overlay"/> to layer
    /// environment variables (or any other source) on top before returning.
    /// </summary>
    /// <param name="overlay">
    /// Mutator invoked with the loaded settings. May be null (then equivalent
    /// to <see cref="Load"/>).
    /// </param>
    /// <returns>The (possibly mutated) settings instance.</returns>
    public T LoadWithOverlay(Action<T> overlay)
    {
        var settings = Load();
        overlay?.Invoke(settings);
        return settings;
    }

    /// <summary>Persists <paramref name="settings"/> to disk, creating the directory if needed.</summary>
    /// <param name="settings">The settings to persist. Required.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="settings"/> is null.</exception>
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
    /// Read-modify-write helper: loads the current settings, applies
    /// <paramref name="mutate"/>, and saves the result. Useful for one-shot
    /// updates from UI code.
    /// </summary>
    /// <param name="mutate">The mutation to apply. Required.</param>
    /// <returns>The saved (post-mutation) settings instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="mutate"/> is null.</exception>
    public T Update(Action<T> mutate)
    {
        if (mutate is null) throw new ArgumentNullException(nameof(mutate));
        // Hold the same lock as Save so a concurrent Update can't read a torn
        // file or race past another thread's mutation.
        lock (writeLock)
        {
            var settings = Load();
            mutate(settings);
            Save(settings);
            return settings;
        }
    }
}
