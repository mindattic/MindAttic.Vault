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

    // SemaphoreSlim (not a monitor) so the async overloads can await acquisition
    // with a CancellationToken — sync and async paths share the same gate, so
    // they're mutually exclusive against each other and against themselves.
    private readonly SemaphoreSlim writeLock = new(1, 1);
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
        writeLock.Wait();
        try { SaveLocked(settings); }
        finally { writeLock.Release(); }
    }

    /// <summary>
    /// Async variant of <see cref="Save"/>. Honors cancellation while acquiring
    /// the write gate and during the underlying disk write.
    /// </summary>
    /// <param name="settings">The settings to persist. Required.</param>
    /// <param name="cancellationToken">Cooperative cancellation. Optional.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="settings"/> is null.</exception>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is requested.</exception>
    public async Task SaveAsync(T settings, CancellationToken cancellationToken = default)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SaveLockedAsync(settings, cancellationToken).ConfigureAwait(false); }
        finally { writeLock.Release(); }
    }

    // Assumes writeLock is held — the actual atomic write. Callers that already
    // own the lock (e.g. Update) invoke this directly instead of re-entering Save,
    // so the lock contract doesn't rely on monitor reentrancy.
    private void SaveLocked(T settings)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var json = JsonSerializer.Serialize(settings, jsonOptions);

        // Atomic swap: a reader process must never see a half-written settings.json
        // (which would parse-fail and silently report defaults).
        var tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        if (File.Exists(FilePath))
            File.Replace(tempPath, FilePath, FilePath + ".bak");
        else
            File.Move(tempPath, FilePath);
    }

    // Async twin of SaveLocked. Caller owns the SemaphoreSlim.
    private async Task SaveLockedAsync(T settings, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var json = JsonSerializer.Serialize(settings, jsonOptions);

        var tempPath = FilePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        // File.Replace / File.Move have no async variant in BCL; the atomic
        // rename itself is sub-millisecond so cancellation isn't honored here.
        if (File.Exists(FilePath))
            File.Replace(tempPath, FilePath, FilePath + ".bak");
        else
            File.Move(tempPath, FilePath);
    }

    /// <summary>
    /// Async variant of <see cref="Load"/>. Returns defaults on cancellation only
    /// if cancellation fires after the file was already read; an early-cancelled
    /// token still throws <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="cancellationToken">Cooperative cancellation. Optional.</param>
    /// <returns>The deserialized settings, or a fresh <c>new T()</c> on any failure.</returns>
    public async Task<T> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(FilePath)) return new T();
        try
        {
            var json = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return new T();
            return JsonSerializer.Deserialize<T>(json, jsonOptions) ?? new T();
        }
        catch (OperationCanceledException) { throw; }
        catch { return new T(); }
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
        // One acquisition for the full read-modify-write — SaveLocked is called
        // directly so we never depend on lock reentrancy.
        writeLock.Wait();
        try
        {
            var settings = Load();
            mutate(settings);
            SaveLocked(settings);
            return settings;
        }
        finally { writeLock.Release(); }
    }

    /// <summary>
    /// Async variant of <see cref="Update"/>. The mutator may be async; the read
    /// and write halves are wrapped in a single semaphore acquisition so a
    /// concurrent Update can't race past another mutation.
    /// </summary>
    /// <param name="mutate">The async mutation to apply. Required.</param>
    /// <param name="cancellationToken">Cooperative cancellation. Optional.</param>
    /// <returns>The saved (post-mutation) settings instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="mutate"/> is null.</exception>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is requested.</exception>
    public async Task<T> UpdateAsync(Func<T, CancellationToken, Task> mutate, CancellationToken cancellationToken = default)
    {
        if (mutate is null) throw new ArgumentNullException(nameof(mutate));
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await LoadAsyncLocked(cancellationToken).ConfigureAwait(false);
            await mutate(settings, cancellationToken).ConfigureAwait(false);
            await SaveLockedAsync(settings, cancellationToken).ConfigureAwait(false);
            return settings;
        }
        finally { writeLock.Release(); }
    }

    // Lock-free Load — used inside UpdateAsync where the semaphore is already held.
    // Logic mirrors LoadAsync exactly; extracted purely to avoid pulling the
    // public LoadAsync's cancellation-precondition into the locked critical section
    // (where it would already have thrown).
    private async Task<T> LoadAsyncLocked(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath)) return new T();
        try
        {
            var json = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return new T();
            return JsonSerializer.Deserialize<T>(json, jsonOptions) ?? new T();
        }
        catch (OperationCanceledException) { throw; }
        catch { return new T(); }
    }
}
