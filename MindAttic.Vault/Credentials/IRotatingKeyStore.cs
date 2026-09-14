namespace MindAttic.Vault.Credentials;

/// <summary>
/// One entry in a provider's key pool: the raw key plus an optional human-readable
/// label (e.g. "Personal", "Team B") shown back in a settings UI.
/// </summary>
/// <param name="Key">The raw API key. Required, non-empty.</param>
/// <param name="Label">Optional display label. <c>null</c> when the caller didn't set one.</param>
public sealed record CredentialPoolEntry(string Key, string? Label = null);

/// <summary>
/// Optional capability for a credential store that can hold MORE THAN ONE key per
/// provider — a pool the caller rotates/fails over through, rather than a single
/// fixed key. Kept as an interface separate from <see cref="ICredentialStore"/>
/// rather than folded into it, since a read-only store like
/// <see cref="ConfigurationCredentialStore"/> has no sensible notion of "a pool of
/// keys" and shouldn't be forced to implement one.
///
/// <para>Every store that DOES implement this keeps the existing single-key
/// <see cref="ICredentialStore.GetKey"/>/<see cref="ICredentialStore.SetKey"/>
/// contract in sync: the first pool entry is always mirrored to the plain
/// <c>apiKey</c> field, so any caller who only knows the old single-key API keeps
/// working unchanged, and a provider with exactly one key never gains an
/// <c>apiKeys</c> array on disk at all.</para>
/// </summary>
public interface IRotatingKeyStore
{
    /// <summary>
    /// Every key currently configured for <paramref name="providerId"/>, in priority
    /// order (the key a caller should try first is index 0).
    /// </summary>
    /// <param name="providerId">Logical provider name (e.g. <c>"claude"</c>). Case-insensitive.</param>
    /// <returns>
    /// An empty list when the provider has no keys configured. A provider with a
    /// single plain <c>apiKey</c> (the common case) yields a one-element list.
    /// </returns>
    IReadOnlyList<CredentialPoolEntry> GetKeys(string providerId);

    /// <summary>
    /// Replaces the entire key pool for <paramref name="providerId"/>, preserving
    /// every other field already on the provider's entry (<c>type</c>, <c>model</c>,
    /// etc.). The first entry becomes the provider's plain <c>apiKey</c> for
    /// back-compat with single-key callers.
    /// </summary>
    /// <param name="providerId">Logical provider name. Required.</param>
    /// <param name="keys">
    /// The new pool, in priority order. Entries with a blank <see cref="CredentialPoolEntry.Key"/>
    /// are dropped. An empty list clears the provider's key (matching
    /// <see cref="ICredentialStore.SetKey"/>'s "empty string clears" behaviour).
    /// </param>
    /// <exception cref="System.ArgumentException">Thrown when <paramref name="providerId"/> is null or whitespace.</exception>
    /// <exception cref="System.NotSupportedException">Thrown by read-only implementations.</exception>
    void SetKeys(string providerId, IReadOnlyList<CredentialPoolEntry> keys);
}
