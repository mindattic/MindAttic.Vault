namespace MindAttic.Vault.Settings;

/// <summary>
/// One app's settings object, persisted by a backend (the local JSON file today, Azure App
/// Configuration through MindAttic.Vault.Desktop's Azure Profile). Never holds secrets: keys live in
/// the credential stores.
/// </summary>
/// <typeparam name="T">The settings class. Its property initializers are the first-run defaults.</typeparam>
public interface ISettingsStore<T> where T : class, new()
{
    /// <summary>Where the settings live, for diagnostics (a file path or a backend description).</summary>
    string FilePath { get; }

    /// <summary>True when the backend already holds saved settings.</summary>
    bool Exists();

    /// <summary>The saved settings, or <c>new T()</c> when none are saved or they can't be read. Never throws for I/O.</summary>
    T Load();

    /// <summary>Async <see cref="Load"/>.</summary>
    Task<T> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Strict <see cref="Load"/> for callers that load, change and then save the whole object: returns
    /// <c>new T()</c> only when nothing is saved (missing or blank), and throws when saved settings
    /// exist but can't be read (<see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>) or
    /// parsed (<see cref="InvalidDataException"/>), so a later Save can't overwrite them with defaults.
    /// </summary>
    T LoadStrict();

    /// <summary>Async <see cref="LoadStrict"/>.</summary>
    Task<T> LoadStrictAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Non-throwing <see cref="LoadStrict"/>: true with the settings (or <c>new T()</c> when nothing is
    /// saved), false with <c>new T()</c> when saved settings exist but can't be read or parsed. A caller
    /// that gets false must not Save over the file.
    /// </summary>
    bool TryLoad(out T value);

    /// <summary>
    /// First-run entry point: when nothing is saved yet, saves <c>new T()</c> (the class's own
    /// defaults) and returns it; otherwise returns <see cref="Load"/>. Saved settings are never overwritten.
    /// </summary>
    T LoadOrSeed();

    /// <summary>Saves <paramref name="settings"/> atomically.</summary>
    void Save(T settings);

    /// <summary>Async <see cref="Save"/>.</summary>
    Task SaveAsync(T settings, CancellationToken cancellationToken = default);

    /// <summary>Load, mutate, save under one lock; returns the saved instance.</summary>
    T Update(Action<T> mutate);

    /// <summary>Async <see cref="Update"/>.</summary>
    Task<T> UpdateAsync(Func<T, CancellationToken, Task> mutate, CancellationToken cancellationToken = default);
}
